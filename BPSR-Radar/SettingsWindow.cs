using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace BpsrRadar;

// The settings as a floating window: draggable by its title row, closed by
// the x or Esc, opened only from the radar window's gear button. Code only,
// so the checks can build it without a XAML resource or an Application.
internal sealed class SettingsWindow : Window
{
    private const double Gap = 8;

    private readonly SettingsCallbacks callbacks;
    private readonly DispatcherTimer saveTimer;
    private readonly StackPanel body = new();

    public SettingsWindow(SettingsCallbacks callbacks)
    {
        // A slider change reaches the window's own debounced save as well as the caller's.
        this.callbacks = callbacks with { QueueSave = () => { callbacks.QueueSave(); QueueSave(); } };
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;

        TitleText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hex("#E6F2F3"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        CloseButton = new Button
        {
            Style = (Style)XamlReader.Parse(CloseButtonStyleXaml),
            Content = "",   // ChromeClose, the radar window's close glyph
            Width = 28,
            Height = 24,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetInitialShowDelay(CloseButton, 0);
        CloseButton.Click += (_, _) => Close();

        // Background=Transparent so the row's empty space takes the mouse.
        TitleRow = new Grid { Height = 30, Background = Brushes.Transparent };
        TitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TitleRow.Children.Add(TitleText);
        Grid.SetColumn(CloseButton, 1);
        TitleRow.Children.Add(CloseButton);
        TitleRow.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            {
                var before = new Point(Left, Top);
                try { DragMove(); } catch { }
                FinishDrag(before);
            }
        };

        Panel = NewPanel();
        body.Children.Add(TitleRow);
        body.Children.Add(Panel.View);
        var frame = new Border
        {
            Background = Hex("#121C20"),
            BorderBrush = Hex("#465F64"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = body,
        };
        frame.Resources.MergedDictionaries.Add(SettingsTheme.Resources);
        Content = frame;
        ApplyStrings();

        // Same 500 ms debounce as the target overlay; it saves slider changes
        // and, once the player has dragged the window, its position.
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            RadarSettings.Save();
        };
        // Only a real drag (FinishDrag) makes Moved true. The first placement,
        // the on-screen clamp and the bottom pull-back also fire LocationChanged
        // but are the program's moves, not the player's; once the player has
        // moved the window, those later corrections are stored too.
        LocationChanged += (_, _) =>
        {
            if (!IsLoaded || !Moved) return;
            StorePosition();
            QueueSave();
        };
        // Not directly: at Loaded, Left/Top still hold the system's default
        // spot; the position set in OnSourceInitialized lands a moment later.
        Loaded += (_, _) => Dispatcher.BeginInvoke(KeepInWorkArea, DispatcherPriority.ApplicationIdle);
        // The tab headings take no keyboard focus, so Ctrl+Tab / Ctrl+Shift+Tab
        // is how the keyboard switches tabs.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                SwitchTab((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            }
        };
        // KeyDown, not PreviewKeyDown: an open combo box drop-down handles Esc
        // itself on the way up (it only closes the list), and only an Esc
        // nobody handled should close the window.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    internal SettingsPanel Panel { get; private set; }
    internal TextBlock TitleText { get; }
    internal Button CloseButton { get; }
    internal Grid TitleRow { get; }

    // True once the player has dragged the window. Until then the position
    // stays unsaved (NaN) and the window opens beside the radar.
    internal bool Moved { get; private set; }

    // A press on the title row also runs DragMove and can shift the window by
    // a pixel or two; only a move of at least the system's drag distance counts.
    internal static bool IsRealMove(Point before, Point after) =>
        Math.Abs(after.X - before.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(after.Y - before.Y) >= SystemParameters.MinimumVerticalDragDistance;

    // After DragMove returns: a real move is kept and stored; a click or a
    // tiny nudge puts the window back and leaves Moved alone.
    internal void FinishDrag(Point before)
    {
        var after = new Point(Left, Top);
        if (IsRealMove(before, after))
        {
            Moved = true;
            StorePosition();
            QueueSave();
        }
        else if (after != before)
        {
            Left = before.X;
            Top = before.Y;
        }
    }

    // Ctrl+Tab / Ctrl+Shift+Tab. An open drop-down is closed first; once the
    // new page is laid out, keyboard focus goes to its first control (or to
    // the window, so Esc and the next Ctrl+Tab still reach it).
    internal void SwitchTab(int step)
    {
        if (FindOpenDropDown(Panel.Scroll.Content as DependencyObject) is { } open) open.IsDropDownOpen = false;
        Panel.CycleTab(step);
        if (!IsLoaded) return;
        var panel = Panel;
        Dispatcher.BeginInvoke(() =>
        {
            if (panel != Panel) return;
            if (Panel.Scroll.Content is UIElement page &&
                page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) return;
            Focus();
        }, DispatcherPriority.Loaded);
    }

    private static ComboBox? FindOpenDropDown(DependencyObject? node)
    {
        if (node == null) return null;
        if (node is ComboBox { IsDropDownOpen: true } combo) return combo;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (FindOpenDropDown(child) is { } found) return found;
        }
        return null;
    }

    // Puts the position into the settings, but only for a window the player
    // moved; OnClosed saves the file. A window that was only placed keeps NaN.
    internal void SavePositionIfMoved()
    {
        if (Moved) StorePosition();
    }

    internal void QueueSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    // Puts the position into the settings; the caller saves.
    internal void StorePosition()
    {
        RadarSettings.Instance.SettingsWindowLeft = Left;
        RadarSettings.Instance.SettingsWindowTop = Top;
    }

    private SettingsPanel NewPanel()
    {
        var panel = new SettingsPanel(callbacks);
        panel.TabChanged += OnTabChanged;
        return panel;
    }

    // A language switch: new panel, same window. The tab comes back through
    // SettingsPanel.SelectedTab; the scroll position is put back once laid out.
    internal void Rebuild()
    {
        double offset = Panel.Scroll.VerticalOffset;
        Panel = NewPanel();
        body.Children.RemoveAt(1);   // the indexer refuses to replace a connected child
        body.Children.Add(Panel.View);
        ApplyStrings();
        var scroll = Panel.Scroll;
        scroll.Dispatcher.BeginInvoke(() => scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    private void ApplyStrings()
    {
        Title = S.T("BPSR Radar Settings", "BPSR Radar 設定");
        TitleText.Text = S.T("Settings", "設定");
        CloseButton.ToolTip = S.T("Close", "閉じる");
    }

    // The page height differs per tab and the window follows it downwards;
    // after layout, bring a window that grew past the screen back.
    private void OnTabChanged()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(KeepInWorkArea, DispatcherPriority.Loaded);
    }

    // The first placement (beside the radar, or where it was left) can also
    // reach below the work area, and so can a display change.
    internal void KeepInWorkArea()
    {
        if (!IsLoaded) return;
        WindowPlacement.EnsureOnScreen(this);
        var work = WindowPlacement.WorkAreaNear(this, new Rect(Left, Top, ActualWidth, ActualHeight));
        double top = FitTop(Top, ActualHeight, work.Top, work.Bottom);
        if (top != Top) Top = top;
    }

    // A window whose bottom is past the work area moves up, but never above
    // its top; one that fits stays where it is.
    internal static double FitTop(double top, double height, double workTop, double workBottom) =>
        top + height > workBottom ? Math.Max(workTop, workBottom - height) : top;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var content = (FrameworkElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;
        var s = RadarSettings.Instance;
        bool savedOnScreen = double.IsFinite(s.SettingsWindowLeft) && double.IsFinite(s.SettingsWindowTop) &&
            WindowPlacement.IntersectsMonitor(this, s.SettingsWindowLeft, s.SettingsWindowTop, size.Width, size.Height);
        if (Owner == null && !savedOnScreen) return;
        var owner = Owner is { } o ? new Rect(o.Left, o.Top, o.ActualWidth, o.ActualHeight) : new Rect(0, 0, 0, 0);
        var position = InitialPosition(owner, size, WindowPlacement.WorkAreaNear(this, owner),
            s.SettingsWindowLeft, s.SettingsWindowTop, savedOnScreen);
        // Already inside the work area when it first appears, rather than
        // jumping up once Loaded has run.
        var placedWork = WindowPlacement.WorkAreaNear(this, new Rect(position, size));
        Left = position.X;
        Top = FitTop(position.Y, size.Height, placedWork.Top, placedWork.Bottom);
    }

    protected override void OnClosed(EventArgs e)
    {
        saveTimer.Stop();
        SavePositionIfMoved();
        RadarSettings.Save();
        base.OnClosed(e);
    }

    // Where the window first appears: where it was left if that is still on a
    // monitor, else beside the radar window (right, then left), else on it.
    internal static Point InitialPosition(Rect owner, Size self, Rect workArea,
        double savedLeft, double savedTop, bool savedOnScreen)
    {
        if (double.IsFinite(savedLeft) && double.IsFinite(savedTop) && savedOnScreen)
        {
            return new Point(savedLeft, savedTop);
        }
        double right = owner.Right + Gap;
        if (right + self.Width <= workArea.Right)
        {
            return new Point(right, owner.Top);
        }
        double left = owner.Left - self.Width - Gap;
        if (left >= workArea.Left)
        {
            return new Point(left, owner.Top);
        }
        return owner.TopLeft;
    }

    private static SolidColorBrush Hex(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    // Flat, 28x24, round 5. Hover: dark red face and a light red glyph.
    private const string CloseButtonStyleXaml = """
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
    <Setter Property="FontFamily" Value="Segoe Fluent Icons, Segoe MDL2 Assets"/>
    <Setter Property="FontSize" Value="11"/>
    <Setter Property="Foreground" Value="#BFE3E6"/>
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="Button">
                <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="5">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="bd" Property="Background" Value="#3A2A2E"/>
                        <Setter Property="Foreground" Value="#FF8A8A"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
""";
}
