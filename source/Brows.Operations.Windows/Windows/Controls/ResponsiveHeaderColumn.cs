using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Brows.Windows.Controls;

internal sealed class ResponsiveHeaderColumn(ColumnDefinition column, Action onInputChanged) {
    private static readonly TimeSpan OverrideDuration = TimeSpan.FromSeconds(1);

    private ClockGroup _Clock;
    private GridLength _InputWidth;
    private double _InputMinimum;
    private double _OverrideWidth;

    private double InputWidth {
        get {
            var width = _Clock is null ? column.Width : _InputWidth;
            var minimum = _Clock is null ? column.MinWidth : _InputMinimum;
            return Math.Max(width.IsAbsolute ? width.Value : 0, minimum);
        }
    }

    private bool OverrideReplaced => _Clock is not null &&
        (column.Width != new GridLength(_OverrideWidth) || column.MinWidth != 0);

    private void WidthEvaluated(GridLength width) {
        if (_InputWidth != width) {
            _InputWidth = width;
            onInputChanged();
        }
    }

    private void MinimumEvaluated(double minimum) {
        if (_InputMinimum != minimum) {
            _InputMinimum = minimum;
            onInputChanged();
        }
    }

    internal string SharedSizeGroup => column.SharedSizeGroup;

    internal bool IsPixelWidth => (_Clock is null ? column.Width : _InputWidth).IsAbsolute;

    internal double RequiredWidth { get; private set; }

    internal bool NeedsMeasure => OverrideReplaced || InputWidth != RequiredWidth;

    internal void Measure() {
        if (OverrideReplaced) {
            Restore();
        }
        RequiredWidth = InputWidth;
    }

    internal void Override(double width) {
        if (_Clock is not null && width == _OverrideWidth) {
            return;
        }
        Restore();
        _OverrideWidth = width;
        _InputWidth = column.Width;
        _InputMinimum = column.MinWidth;
        /*
         * Compose constant layout values over the consumer's bindings, resources and animations.
         * WPF supplies the preceding animation's output as the origin on every evaluation.
         * One paused clock group applies and releases both constraints together.
         */
        var group = new ParallelTimeline { Duration = OverrideDuration, FillBehavior = FillBehavior.Stop };
        group.Children.Add(new HeaderColumnWidthOverride<GridLength>(new GridLength(width), WidthEvaluated));
        group.Children.Add(new HeaderColumnWidthOverride<double>(0, MinimumEvaluated));
        _Clock = group.CreateClock();
        column.ApplyAnimationClock(ColumnDefinition.WidthProperty, (AnimationClock)_Clock.Children[0],
                                   HandoffBehavior.Compose);
        column.ApplyAnimationClock(ColumnDefinition.MinWidthProperty, (AnimationClock)_Clock.Children[1],
                                   HandoffBehavior.Compose);
        _Clock.Controller.Pause();
        _Clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
    }

    internal void Restore() {
        if (_Clock is null) {
            return;
        }
        var controller = _Clock.Controller;
        _Clock = null;
        controller.SeekAlignedToLastTick(OverrideDuration, TimeSeekOrigin.BeginTime);
        controller.Remove();
    }
}
