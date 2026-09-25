using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
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
    public void AutoSubsetReembedsFontsAfterSavingEdits()
    {
        using var server = new FakeFontServer((_, body) => (200, "[]", body + "\n[Fonts]\nfontname: subset.ttf\n"));
        var output = Path.Combine(TempDir(), "out.ass");
        var w = NewWindow(tourDone: true);
        w.FindControl<TextBox>("SubsetServer")!.Text = server.Url;
        w.FindControl<ToggleSwitch>("AutoSubset")!.IsChecked = true;
        Assert.True(AppSettings.Load().AutoSubset); // remembered

        w.ShowResult(FakeResult(output), "none.mkv", "none.mkv");
        var grid = w.FindControl<DataGrid>("Grid")!;
        grid.SelectedItem = grid.ItemsSource!.Cast<Row>().Single(r => r.Text == "b");
        Pump();
        Click(w, "+1 帧");
        Click(w, "保存修改");
        for (int i = 0; i < 100 && !(File.Exists(output) && File.ReadAllText(output).Contains("[Fonts]")); i++) { Thread.Sleep(50); Pump(); }

        var text = File.ReadAllText(output);
        Assert.Contains("0:00:08.04", text); // the edit
        Assert.Contains("[Fonts]", text);    // and fonts embedded again after the save rewrote the file
        Assert.Single(server.Seen);
    }

    /// <summary>Expander, dropdown and tab switch must animate: partly transparent just after, fully opaque once settled.</summary>
    [AvaloniaFact]
    public void ListsAndPanelsAnimateIn()
    {
        var w = NewWindow(tourDone: true);
        double Settled(Visual v) { Thread.Sleep(450); Pump(); return v.Opacity; }
        // lowest opacity seen over the first ~80 ms, sampled every 10 ms
        double JustStarted(Visual v) { double min = v.Opacity; for (int i = 0; i < 8; i++) { Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); min = Math.Min(min, v.Opacity); } return min; }

        // "高级参数" unfolding
        var expander = w.FindControl<Expander>("AdvancedPanel")!;
        expander.IsExpanded = true;
        var content = expander.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_ContentPresenter");
        Assert.InRange(JustStarted(content), 0, 0.9);
        Assert.Equal(1, Settled(content), 3);

        // dropdown list on the tools page
        w.FindControl<TabControl>("Tabs")!.SelectedItem = w.FindControl<TabItem>("TabTools");
        var page = (Visual)((TabItem)w.FindControl<TabControl>("Tabs")!.SelectedItem!).Content!;
        Assert.InRange(JustStarted(page), 0, 0.9); // tab switch fades in too
        Assert.Equal(1, Settled(page), 3);

        var combo = w.FindControl<ComboBox>("FpsFrom")!;
        combo.IsDropDownOpen = true;
        var popup = combo.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup");
        var border = popup.Child!.GetSelfAndVisualDescendants().OfType<Border>().First(b => b.Name == "PopupBorder");
        Assert.InRange(JustStarted(border), 0, 0.9);
        Assert.Equal(1, Settled(border), 3);
        combo.IsDropDownOpen = false;
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
        Assert.Equal(9, tour2.Index);
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
        w.FindControl<TabControl>("Tabs")!.SelectedItem = w.FindControl<TabItem>("TabTools");
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


    [AvaloniaFact]
    public void RenamePageMatchesDroppedFolders()
    {
        var w = NewWindow(tourDone: true);
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "[BD] Show - 01.mkv"), "");
        File.WriteAllText(Path.Combine(dir, "CHS_Show_第1集_Viu.srt"), "sc");
        w.FindControl<TabControl>("Tabs")!.SelectedItem = w.FindControl<TabItem>("TabSubs");
        Pump();
        w.AcceptPaths([dir]);
        Assert.Equal(dir, w.FindControl<TextBox>("RenVideos")!.Text);
        Click(w, "预览");
        Assert.Contains("[BD] Show - 01.sc.srt", w.FindControl<SelectableTextBlock>("RenLog")!.Text);
        Click(w, "改名");
        Assert.Equal("sc", File.ReadAllText(Path.Combine(dir, "[BD] Show - 01.sc.srt")));
        Assert.True(File.Exists(Path.Combine(dir, "字幕备份", "CHS_Show_第1集_Viu.srt")));

        if (Environment.GetEnvironmentVariable("SUBMATCHER_SHOTS") is { } shots)
        {
            Pump();
            Save(w, shots, "subs-page.png");
        }
    }

    [AvaloniaFact]
    public void SearchResultsExpandAndCollapse()
    {
        var w = NewWindow(tourDone: true);
        w.FindControl<TabControl>("Tabs")!.SelectedItem = w.FindControl<TabItem>("TabSubs");
        Pump();
        w.ShowGroups(TgSubs.Group([
            new(1, "CHS 葬送的芙莉莲 01_meWATCH.srt", 1), new(2, "CHT 葬送的芙莉莲 01_meWATCH.srt", 1),
            new(3, "葬送的芙莉莲_EP01.BG.zh-Hans.srt", 1), new(4, "葬送的芙莉莲_EP01.BG.zh-Hant.srt", 1),
        ]));
        Pump();
        var list = w.FindControl<ListBox>("TgGroups")!;
        var items = ((IEnumerable<Control>)list.ItemsSource!).Cast<StackPanel>().ToList();
        Assert.Equal(2, items.Count);
        bool Open(StackPanel p) => p.Children[1].IsVisible;
        Button Fold(StackPanel p) => (Button)((Grid)p.Children[0]).Children[1];
        void ClickAt(Visual v, double x, double y)
        {
            var pt = v.TranslatePoint(new Avalonia.Point(x, y), w)!.Value;
            w.MouseDown(pt, MouseButton.Left); w.MouseUp(pt, MouseButton.Left); Pump();
        }
        Assert.True(Open(items[0]));   // first result opens with its file list
        Assert.Contains("CHS 葬送的芙莉莲 01_meWATCH.srt", ((SelectableTextBlock)items[0].Children[1].GetVisualDescendants().OfType<SelectableTextBlock>().First()).Text);
        ClickAt(Fold(items[0]), 5, 5); // 收起
        Assert.False(Open(items[0]));
        Assert.Equal(0, list.SelectedIndex); // still the download target
        ClickAt(items[0], 10, 8);      // tap the folded row → open again
        Assert.True(Open(items[0]));
        ClickAt(items[1], 10, 8);      // another row: it opens, the first folds
        Assert.Equal(1, list.SelectedIndex);
        Assert.True(Open(items[1]));
        Assert.False(Open(items[0]));
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

            // 下载与改名: a search result, a finished download and a rename preview
            w.FindControl<TabControl>("Tabs")!.SelectedItem = w.FindControl<TabItem>("TabSubs");
            w.FindControl<TextBlock>("TgStatus")!.Text = "已登录";
            w.FindControl<Button>("TgLoginBtn")!.Content = "退出登录";
            w.FindControl<TextBox>("TgQuery")!.Text = "秘密基地";
            // a realistic result: 12 episodes on 4 platforms, the first one expanded
            List<TgFile> files = [];
            foreach (var (plat, title, n) in new[] { ("Bilibili", "秘密基地", 12), ("Crunchyroll", "秘密基地", 12), ("iQIYI", "祕密基地", 12), ("Viu", "秘密基地", 12) })
                for (int ep = 1; ep <= n; ep++)
                    if (plat != "Viu" || ep != 8) // one missing episode, to show the gap in the summary
                    foreach (var lang in new[] { "CHS", "CHT" })
                        files.Add(new(files.Count, $"{lang}_{title}_第{ep}集_{plat}.srt", 30000));
            w.ShowGroups(TgSubs.Group(files));
            w.FindControl<ListBox>("TgGroups")!.SelectedIndex = 3; // Viu, the one with a missing episode
            w.FindControl<TextBlock>("TgLog")!.Text = @"已下载 12 个 → D:\Anime\字幕下载\秘密基地 [Bilibili]";
            w.FindControl<TextBox>("RenVideos")!.Text = @"D:\Anime\[BD] 秘密基地";
            w.FindControl<TextBox>("RenSubs")!.Text = @"D:\Anime\字幕下载\秘密基地 [Bilibili]";
            w.FindControl<SelectableTextBlock>("RenLog")!.Text = string.Join("\n", Enumerable.Range(1, 6).Select(i =>
                $"CHS_秘密基地_第{i}集_Bilibili.subset.ass\n    → [BD] 秘密基地 [{i:00}][Ma10p_1080p].sc.ass"));
            Thread.Sleep(600); Pump(); Thread.Sleep(300); Pump(); // let the tab fade-in finish
            Save(w, dir, $"subs-{theme}.png");
            if (theme == "dark") Click(w, "浅色模式");
        }
    }
}
