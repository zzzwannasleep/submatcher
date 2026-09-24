using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SubMatcher.Core;
using SubMatcher.Gui;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(SubMatcher.Tests.TestApp))]

namespace SubMatcher.Tests;

public static class TestApp
{
    // Real Skia rendering (not the stub renderer) so screenshots can be captured.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class GuiTests
{
    static string TempDir() => Directory.CreateTempSubdirectory("submatcher").FullName;

    /// <summary>Fresh settings file per test so the first-run tour state is deterministic.</summary>
    static MainWindow NewWindow(bool tourDone)
    {
        AppSettings.FilePath = Path.Combine(TempDir(), "settings.json");
        if (tourDone) new AppSettings { TourDone = true }.Save();
        var w = new MainWindow { Width = 1280, Height = 820 };
        w.Show();
        Pump();
        return w;
    }

    static void Pump()
    {
        for (int i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    static SyncResult FakeResult(string output)
    {
        var doc = SubtitleDoc.Parse("[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,D,,0,0,0,,第一句\nDialogue: 0,0:00:05.00,0:00:06.00,D,,0,0,0,,b\n" +
                                    "Dialogue: 0,0:00:09.00,0:00:10.00,D,,0,0,0,,被删掉的镜头\n", SubFormat.Ass);
        var events = doc.Events.Select((e, i) => new EventResult
        {
            Index = i, OldStart = e.Start, OldEnd = e.End, NewStart = e.Start + 3000, NewEnd = e.End + 3000, ShiftFrames = 72, Text = e.Text,
            Cost = 0.03, Status = i == 2 ? MatchStatus.Inherited : MatchStatus.Ok,
        }).ToList();
        foreach (var (e, r) in doc.Events.Zip(events)) { e.Start = r.NewStart; e.End = r.NewEnd; }
        return new SyncResult { Events = events, Doc = doc, OutputPath = output, Fps = 24, CheckLog = "" };
    }

    static void Click(Visual root, string content)
    {
        var b = root.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == content && b.IsEffectivelyVisible);
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    [AvaloniaFact]
    public void NudgeUpdatesGridAndSaves()
    {
        var output = Path.Combine(TempDir(), "out.ass");
        var w = NewWindow(tourDone: true);
        w.ShowResult(FakeResult(output), "none.mkv", "none.mkv");
        var grid = w.FindControl<DataGrid>("Grid")!;
        var row = grid.ItemsSource!.Cast<Row>().Single(r => r.Text == "b");
        grid.SelectedItem = row;
        Pump();

        Click(w, "+1 帧");
        Assert.Equal("0:00:08.04", row.NewStart);
        Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "0:00:08.04"); // the cell itself re-rendered

        Click(w, "保存修改");
        Assert.Contains("Dialogue: 0,0:00:08.04,0:00:09.04,D,,0,0,0,,b", File.ReadAllText(output));
        Assert.False(w.FindControl<Button>("SaveBtn")!.IsEnabled);
    }

    [AvaloniaFact]
    public void TourRunsOnFirstLaunchCanBeSkippedAndReplayed()
    {
        var w = NewWindow(tourDone: false);
        var tour = w.FindControl<TourOverlay>("Tour")!;
        Assert.True(tour.IsOpen); // first launch

        Click(tour, "跳过引导");
        Assert.False(tour.IsOpen);
        Assert.True(AppSettings.Load().TourDone); // remembered, portable settings.json
        Assert.False(w.FindControl<Control>("ResultsPanel")!.IsVisible); // placeholder frame put back

        // Second launch with the same settings: no tour.
        var w2 = new MainWindow();
        w2.Show();
        Pump();
        Assert.False(w2.FindControl<TourOverlay>("Tour")!.IsOpen);

        // Replay from the top bar, walk every step with buttons and keys, finish on the last one.
        Click(w2, "使用引导");
        var tour2 = w2.FindControl<TourOverlay>("Tour")!;
        Assert.True(tour2.IsOpen);
        Click(tour2, "下一步");
        Assert.Equal(1, tour2.Index);
        tour2.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        Assert.Equal(2, tour2.Index);
        Click(tour2, "上一步");
        Assert.Equal(1, tour2.Index);
        while (tour2.IsOpen) Click(tour2, tour2.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "完成") ? "完成" : "下一步");
        Assert.Equal(8, tour2.Index);
    }

    /// <summary>Renders the main screens to PNG when SUBMATCHER_SHOTS is set — a visual check without touching a real desktop.</summary>
    [AvaloniaFact]
    public void Screenshots()
    {
        var dir = Environment.GetEnvironmentVariable("SUBMATCHER_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        var w = NewWindow(tourDone: false);
        var tour = w.FindControl<TourOverlay>("Tour")!;
        Save(w, dir, "1-tour-welcome.png");
        Click(tour, "下一步");
        Thread.Sleep(350); Pump();
        Save(w, dir, "2-tour-files.png");
        Click(tour, "下一步"); Click(tour, "下一步"); Click(tour, "下一步");
        Thread.Sleep(350); Pump();
        Save(w, dir, "3-tour-results.png");
        Click(tour, "跳过引导");

        w.ShowResult(FakeResult(Path.Combine(TempDir(), "o.ass")), "none.mkv", "none.mkv");
        Thread.Sleep(600); Pump(); Thread.Sleep(300); Pump();
        Save(w, dir, "4-results-light.png");
        Click(w, "深色模式");
        Thread.Sleep(300); Pump();
        Save(w, dir, "5-results-dark.png");
        w.FindControl<TabControl>("Tabs")!.SelectedIndex = 2;
        Pump();
        Save(w, dir, "6-tools-dark.png");
        if (Environment.GetEnvironmentVariable("SUBMATCHER_SUBSET_SAMPLE") is { Length: > 0 } sample)
        {
            // Opt-in live check against the real FontInAss server (network), never in normal test runs.
            w.FindControl<TextBox>("SubsetInput")!.Text = sample;
            Click(w, "子集化");
            for (int i = 0; i < 100 && !w.FindControl<Button>("SubsetBtn")!.IsEnabled; i++) { Thread.Sleep(200); Pump(); }
            w.FindControl<Border>("SubsetCard")!.BringIntoView();
            Thread.Sleep(300); Pump();
            Save(w, dir, "6b-subset.png");
        }
        Click(w, "浅色模式");

        // Advanced options expanded: the left column overflows and grows a scrollbar.
        w.FindControl<TabControl>("Tabs")!.SelectedIndex = 0;
        w.FindControl<Expander>("AdvancedPanel")!.IsExpanded = true;
        w.Height = 700;
        Thread.Sleep(400); Pump(); Thread.Sleep(300); Pump();
        Save(w, dir, "7-advanced.png");
    }

    static void Save(Window w, string dir, string name) => w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name));

