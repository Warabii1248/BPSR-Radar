using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace BpsrRadar;

// Tiny always-interactive window shown on top of the click-through toggle
// while click-through is active, so the mode can always be switched off
// without knowing the hotkey.
internal sealed class ClickThroughGuardWindow : Window
{
    public event Action? Clicked;

    public ClickThroughGuardWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;

        var button = new Button
        {
            Width = 32,
            Height = 30,
            // The same glyph and ON colour as the title bar's click-through
            // button it covers (this window only shows while that is ON).
            Content = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x3F, 0x44)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xD0, 0x7B)),
            BorderThickness = new Thickness(1.5),
            Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xA7, 0xAD)),
            Template = template,
        };
        ToolTipService.SetInitialShowDelay(button, 0);
        button.Click += (_, _) => Clicked?.Invoke();
        Content = button;
        ApplyStrings();
    }

    // The window is reused across toggles; refreshed on each show so a
    // language change in between is picked up.
    public void ApplyStrings()
    {
        ((Button)Content).ToolTip = S.T("Click-through ON — click to restore mouse input",
            "クリック透過中 — クリックでマウス操作を復帰");
    }

    // Positions this window exactly over the given element (usually the
    // click-through toggle button) of another window.
    public void TrackTo(FrameworkElement target)
    {
        var source = PresentationSource.FromVisual(target);
        if (source?.CompositionTarget == null)
        {
            return;
        }
        var topLeft = target.PointToScreen(new Point(0, 0));
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        Left = topLeft.X * fromDevice.M11;
        Top = topLeft.Y * fromDevice.M22;
    }
}
