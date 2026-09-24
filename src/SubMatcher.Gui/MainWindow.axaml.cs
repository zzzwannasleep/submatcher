using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SubMatcher.Core;

namespace SubMatcher.Gui;

public sealed class Row(EventResult r, double fps) : INotifyPropertyChanged
{
    public EventResult R { get; } = r;
    public int Number => R.Index + 1;
    public string OldStart => SubtitleDoc.FormatAssTime(R.OldStart);
    public string NewStart => SubtitleDoc.FormatAssTime(R.NewStart);
    public string Shift => $"{R.ShiftMs / 1000.0:+0.000;-0.000}";
    public string Cost => R.Cost.ToString("0.000");
    public string StatusText => (R.NeedsCheck ? "⚠ " : "") + Sync.StatusText(R.Status) + (R.IsComment ? " · 注释" : "");
    public string Text => R.Text;
    public double Fps => fps;

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        foreach (var p in new[] { nameof(NewStart), nameof(Shift), nameof(StatusText) }) PropertyChanged?.Invoke(this, new(p));
    }
}

public partial class MainWindow : Window
{
    public static string[] CommonFps { get; } = ["23.976", "24", "25", "29.97", "30", "50", "59.94", "60"];

    static readonly FilePickerFileType VideoType = new("视频") { Patterns = Sync.VideoExts.Select(e => "*" + e).ToArray() };
    static readonly FilePickerFileType SubType = new("字幕") { Patterns = Sync.SubExts.Select(e => "*" + e).ToArray() };

    CancellationTokenSource? _cts, _previewCts;
    SyncResult? _result;
    string _resultSrc = "", _resultDst = "";
    readonly ObservableCollection<Row> _rows = [];
    bool _dirty;

    public MainWindow() : this([]) { }

    /// <summary>Paths passed on the command line (e.g. files dropped onto the exe) are handled like a window drop.</summary>
    public MainWindow(string[] paths)
    {
        InitializeComponent();
        if (paths.Length > 0) Tabs.SelectedIndex = paths.All(Directory.Exists) ? 1 : 0;
        AcceptPaths(paths);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Tabs.SelectionChanged += (_, _) => { if (Tabs.SelectedIndex == 2) UpdateCacheInfo(); };
    }

    // ---------------- drag & drop / pickers ----------------

    void OnDrop(object? sender, DragEventArgs e) => AcceptPaths(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? []);

