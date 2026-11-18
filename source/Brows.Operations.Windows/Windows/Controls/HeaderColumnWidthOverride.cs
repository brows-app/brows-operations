using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Brows.Windows.Controls;

internal sealed class HeaderColumnWidthOverride<T>(T value, Action<T> onInput)
: AnimationTimeline where T : struct {

    protected override Freezable CreateInstanceCore() =>
        new HeaderColumnWidthOverride<T>(value, onInput);

    public override Type TargetPropertyType => typeof(T);

    public override object GetCurrentValue(object defaultOriginValue,
                                           object defaultDestinationValue,
                                           AnimationClock animationClock) {
        onInput((T)defaultOriginValue);
        return value;
    }
}
