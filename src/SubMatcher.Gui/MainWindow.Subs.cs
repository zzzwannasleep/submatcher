using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SubMatcher.Core;

namespace SubMatcher.Gui;

// 「下载与改名」page: Telegram subtitle channel search/download + SubRenamer-style auto rename.
public partial class MainWindow
{
    TgClient? _tg;
    string? _tgProxy;
    List<TgGroup> _groups = [];

    // ---------------- Telegram ----------------

    /// <summary>The logged-in client, or null. <paramref name="interactive"/> shows the QR login when there is no session.</summary>
    async Task<TgClient?> TgEnsure(bool interactive)
    {
        var proxy = TgProxy.Text?.Trim() ?? "";
        if (_tg != null && proxy != _tgProxy) { _tg.Dispose(); _tg = null; }
        if (_tg?.LoggedIn == true) return _tg;
        _settings.TgProxy = proxy;
        _settings.Save();
        try
        {
            _tg ??= new TgClient(proxy, AskTgPassword);
            _tgProxy = proxy;
            TgStatus.Text = "正在连接 Telegram…";
            var tg = _tg;
            if (!await Task.Run(tg.Resume) && interactive) await ShowLogin(tg);
            UpdateTgStatus();
            return _tg?.LoggedIn == true ? _tg : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _tg?.Dispose();
            _tg = null;
            TgStatus.Text = "连接失败：" + e.Message + "（连不上的话在「设置」里填代理）";
            return null;
        }
        catch (OperationCanceledException) { UpdateTgStatus(); return null; }
    }

    void UpdateTgStatus()
    {
        bool on = _tg?.LoggedIn == true;
        TgStatus.Text = on ? $"已登录 {_tg!.UserName}" : "未登录（检索和下载需要扫码登录一次）";
        TgLoginBtn.Content = on ? "退出登录" : "扫码登录";
    }

    async void TgLogin(object? sender, RoutedEventArgs e)
    {
        if (_tg?.LoggedIn == true)
        {
            if (!await Confirm("退出 Telegram 登录？下次检索需要重新扫码。", "退出")) return;
            _tg.Dispose();
            _tg = null;
            try { TgClient.Logout(); } catch (IOException) { }
            UpdateTgStatus();
            return;
        }
        TgLoginBtn.IsEnabled = false;
        try { await TgEnsure(true); }
        finally { TgLoginBtn.IsEnabled = true; }
    }

