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
        Click(w, "浅色模式");
    }

    static void Save(Window w, string dir, string name) => w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name));
}
