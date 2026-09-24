using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace SubMatcher.Gui;

/// <summary>
/// Content transition: the content fades in while sliding down from just under its header, instead of popping
/// into place. Used for expanders and tab pages. Collapsing stays instant (the panel is hidden right away anyway).
/// </summary>
/// <remarks>
/// Built on property transitions, not keyframe animations: a code-built keyframe animation of RenderTransform
/// throws "no animator registered" in Avalonia 11.3, and Expander swallows it (async void), so it silently didn't animate.
/// </remarks>
public sealed class DropDownReveal : IPageTransition
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(220);

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (to is null) return;
        // Start state, applied without animating.
        to.Transitions = null;
        to.Opacity = 0;
        to.RenderTransform = TransformOperations.Parse("translateY(-10px)");
        try
        {
            // Expander calls this synchronously on IsExpanded, before its template has made the content visible.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            var easing = new CubicEaseOut();
            to.Transitions =
            [
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = Duration, Easing = easing },
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Duration, Easing = easing },
            ];
            to.Opacity = 1;
            to.RenderTransform = TransformOperations.Parse("translateY(0px)");
            await Task.Delay(Duration, cancellationToken);
        }
        catch (TaskCanceledException) { }
        finally
        {
            // Never leave content hidden or shifted, even if cancelled midway; hand the properties back to styles.
            to.ClearValue(Visual.TransitionsProperty);
            to.ClearValue(Visual.OpacityProperty);
            to.ClearValue(Visual.RenderTransformProperty);
        }
    }
}