    void AcceptPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            switch (Tabs.SelectedIndex)
            {
                case 1 when Directory.Exists(p):
                    (string.IsNullOrWhiteSpace(BatchSrc.Text) ? BatchSrc : BatchDst).Text = p;
                    break;
                case 2 when Sync.IsSub(p):
                    ToolSub.Text = p;
                    break;
                case 0 when Sync.IsSub(p):
                    SrcSub.Text = p;
                    break;
                case 0 when Sync.IsVideo(p):
                    // First video → source; after that → target. A source with a same-named subtitle next to it gets it picked up too.
                    if (string.IsNullOrWhiteSpace(SrcVideo.Text)) { SrcVideo.Text = p; AutoPickSub(p); }
                    else DstVideo.Text = p;
                    break;
            }
        }
    }

    void AutoPickSub(string video)
    {
        if (!string.IsNullOrWhiteSpace(SrcSub.Text)) return;
        var dir = Path.GetDirectoryName(video)!;
        var b = Path.GetFileNameWithoutExtension(video) + ".";
        SrcSub.Text = Directory.GetFiles(dir).Where(Sync.IsSub).FirstOrDefault(s => Path.GetFileName(s).StartsWith(b, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    async Task<string?> PickFile(params FilePickerFileType[] types)
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { FileTypeFilter = [.. types, FilePickerFileTypes.All] });
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    }

    async void BrowseVideo(object? sender, RoutedEventArgs e)
    {
        if (await PickFile(VideoType) is not { } p) return;
        if ((string?)((Button)sender!).Tag == "SrcVideo") { SrcVideo.Text = p; AutoPickSub(p); } else DstVideo.Text = p;
    }

    async void BrowseSrcSub(object? sender, RoutedEventArgs e) { if (await PickFile(SubType) is { } p) SrcSub.Text = p; }
    async void BrowseToolSub(object? sender, RoutedEventArgs e) { if (await PickFile(SubType) is { } p) ToolSub.Text = p; }

    async void BrowseOut(object? sender, RoutedEventArgs e)
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { DefaultExtension = "ass", FileTypeChoices = [SubType] });
        if (f?.TryGetLocalPath() is { } p) OutSub.Text = p;
    }

    async void BrowseFolder(object? sender, RoutedEventArgs e)
    {
        var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
        if (r.Count == 0 || r[0].TryGetLocalPath() is not { } p) return;
        ((TextBox)this.FindControl<TextBox>((string)((Button)sender!).Tag!)!).Text = p;
    }

    // ---------------- sync ----------------

    SyncOptions ReadOptions() => new()
    {
        WindowSeconds = (double)(OptWindow.Value ?? 10),
        AnalysisFps = (double)(OptFps.Value ?? 0),
        MaxCost = (double)(OptMaxCost.Value ?? 0.4m),
        WarnCost = (double)(OptWarnCost.Value ?? 0.2m),
        MinLineSeconds = (double)(OptMinLine.Value ?? 1.5m),
        SubtitleStream = (int)(OptStream.Value ?? 0),
        SnapToCuts = OptSnap.IsChecked == true,
        HwAccel = OptHw.IsChecked == true,
        UseCache = OptCache.IsChecked == true,
    };

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Trim('"');

    void SetBusy(bool busy)
    {
        RunBtn.IsEnabled = BatchRunBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = BatchCancelBtn.IsEnabled = busy;
    }

    async void RunSync(object? sender, RoutedEventArgs e)
    {
        var src = Blank(SrcVideo.Text);
        var dst = Blank(DstVideo.Text);
        if (src == null || dst == null) { Status.Text = "请先选择源视频和目标视频"; return; }
        if (_dirty && !await Confirm("上一次的手动修改还没保存，丢弃吗？")) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        _rows.Clear();
        OpenLogBtn.IsEnabled = SaveBtn.IsEnabled = false;
        var sw = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<SyncProgress>(p => { Progress.Value = p.Fraction; Status.Text = $"{p.Stage}… {p.Fraction * 100:0}%"; });
            // Read every control here on the UI thread; the lambda runs on the pool.
            var (sub, output, o, ct) = (Blank(SrcSub.Text), Blank(OutSub.Text), ReadOptions(), _cts.Token);
            _result = await Task.Run(() => Sync.Run(src, sub, dst, output, o, progress, ct));
            ShowResult(_result, src, dst);
            File.WriteAllText(Path.ChangeExtension(_result.OutputPath, ".check.log"), _result.CheckLog, new UTF8Encoding(true));
            OpenLogBtn.IsEnabled = true;
            Status.Text = $"完成（{sw.Elapsed.TotalSeconds:0.0}s）：{_result.Events.Count} 行，需检查 {_result.NeedsCheckCount} 行 → {_result.OutputPath}";
        }
        catch (OperationCanceledException) { Status.Text = "已取消"; }
        catch (Exception ex) { Status.Text = "失败：" + ex.Message; }
        finally { SetBusy(false); Progress.Value = 0; }
    }

    internal void ShowResult(SyncResult result, string src, string dst)
    {
        _result = result; _resultSrc = src; _resultDst = dst; _dirty = false;
        _rows.Clear();
        foreach (var r in result.Events.OrderBy(r => r.OldStart)) _rows.Add(new Row(r, result.Fps));
        ApplyFilter();
    }

    void Cancel(object? sender, RoutedEventArgs e) => _cts?.Cancel();

    void FilterChanged(object? sender, RoutedEventArgs e) => ApplyFilter();

    void ApplyFilter() => Grid.ItemsSource = OnlyCheck.IsChecked == true ? new ObservableCollection<Row>(_rows.Where(r => r.R.NeedsCheck)) : _rows;

    async void ShowCheckLog(object? sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var box = new TextBox { Text = _result.CheckLog, IsReadOnly = true, AcceptsReturn = true, FontFamily = "Consolas,Menlo,monospace", FontSize = 12 };
        await new Window { Title = "检查日志", Width = 900, Height = 600, Content = box }.ShowDialog(this);
    }

    // ---------------- preview & manual fix ----------------

    async void RowSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is not Row row) return;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        double mid = (row.R.OldEnd - row.R.OldStart) / 2000.0;
        double ts = row.R.OldStart / 1000.0 + mid, td = row.R.NewStart / 1000.0 + mid;
        SrcCaption.Text = $"源 @ {SubtitleDoc.FormatAssTime((long)(ts * 1000))}";
        DstCaption.Text = $"目标 @ {SubtitleDoc.FormatAssTime((long)(td * 1000))}";
        try
        {
            var a = FFmpeg.GrabFrame(_resultSrc, ts, 480, cts.Token);
            var b = FFmpeg.GrabFrame(_resultDst, td, 480, cts.Token);
            var (ia, ib) = (await a, await b);
            if (cts.IsCancellationRequested) return;
            SrcImage.Source = new Bitmap(new MemoryStream(ia));
            DstImage.Source = new Bitmap(new MemoryStream(ib));
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
    }

    void Nudge(object? sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender!).Tag!;
        foreach (var row in SelectedRows())
        {
            long delta = tag.EndsWith('s')
                ? (long)(double.Parse(tag[..^1], CultureInfo.InvariantCulture) * 1000)
                : (long)Math.Round(int.Parse(tag) * 1000 / row.Fps);
            Move(row, delta);
        }
        RowSelected(null, null!);
    }

    void CopyPrevShift(object? sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows())
        {
            int i = _rows.IndexOf(row);
            var prev = _rows.Take(i).LastOrDefault(r => !SelectedRows().Contains(r));
            if (prev != null) Move(row, prev.R.ShiftMs - row.R.ShiftMs);
        }
        RowSelected(null, null!);
    }

    List<Row> SelectedRows() => Grid.SelectedItems.OfType<Row>().ToList();

    void Move(Row row, long delta)
    {
        if (_result == null || delta == 0) return;
        row.R.NewStart += delta;
        row.R.NewEnd += delta;
        var ev = _result.Doc.Events[row.R.Index];
        ev.Start = row.R.NewStart;
        ev.End = row.R.NewEnd;
        row.Refresh();
        _dirty = SaveBtn.IsEnabled = true;
    }

    void SaveEdits(object? sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        try
        {
            _result.Doc.Save(_result.OutputPath);
            _dirty = SaveBtn.IsEnabled = false;
            Status.Text = "已保存 → " + _result.OutputPath;
        }
        catch (IOException ex) { Status.Text = "保存失败：" + ex.Message; }
    }

    async Task<bool> Confirm(string msg)
    {
        var ok = false;
        var dlg = new Window { Title = "SubMatcher", Width = 360, Height = 140, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var yes = new Button { Content = "确定", Classes = { "accent" } };
        var no = new Button { Content = "取消" };
        yes.Click += (_, _) => { ok = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new(16), Spacing = 16,
            Children = { new TextBlock { Text = msg, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                         new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { yes, no } } },
        };
        await dlg.ShowDialog(this);
        return ok;
    }

    // ---------------- batch ----------------

    void Log(string s) => Dispatcher.UIThread.Post(() => { BatchLog.Text += s + Environment.NewLine; BatchLog.CaretIndex = BatchLog.Text.Length; });

    List<Sync.BatchPair>? Pairs()
    {
        var s = Blank(BatchSrc.Text);
        var d = Blank(BatchDst.Text);
        if (s == null || d == null || !Directory.Exists(s) || !Directory.Exists(d)) { BatchLog.Text = "请先选择存在的源目录和目标目录\n"; return null; }
        return Sync.PairFolders(s, d);
    }

    void ScanBatch(object? sender, RoutedEventArgs e)
    {
        if (Pairs() is not { } pairs) return;
        BatchLog.Text = $"配对 {pairs.Count} 组：\n";
        foreach (var p in pairs)
            Log($"{Path.GetFileName(p.SrcVideo)}  →  {Path.GetFileName(p.DstVideo)}\n    字幕: {(p.Subs.Count == 0 ? "(内封)" : string.Join(", ", p.Subs.Select(Path.GetFileName)))}");
    }

    async void RunBatch(object? sender, RoutedEventArgs e)
    {
        if (Pairs() is not { } pairs) return;
        if (pairs.Count == 0) { BatchLog.Text = "没有能配对的视频\n"; return; }
        var o = ReadOptions();
        var outDir = Blank(BatchOut.Text);
        _cts = new CancellationTokenSource();
        SetBusy(true);
        BatchLog.Text = "";
        int ok = 0, fail = 0, total = pairs.Sum(p => Math.Max(1, p.Subs.Count)), done = 0;
        try
        {
            foreach (var p in pairs)
            {
                foreach (var sub in p.Subs.Count > 0 ? p.Subs.Cast<string?>() : [null])
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    Log($"== {Path.GetFileName(p.SrcVideo)} [{(sub == null ? "内封" : Path.GetFileName(sub))}] → {Path.GetFileName(p.DstVideo)}");
                    string? output = outDir == null ? null : Path.Combine(outDir, Path.GetFileName(Sync.DefaultOutput(p.SrcVideo, sub, p.DstVideo)));
                    int n = done;
                    var progress = new Progress<SyncProgress>(x => BatchProgress.Value = (n + x.Fraction) / total);
                    try
                    {
                        var r = await Task.Run(() => Sync.Run(p.SrcVideo, sub, p.DstVideo, output, o, progress, _cts.Token));
                        File.WriteAllText(Path.ChangeExtension(r.OutputPath, ".check.log"), r.CheckLog, new UTF8Encoding(true));
                        Log($"   ✓ {r.Events.Count} 行，需检查 {r.NeedsCheckCount} 行 → {r.OutputPath}");
                        ok++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log("   ✗ " + ex.Message); fail++; }
                    done++;
                }
            }
            Log($"\n完成：成功 {ok}，失败 {fail}");
        }
        catch (OperationCanceledException) { Log("已取消"); }
        finally { SetBusy(false); BatchProgress.Value = 0; }
    }

    // ---------------- tools ----------------

    SubtitleDoc? LoadToolSub(out string path)
    {
        path = Blank(ToolSub.Text) ?? "";
        if (!File.Exists(path)) { ToolStatus.Text = "请先选择字幕文件"; return null; }
        return SubtitleDoc.Load(path);
    }

    static string Tagged(string path, string tag) =>
        Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + tag + Path.GetExtension(path));

    void ToolShift(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (LoadToolSub(out var path) is not { } doc) return;
            long offset = Blank(ShiftOffset.Text) is { } o ? SubtitleDoc.ParseFlexibleTime(o)
                : Blank(ShiftFrom.Text) is { } f && Blank(ShiftTo.Text) is { } t ? SubtitleDoc.ParseFlexibleTime(t) - SubtitleDoc.ParseFlexibleTime(f)
                : throw new FormatException("填写偏移，或同时填写原时间和目标时间");
            long from = Blank(RangeStart.Text) is { } rs ? SubtitleDoc.ParseFlexibleTime(rs) : long.MinValue;
            long to = Blank(RangeEnd.Text) is { } re ? SubtitleDoc.ParseFlexibleTime(re) : long.MaxValue;
            int n = Tools.Shift(doc, offset, from, to);
            var outPath = Tagged(path, ".shifted");
            doc.Save(outPath);
            ToolStatus.Text = $"平移 {offset / 1000.0:+0.000;-0.000}s，{n} 行 → {outPath}";
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException) { ToolStatus.Text = "失败：" + ex.Message; }
    }

    void ToolFps(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (LoadToolSub(out var path) is not { } doc) return;
            double a = double.Parse(FpsFrom.Text ?? "", CultureInfo.InvariantCulture), b = double.Parse(FpsTo.Text ?? "", CultureInfo.InvariantCulture);
            Tools.ConvertFps(doc, a, b);
            var outPath = Tagged(path, ".fps");
            doc.Save(outPath);
            ToolStatus.Text = $"{a} → {b} fps → {outPath}";
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException) { ToolStatus.Text = "失败：" + ex.Message; }
    }

    void ToolEncode(object? sender, RoutedEventArgs e)
    {
        var path = Blank(ToolSub.Text);
        if (path == null || !File.Exists(path)) { ToolStatus.Text = "请先选择字幕文件"; return; }
        try
        {
            var text = TextEncoding.Decode(File.ReadAllBytes(path), out var enc);
            File.WriteAllText(path, text, new UTF8Encoding(true));
            ToolStatus.Text = $"{enc.WebName} → UTF-8 BOM：{path}";
        }
        catch (IOException ex) { ToolStatus.Text = "失败：" + ex.Message; }
    }

    void UpdateCacheInfo()
    {
        var dir = new DirectoryInfo(Fingerprint.CacheDir);
        long bytes = dir.Exists ? dir.GetFiles().Sum(f => f.Length) : 0;
        CacheInfo.Text = $"{dir.FullName}  ({bytes / 1048576.0:0.0} MB)";
    }

    void ClearCache(object? sender, RoutedEventArgs e)
    {
        try { if (Directory.Exists(Fingerprint.CacheDir)) Directory.Delete(Fingerprint.CacheDir, true); }
        catch (IOException ex) { ToolStatus.Text = "失败：" + ex.Message; }
        UpdateCacheInfo();
    }
}
