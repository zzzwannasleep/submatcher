using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SubMatcher.Gui;

/// <summary>One step of the tour. Target null = centered card with no highlight.</summary>
public sealed record TourStep(Func<Control?> Target, string Title, string Body, Action? Before = null);

/// <summary>
/// driver.js, natively: a dimmed overlay with an animated cut-out around the current control and a popover
/// card with skip / previous / next. Esc skips, ← → navigate. Blocks clicks on the app while open.
/// </summary>
public sealed class TourOverlay : Panel
{
    readonly Spotlight _spot = new();
    readonly Canvas _canvas = new();
    readonly Border _card;
    readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _body = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 22, Opacity = 0.85 };
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 0, 0, 4) };
    readonly Button _prev, _next, _skip;
    IReadOnlyList<TourStep> _steps = [];
    int _index;

    /// <summary>Raised once when the tour closes; true = finished all steps, false = skipped.</summary>
    public event Action<bool>? Closed;
    public int Index => _index;
    public bool IsOpen => IsVisible;

    public TourOverlay()
    {
        IsVisible = false;
        Background = Brushes.Transparent; // swallow clicks meant for the app underneath
        Focusable = true;

        _skip = new Button { Content = "跳过引导", Classes = { "Tertiary" } };
        _prev = new Button { Content = "上一步" };
        _next = new Button { Content = "下一步", Classes = { "Primary" } };
        _skip.Theme = _prev.Theme = Application.Current?.FindResource("BorderlessButton") as Avalonia.Styling.ControlTheme;
        _next.Theme = Application.Current?.FindResource("SolidButton") as Avalonia.Styling.ControlTheme;
        _skip.Click += (_, _) => Close(false);
        _prev.Click += (_, _) => Go(_index - 1);
        _next.Click += (_, _) => Next();

        // Skip on the left, back/next on the right; progress dots sit above the title.
        var footer = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), Margin = new(0, 12, 0, 0) };
        footer.Children.Add(_skip);
        _skip.Margin = new(-8, 0, 0, 0);
        Grid.SetColumn(_prev, 2); _prev.Margin = new(4, 0); footer.Children.Add(_prev);
        Grid.SetColumn(_next, 3); footer.Children.Add(_next);

        _card = new Border
        {
            Width = 360, Padding = new(20, 18), CornerRadius = new(12),
            Child = new StackPanel { Spacing = 8, Children = { _dots, _title, _body, footer } },
            BoxShadow = BoxShadows.Parse("0 8 32 0 #40000000"),
            Transitions =
            [
                new DoubleTransition { Property = Canvas.LeftProperty, Duration = TimeSpan.FromMilliseconds(280), Easing = new CubicEaseOut() },
                new DoubleTransition { Property = Canvas.TopProperty, Duration = TimeSpan.FromMilliseconds(280), Easing = new CubicEaseOut() },
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) },
            ],
        };
        _card.Bind(Border.BackgroundProperty, _card.GetResourceObservable("SemiColorBackground2"));
        _canvas.Children.Add(_card);
        Children.Add(_spot);
        Children.Add(_canvas);

        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Close(false); break;
                case Key.Right or Key.Enter: Next(); break;
                case Key.Left: Go(_index - 1); break;
                default: return;
            }
            e.Handled = true;
        };
        // Keep the highlight glued to its control when the window resizes.
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty && IsVisible) Place(animate: false); };
    }

    public void Start(IReadOnlyList<TourStep> steps)
    {
        _steps = steps;
        IsVisible = true;
        _spot.Hole = new Rect(Bounds.Width / 2, Bounds.Height / 2, 0, 0);
        Go(0);
        Focus();
    }

    void Next()
    {
        if (_index + 1 < _steps.Count) Go(_index + 1); else Close(true);
    }

    void Close(bool finished)
    {
        if (!IsVisible) return;
        IsVisible = false;
        Closed?.Invoke(finished);
    }

    void Go(int i)
    {
        if (i < 0 || i >= _steps.Count) return;
        _index = i;
        var step = _steps[i];
        step.Before?.Invoke();
        _title.Text = step.Title;
        _body.Text = step.Body;
        _prev.IsVisible = i > 0;
        _next.Content = i == _steps.Count - 1 ? "完成" : "下一步";
        _dots.Children.Clear();
        for (int k = 0; k < _steps.Count; k++)
        {
            var dot = new Border { Width = k == i ? 16 : 6, Height = 6, CornerRadius = new(3), Opacity = k == i ? 1 : 0.35 };
            dot.Bind(Border.BackgroundProperty, dot.GetResourceObservable("SemiColorPrimary"));
            _dots.Children.Add(dot);
        }
        // Wait a layout pass: Before may have switched tabs or expanded panels.
        Dispatcher.UIThread.Post(() => Place(animate: true), DispatcherPriority.Render);
    }

    void Place(bool animate)
    {
        if (_steps.Count == 0) return;
        var target = _steps[_index].Target();
        Rect hole = default;
        if (target is { IsEffectivelyVisible: true } && target.TranslatePoint(default, this) is { } p)
            hole = new Rect(p, target.Bounds.Size).Inflate(6);
        _spot.MoveTo(hole.Width > 0 ? hole : new Rect(Bounds.Center, new Size()), animate);

        _card.Measure(Size.Infinity);
        double w = _card.Width, h = _card.DesiredSize.Height, pad = 12, x, y;
        if (hole.Width <= 0) { x = (Bounds.Width - w) / 2; y = (Bounds.Height - h) / 2; }
        else
        {
            // Below the target if it fits, else above, else to its side; always kept inside the window.
            x = hole.X;
            if (hole.Bottom + pad + h <= Bounds.Height) y = hole.Bottom + pad;
            else if (hole.Top - pad - h >= 0) y = hole.Top - pad - h;
            else { y = hole.Y; x = hole.Right + pad + w <= Bounds.Width ? hole.Right + pad : hole.Left - pad - w; }
        }
        x = Math.Clamp(x, pad, Math.Max(pad, Bounds.Width - w - pad));
        y = Math.Clamp(y, pad, Math.Max(pad, Bounds.Height - h - pad));
        var transitions = _card.Transitions;
        if (!animate) _card.Transitions = null;
        Canvas.SetLeft(_card, x);
        Canvas.SetTop(_card, y);
        if (!animate) _card.Transitions = transitions;
    }

    /// <summary>Dimmed layer with a rounded cut-out that glides between targets.</summary>
    sealed class Spotlight : Control
    {
        static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        public Rect Hole;
        Rect _from, _to;
        DateTime _t0;
        readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
        const double DurationMs = 280;

        public Spotlight()
        {
            IsHitTestVisible = false;
            _timer.Tick += (_, _) =>
            {
                double t = Math.Min(1, (DateTime.UtcNow - _t0).TotalMilliseconds / DurationMs);
                double e = 1 - Math.Pow(1 - t, 3); // cubic ease-out, same curve as the card
                Hole = new Rect(Lerp(_from.X, _to.X, e), Lerp(_from.Y, _to.Y, e), Lerp(_from.Width, _to.Width, e), Lerp(_from.Height, _to.Height, e));
                InvalidateVisual();
                if (t >= 1) _timer.Stop();
            };
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        public void MoveTo(Rect to, bool animate)
        {
            _from = Hole; _to = to; _t0 = DateTime.UtcNow;
            if (animate) _timer.Start(); else { Hole = to; InvalidateVisual(); }
        }

        public override void Render(DrawingContext context)
        {
            var geometry = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(Bounds.Size)), new RectangleGeometry(Hole, 10, 10));
            context.DrawGeometry(Dim, null, geometry);
        }
    }
}