    /// <summary>Login dialog: QR code (refreshable) or phone number + code; 2FA password when the account has one.</summary>
    async Task ShowLogin(TgClient tg)
    {
        static TextBlock Hint(string t) => new() { Text = t, Classes = { "hint" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var status = new SelectableTextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12, MaxHeight = 220 };
        var img = new Image { Width = 220, Height = 220 };
        var refresh = new Button { Content = "刷新二维码", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var phone = new TextBox { Watermark = "手机号，带国家码：+8613800000000" };
        var code = new TextBox { Watermark = "验证码", IsVisible = false };
        var pwd = new TextBox { Watermark = "两步验证密码", PasswordChar = '•', IsVisible = false };
        var next = new Button { Content = "发送验证码", Theme = this.FindResource("SolidButton") as Avalonia.Styling.ControlTheme, Classes = { "Primary" }, IsDefault = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var qrPage = new TabItem
        {
            Header = "扫码",
            Content = new StackPanel
            {
                Spacing = 10, Margin = new(0, 12, 0, 0),
                Children =
                {
                    new Border { Background = Avalonia.Media.Brushes.White, Padding = new(8), CornerRadius = new(8), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Child = img },
                    Hint("手机 Telegram → 设置 → 设备 → 连接桌面设备，扫这个码。扫错了或过期了就刷新再扫。"),
                    refresh,
                },
            },
        };
        var phonePage = new TabItem
        {
            Header = "手机号",
            Content = new StackPanel { Spacing = 10, Margin = new(0, 12, 0, 0), Children = { phone, code, pwd, Hint("验证码会发到你已登录的 Telegram 里（没有的话发短信）。"), next } },
        };
        var tabs = new TabControl { Items = { qrPage, phonePage } };
        var dlg = new Window
        {
            Title = "登录 Telegram", Width = 380, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new(20), Spacing = 12,
                Children = { tabs, status, Hint("只用来检索和下载频道里的字幕；登录状态保存在程序目录的 telegram.session") },
            },
        };

        void Fail(Exception ex) => status.Text = "登录失败：" + TgClient.Explain(ex) + (TgClient.RecentLog() is { Length: > 0 } log ? "\n\n" + log : "");

        CancellationTokenSource? qrCts = null;
        void StartQr()
        {
            qrCts?.Cancel();
            var cts = qrCts = new CancellationTokenSource();
            status.Text = "";
            img.Source = null;
            // Off the UI thread: the 2FA password callback blocks while it asks.
            Task.Run(() => tg.LoginQr(url => Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) img.Source = QrBitmap(url); }), cts.Token))
                .ContinueWith(t => Dispatcher.UIThread.Post(() =>
                {
                    if (tg.LoggedIn) dlg.Close();
                    else if (!cts.IsCancellationRequested && t.Exception?.GetBaseException() is { } ex && ex is not OperationCanceledException) Fail(ex);
                }));
        }
        refresh.Click += (_, _) => StartQr();
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != tabs) return;
            if (tabs.SelectedItem == qrPage) StartQr();
            else { qrCts?.Cancel(); status.Text = ""; phone.Focus(); }
        };

        string stage = "phone";
        next.Click += async (_, _) =>
        {
            var input = (stage switch { "verification_code" => code.Text, "password" => pwd.Text, _ => phone.Text })?.Trim();
            if (string.IsNullOrEmpty(input)) return;
            next.IsEnabled = false;
            status.Text = "请稍候…";
            try
            {
                var need = await Task.Run(() => tg.LoginPhone(input));
                status.Text = "";
                if (need == null) { dlg.Close(); return; }
                stage = need;
                code.IsVisible = need == "verification_code" || code.IsVisible;
                pwd.IsVisible = need == "password";
                next.Content = "登录";
                (need == "password" ? pwd : code).Focus();
                if (need == "verification_code") phone.IsEnabled = false;
                else if (need != "password") status.Text = "Telegram 还需要：" + need;
            }
            catch (Exception ex) { Fail(ex); }
            finally { next.IsEnabled = true; }
        };

        dlg.Closed += (_, _) => qrCts?.Cancel();
        dlg.Opened += (_, _) => StartQr();
        await dlg.ShowDialog(this);
    }

    static WriteableBitmap QrBitmap(string text)
    {
        var m = TgSubs.QrMatrix(text);
        const int scale = 6;
        int n = m.Length, size = n * scale;
        var px = new int[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = m[y / scale][x / scale] ? unchecked((int)0xFF000000) : -1;
        var bmp = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
            for (int y = 0; y < size; y++) Marshal.Copy(px, y * size, fb.Address + y * fb.RowBytes, size);
        return bmp;
    }

    /// <summary>Called on a worker thread by the Telegram client for accounts with two-step verification.</summary>
    string? AskTgPassword() => Dispatcher.UIThread.CheckAccess() ? null
        : Dispatcher.UIThread.InvokeAsync(() => Prompt("这个账号开了两步验证，输入密码：", true)).GetAwaiter().GetResult();

    async Task<string?> Prompt(string msg, bool password)
    {
        string? result = null;
        var box = new TextBox { PasswordChar = password ? '•' : '\0' };
        var dlg = new Window { Title = "SubMatcher", Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var ok = new Button { Content = "确定", Classes = { "accent" }, IsDefault = true };
        var no = new Button { Content = "取消", IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new(16), Spacing = 12,
            Children = { new TextBlock { Text = msg, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, box,
                         new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { ok, no } } },
        };
        dlg.Opened += (_, _) => box.Focus();
        await dlg.ShowDialog(this);
        return result;
    }

    void TgQueryKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TgSearch(sender, e);
    }

    string Channel => Blank(TgChannel.Text) ?? TgSubs.DefaultChannel;

    static string Describe(TgGroup g) =>
        $"{g.Title}  ·  {(g.Platform.Length > 0 ? g.Platform : "未知平台")}    简 {g.Chs.Count} / 繁 {g.Cht.Count}{(g.Other.Count > 0 ? $" / 其他 {g.Other.Count}" : "")}";

    async void TgSearch(object? sender, RoutedEventArgs e)
    {
        if (Blank(TgQuery.Text) is not { } q) { TgLog.Text = "先填片名关键词"; return; }
        TgSearchBtn.IsEnabled = TgDownloadBtn.IsEnabled = false;
        try
        {
            if (await TgEnsure(true) is not { } tg) return;
            var channel = Channel;
            _settings.TgChannel = channel;
            _settings.Save();
            TgLog.Text = "检索中…";
            // Progress must be created here on the UI thread: inside Task.Run it would call back on the thread pool.
            var found = new Progress<int>(n => TgLog.Text = $"检索中… {n} 个文件");
            var files = await Task.Run(() => tg.Search(channel, q, found));
            ShowGroups(TgSubs.Group(files));
            TgLog.Text = _groups.Count == 0 ? "没找到。换个关键词，简体 / 繁体片名都试试" : $"{files.Count} 个文件，合并为 {_groups.Count} 组";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { TgLog.Text = "检索失败：" + ex.Message; }
        finally { TgSearchBtn.IsEnabled = TgDownloadBtn.IsEnabled = true; }
    }

    /// <summary>Result list: one row per group; the selected row expands to list its files, "收起" folds it back.</summary>
    internal void ShowGroups(List<TgGroup> groups)
    {
        _groups = groups;
        TgGroups.ItemsSource = groups.Select(GroupItem).ToList();
        TgGroups.SelectedIndex = groups.Count > 0 ? 0 : -1;
    }

    Control GroupItem(TgGroup g)
    {
        var detail = new StackPanel { Spacing = 4, Margin = new(0, 8, 0, 4), IsVisible = false };
        foreach (var (label, files) in new[] { ("简体", g.Chs), ("繁体", g.Cht), ("其他", g.Other) })
        {
            if (files.Count == 0) continue;
            var eps = TgSubs.EpisodeSummary(files);
            detail.Children.Add(new TextBlock
            {
                Text = $"{label} {files.Count} 个{(eps.Length > 0 ? " · " + eps : "")}", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 12,
                Margin = new(0, 4, 0, 0), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            var list = new SelectableTextBlock
            {
                Text = string.Join("\n", files.Select(f => f.Name)), FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
            };
            list.Bind(TextBlock.ForegroundProperty, list.GetResourceObservable("SemiColorText2"));
            // A season is dozens of names: keep the row short so the other results stay in view.
            detail.Children.Add(new ScrollViewer { Content = list, MaxHeight = 96 });
        }
        var collapse = new Button
        {
            Content = "收起", Theme = this.FindResource("BorderlessButton") as Avalonia.Styling.ControlTheme,
            Padding = new(8, 0), IsVisible = false, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var header = new Avalonia.Controls.Grid { ColumnDefinitions = new("*,Auto"), MinHeight = 24 };
        header.Children.Add(new TextBlock { Text = Describe(g), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        Avalonia.Controls.Grid.SetColumn(collapse, 1);
        header.Children.Add(collapse);
        var item = new StackPanel { Children = { header, detail } };
        collapse.Click += (_, e) => { SetExpanded(item, false); e.Handled = true; };
        // Tapping a folded row (already selected, so no SelectionChanged) opens it again.
        item.Tapped += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) == collapse) return;
            if (!detail.IsVisible) SetExpanded(item, true);
        };
        return item;
    }

    static void SetExpanded(StackPanel item, bool on)
    {
        var detail = item.Children[1];
        if (detail.IsVisible == on) return;
        detail.IsVisible = on;
        ((Avalonia.Controls.Grid)item.Children[0]).Children[1].IsVisible = on;
        if (on) _ = new DropDownReveal { Duration = TimeSpan.FromMilliseconds(160) }.Start(null, detail, true, default);
    }

    void TgGroupSelected(object? sender, SelectionChangedEventArgs e)
    {
        e.Handled = true; // don't bubble up to the tab control
        foreach (var item in (TgGroups.ItemsSource as IEnumerable<Control>)?.OfType<StackPanel>() ?? [])
            SetExpanded(item, item == TgGroups.SelectedItem);
    }

    async void TgDownload(object? sender, RoutedEventArgs e)
    {
        if (TgGroups.SelectedIndex < 0 || TgGroups.SelectedIndex >= _groups.Count) { TgLog.Text = "先检索，再选一组"; return; }
        var g = _groups[TgGroups.SelectedIndex];
        List<TgFile> files = TgLang.SelectedIndex switch { 0 => g.Chs, 1 => g.Cht, _ => [.. g.Chs, .. g.Cht, .. g.Other] };
        if (files.Count == 0) { TgLog.Text = $"这一组没有{(TgLang.SelectedIndex == 0 ? "简体" : "繁体")}字幕"; return; }
        var folder = string.Concat($"{g.Title} [{g.Platform}]".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Blank(TgOut.Text) ?? Path.Combine(AppContext.BaseDirectory, "字幕下载", folder);
        TgSearchBtn.IsEnabled = TgDownloadBtn.IsEnabled = false;
        TgProgress.IsVisible = true;
        TgProgress.Value = 0;
        try
        {
            if (await TgEnsure(true) is not { } tg) return;
            var channel = Channel;
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                TgProgress.Value = (double)p.Done / p.Total;
                TgLog.Text = $"下载中… {p.Done}/{p.Total}";
            });
            var got = await Task.Run(() => tg.Download(channel, files, dir, progress));
            TgLog.Text = $"已下载 {got.Count} 个 → {dir}";
            RenSubs.Text = dir; // ready for renaming once synced
            Toast("字幕下载完成", $"{got.Count} 个字幕", NotificationType.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { TgLog.Text = "下载失败：" + ex.Message; }
        finally { TgSearchBtn.IsEnabled = TgDownloadBtn.IsEnabled = true; TgProgress.IsVisible = false; }
    }

    // ---------------- rename ----------------

    List<RenamePlan>? RenamePlanNow()
    {
        if (Blank(RenVideos.Text) is not { } videos) { RenLog.Text = "先选视频文件夹"; return null; }
        var (vs, _) = Renamer.Collect([videos]);
        var (_, subs) = Renamer.Collect([Blank(RenSubs.Text) ?? videos]);
        var plan = Renamer.Plan(vs, subs, RenSc.Text?.Trim() ?? "", RenTc.Text?.Trim() ?? "");
        RenLog.Text = plan.Count == 0
            ? $"{vs.Count} 个视频、{subs.Count} 个字幕，没有能对上集数的"
            : string.Join("\n", plan.Select(p => p.NoOp ? $"= {Path.GetFileName(p.Target)}" : $"{Path.GetFileName(p.Sub)}\n    → {Path.GetFileName(p.Target)}"));
        return plan;
    }

    void RenamePreview(object? sender, RoutedEventArgs e) => RenamePlanNow();

    void RenameRun(object? sender, RoutedEventArgs e)
    {
        if (RenamePlanNow() is not { Count: > 0 } plan) return;
        try
        {
            int n = Renamer.Apply(plan, RenCopy.IsChecked == true, RenBackup.IsChecked == true);
            RenLog.Text += $"\n\n已{(RenCopy.IsChecked == true ? "复制" : "改名")} {n} 个" + (RenBackup.IsChecked == true && RenCopy.IsChecked != true && n > 0 ? "，原字幕在「字幕备份」文件夹" : "");
            Toast("改名完成", $"{n} 个字幕", NotificationType.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RenLog.Text += "\n\n失败：" + ex.Message; }
    }

    // ---------------- 比例调整 ----------------

    async void BrowseFitVideo(object? sender, RoutedEventArgs e) { if (await PickFile(VideoType) is { } p) FitVideo.Text = p; }
    async void BrowseFitSub(object? sender, RoutedEventArgs e) { if (await PickFile(SubType) is { } p) FitSub.Text = p; }

    async void ToolFit(object? sender, RoutedEventArgs e)
    {
        void Show(string s) { FitLog.IsVisible = true; FitLog.Text = s; }
        if (Blank(FitVideo.Text) is not { } video) { Show("先选视频"); return; }
        string[] subs = Blank(FitSub.Text) is { } s ? [s] : [];
        bool inPlace = FitInPlace.IsChecked == true;
        FitBtn.IsEnabled = false;
        Show("检测黑边…");
        try
        {
            var rs = await Task.Run(() => AssFrame.FitToVideo(video, subs, inPlace));
            Show(string.Join("\n", rs.Select(r => $"{(r.Output != null ? "✓" : "–")} {r.Input}\n    {r.Message}{(r.Output != null ? "\n    → " + r.Output : "")}")));
            int n = rs.Count(r => r.Output != null);
            Toast("比例调整", n > 0 ? $"已调整 {n} 个字幕" : "不需要调整", n > 0 ? NotificationType.Success : NotificationType.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { Show("失败：" + ex.Message); }
        finally { FitBtn.IsEnabled = true; }
    }

    // ---------------- 简繁转换 (繁化姬) ----------------

    public static string[] ZhModes { get; } = ZhConvert.Modes.Select(m => m.Label).ToArray();

    async void ToolZh(object? sender, RoutedEventArgs e)
    {
        var target = Blank(ZhInput.Text) ?? Blank(ToolSub.Text);
        void Show(string s) { ZhLog.IsVisible = true; ZhLog.Text = s; }
        if (target == null) { Show("先选字幕或文件夹"); return; }
        var files = FontSubset.Collect([target], ZhRecursive.IsChecked == true);
        if (files.Count == 0) { Show("没有找到 .ass / .ssa / .srt 字幕"); return; }
        var mode = ZhConvert.Modes[Math.Max(0, ZhMode.SelectedIndex)];
        bool inPlace = ZhInPlace.IsChecked == true;
        ZhBtn.IsEnabled = false;
        ZhProgress.IsVisible = true;
        ZhProgress.Value = 0;
        var log = new System.Text.StringBuilder();
        int done = 0, bad = 0;
        Show($"{mode.Label}：{files.Count} 个字幕…");
        try
        {
            // 2 at a time: a free public service.
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (f, ct) =>
            {
                string line;
                try { line = $"✓ {Path.GetFileName(f)} → {Path.GetFileName(await ZhConvert.ConvertFile(f, mode.Key, inPlace ? f : null, ct))}"; }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException or UnauthorizedAccessException)
                {
                    line = $"✗ {Path.GetFileName(f)}  {ex.Message}";
                    Interlocked.Increment(ref bad);
                }
                int n = Interlocked.Increment(ref done);
                Dispatcher.UIThread.Post(() => { log.AppendLine(line); ZhProgress.Value = (double)n / files.Count; Show(log.ToString()); });
            });
            Toast("简繁转换完成", $"成功 {files.Count - bad}，失败 {bad}", bad == 0 ? NotificationType.Success : NotificationType.Warning);
        }
        finally { ZhBtn.IsEnabled = true; ZhProgress.IsVisible = false; }
    }
}
