using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class GuiTests
{
    [AvaloniaFact]
    public void NudgeUpdatesGridAndSaves()
    {
        var dir = Directory.CreateTempSubdirectory("submatcher").FullName;
        var output = Path.Combine(dir, "out.ass");
        var doc = SubtitleDoc.Parse("[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,D,,0,0,0,,a\nDialogue: 0,0:00:05.00,0:00:06.00,D,,0,0,0,,b\n", SubFormat.Ass);
        var events = doc.Events.Select((e, i) => new EventResult
        {
            Index = i, OldStart = e.Start, OldEnd = e.End, NewStart = e.Start + 3000, NewEnd = e.End + 3000, ShiftFrames = 72, Text = e.Text,
        }).ToList();
        foreach (var (e, r) in doc.Events.Zip(events)) { e.Start = r.NewStart; e.End = r.NewEnd; }

        var w = new MainWindow();
        w.Show();
        w.ShowResult(new SyncResult { Events = events, Doc = doc, OutputPath = output, Fps = 24, CheckLog = "" }, "none.mkv", "none.mkv");
        var grid = w.FindControl<DataGrid>("Grid")!;
        var row = grid.ItemsSource!.Cast<Row>().Single(r => r.Text == "b");
        grid.SelectedItem = row;
        Dispatcher.UIThread.RunJobs();

        Click(w, "+1 帧");
        Assert.Equal("0:00:08.04", row.NewStart);
        Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "0:00:08.04"); // the cell itself re-rendered

        Click(w, "保存修改");
        Assert.Contains("Dialogue: 0,0:00:08.04,0:00:09.04,D,,0,0,0,,b", File.ReadAllText(output));
        Assert.False(w.FindControl<Button>("SaveBtn")!.IsEnabled);
    }

    static void Click(Window w, string content)
    {
        var b = w.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == content);
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
}