    /// <summary>The README screenshot: a realistic episode's worth of rows, one selected, real frames in the preview.</summary>
    [AvaloniaFact]
    public void ReadmeScreenshot()
    {
        var dir = Environment.GetEnvironmentVariable("SUBMATCHER_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        string[] lines =
        [
            "早上好，今天也要加油哦", "你又迟到了", "抱歉抱歉，路上电车停了", "借口倒是很多嘛", "这次是真的！", "好啦好啦，快进来吧",
            "社团活动今天几点开始？", "四点，别忘了带乐谱", "我昨天练到很晚", "那首曲子果然还是好难", "副歌那段总是跟不上", "要不要放慢一点试试",
            "（广播）请各位同学注意", "……刚才那是什么声音", "天台那边好像有人", "我们去看看吧", "等一下，老师说不能上去", "就看一眼",
            "门没锁", "风好大", "你看，从这里能看到整个小镇", "原来你一直一个人来这里啊", "嗯，这是我的秘密基地", "现在也是你的了",
        ];
        var sb = new System.Text.StringBuilder("[Events]\n");
        for (int i = 0; i < lines.Length; i++)
            sb.Append($"Dialogue: 0,{SubtitleDoc.FormatAssTime(2000 + i * 4200)},{SubtitleDoc.FormatAssTime(4800 + i * 4200)},D,,0,0,0,,{lines[i]}\n");
        var doc = SubtitleDoc.Parse(sb.ToString(), SubFormat.Ass);
        var events = doc.Events.Select((e, i) =>
        {
            int frames = i < 13 ? 72 : -48;
            var status = i == 12 ? MatchStatus.Inherited : i == 15 ? MatchStatus.Static : MatchStatus.Ok;
            long shift = (long)Math.Round(frames * 1001 / 24.0);
            return new EventResult
            {
                Index = i, OldStart = e.Start, OldEnd = e.End, NewStart = e.Start + shift, NewEnd = e.End + shift, ShiftFrames = frames, Text = e.Text,
                Cost = status == MatchStatus.Inherited ? 0.93 : 0.02 + i % 5 * 0.01, Status = status,
            };
        }).ToList();
        var result = new SyncResult { Events = events, Doc = doc, OutputPath = Path.Combine(TempDir(), "o.ass"), Fps = 24000 / 1001.0, CheckLog = "" };

        foreach (var theme in new[] { "light", "dark" })
        {
            var w = NewWindow(tourDone: true);
            if (theme == "dark") Click(w, "深色模式");
            w.FindControl<TextBox>("SrcVideo")!.Text = @"D:\Anime\[TV] 秘密基地 - 01 [1080p].mkv";
            w.FindControl<TextBox>("SrcSub")!.Text = @"D:\Anime\[TV] 秘密基地 - 01 [1080p].sc.ass";
            w.FindControl<TextBox>("DstVideo")!.Text = @"D:\Anime\[BD] 秘密基地 [01][Ma10p_1080p].mkv";
            w.ShowResult(result, "none.mkv", "none.mkv");
            var grid = w.FindControl<DataGrid>("Grid")!;
            grid.SelectedItem = grid.ItemsSource!.Cast<Row>().ElementAt(10);
            Pump();
            if (Environment.GetEnvironmentVariable("SUBMATCHER_SHOT_FRAMES") is { Length: > 0 } frames)
            {
                w.SrcImage.Source = new Avalonia.Media.Imaging.Bitmap(Path.Combine(frames, "src.png"));
                w.DstImage.Source = new Avalonia.Media.Imaging.Bitmap(Path.Combine(frames, "dst.png"));
            }
            Thread.Sleep(600); Pump(); Thread.Sleep(300); Pump();
            Save(w, dir, $"readme-{theme}.png");
            if (theme == "dark") Click(w, "浅色模式");
        }
    }
}
