using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BpsrRadar;

// Every slider in the settings popup. A click on the track has to jump to
// the clicked value. WPF's default instead pages by LargeChange (1.0), which
// on 0.15-1 opacity or 0-4 frame ranges sends the value to an end on every
// click, so the slider seemed to toggle between minimum and maximum.
internal static class SettingSlider
{
    public static Slider Create(float value, float min, float max, bool wholeNumbers)
    {
        double range = max - min;
        return new Slider
        {
            Width = 130,
            Minimum = min,
            Maximum = max,
            Value = value,
            IsMoveToPointEnabled = true,
            IsSnapToTickEnabled = wholeNumbers,
            TickFrequency = 1,
            SmallChange = wholeNumbers ? 1 : range / 100,
            // Keyboard PageUp/PageDown, and the track while the thumb is held.
            LargeChange = wholeNumbers ? Math.Max(1, Math.Round(range / 10)) : range / 10,
            VerticalAlignment = VerticalAlignment.Center,
            AutoToolTipPlacement = AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = range <= 4 ? 2 : 0,
        };
    }
}
