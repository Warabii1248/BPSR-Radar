using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BpsrRadar;

// What the settings panel needs from the radar window. No-ops by default,
// so a check can build and render the panel on its own.
internal sealed record SettingsCallbacks
{
    public Action SyncTargetOverlay { get; init; } = () => { };   // overlay show/close
    public Action SyncEnemyList { get; init; } = () => { };
    public Action ApplyTargetOverlay { get; init; } = () => { };  // targetOverlay?.ApplySettings()
    public Action ApplyEnemyList { get; init; } = () => { };      // enemyList?.ApplySettings()
    public Action<double> BackgroundOpacityChanged { get; init; } = _ => { }; // RootBrush.Opacity
    public Action<bool> LockTargetChanged { get; init; } = _ => { };          // LockButton.Opacity
    public Action LanguageChanged { get; init; } = () => { };     // ApplyStrings + rebuild the settings window
    public Action AfterWindowChange { get; init; } = () => { };   // KeepSettingsWindowOnTop
    public Action QueueSave { get; init; } = () => { };           // a slider moved: save after a short pause
}

// The settings window's content: four tabs (map, target, enemy list, general)
// over one scrolling page.
internal sealed class SettingsPanel
{
    // Kept for the session, like the old expanders' open state, so a rebuilt
    // panel (language switch) and the next open return to the same tab.
    internal static int SelectedTab;

    // No page is this tall today; a check lowers it to make the pages scroll.
    internal static double MaxPageHeight = 460;

    private const double LabelWidth = 90;
    private const double ControlWidth = 130;

    private const string AccentColor = "#3FA7AD";
    private const string TextColor = "#E6F2F3";
    private const string SubTextColor = "#9FC4C8";
    private const string DisabledColor = "#5E7377";

    private static readonly uint[] OverlayPalette =
    [
        0xFFFFFFFF, 0xFF55E0E6, 0xFF7BE37B, 0xFFE3D07B, 0xFFE39A5B,
        0xFFE35B5B, 0xFFD07BE3, 0xFF7BA8E3, 0xFF9AA5A8, 0xFF20262A, 0x00000000,
    ];

    // The name colours of elite (#FFD84A) and boss (#FF941F) targets. These two
    // yellow/orange swatches would make a plain name look like a kind, so the
    // text colour row leaves them out.
    private static readonly uint[] TextPaletteExcluded = [0xFFE3D07B, 0xFFE39A5B];

    private readonly SettingsCallbacks callbacks;
    private readonly RadarSettings settings = RadarSettings.Instance;
    private readonly List<(UIElement Element, Func<bool> Enabled)> enabledRules = [];
    private readonly StackPanel[] pages = new StackPanel[4];
    private readonly Border[] tabHeaders = new Border[4];
    private Border? sampleFrame;
    private TextBlock[] sampleTexts = [];

    public SettingsPanel(SettingsCallbacks callbacks)
    {
        this.callbacks = callbacks;

        pages[0] = BuildMapPage();
        pages[1] = BuildTargetPage();
        pages[2] = BuildEnemyListPage();
        pages[3] = BuildGeneralPage();

        string[] titles =
        [
            S.T("Map", "地図"), S.T("Target", "ターゲット"),
            S.T("Enemy list", "敵リスト"), S.T("General", "全般"),
        ];
        var headerGrid = new UniformGrid { Columns = 4 };
        for (int i = 0; i < titles.Length; i++)
        {
            int index = i;
            var header = new Border
            {
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Padding = new Thickness(2, 4, 2, 2),
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent,
                Child = new TextBlock { Text = titles[i], HorizontalAlignment = HorizontalAlignment.Center },
            };
            header.MouseLeftButtonDown += (_, e) => { SelectTab(index); e.Handled = true; };
            // The selected tab keeps its white title.
            header.MouseEnter += (_, _) => { if (index != SelectedTab) ((TextBlock)header.Child).Foreground = Hex(TextColor); };
            header.MouseLeave += (_, _) => { if (index != SelectedTab) ((TextBlock)header.Child).Foreground = Hex(SubTextColor); };
            tabHeaders[i] = header;
            headerGrid.Children.Add(header);
        }

        Scroll = new ScrollViewer
        {
            MaxHeight = MaxPageHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var top = new StackPanel { Margin = new Thickness(10, 10, 10, 0) };
        top.Children.Add(headerGrid);
        top.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 4) });
        var view = new StackPanel { Width = 300 };
        view.Resources.MergedDictionaries.Add(SettingsTheme.Resources);
        view.Children.Add(top);
        view.Children.Add(Scroll);
        View = view;

        RefreshEnabled();
        SelectTab(SelectedTab);
    }

    public FrameworkElement View { get; }        // the settings window's body
    public ScrollViewer Scroll { get; }          // keeps the scroll position across a language switch
    internal int TabCount => 4;

    // Raised when a tab is selected; the hosting window's height follows the page.
    internal event Action? TabChanged;

    // Ctrl+Tab / Ctrl+Shift+Tab: the next or previous tab, wrapping around.
    internal void CycleTab(int step) =>
        SelectTab(((SelectedTab + step) % TabCount + TabCount) % TabCount);

    internal void SelectTab(int index)
    {
        index = Math.Clamp(index, 0, TabCount - 1);
        SelectedTab = index;
        TabChanged?.Invoke();
        Scroll.Content = pages[index];
        Scroll.ScrollToTop();
        for (int i = 0; i < tabHeaders.Length; i++)
        {
            bool selected = i == index;
            var text = (TextBlock)tabHeaders[i].Child;
            tabHeaders[i].Background = selected ? Hex("#1A2A2F") : System.Windows.Media.Brushes.Transparent;
            tabHeaders[i].BorderBrush = selected ? Hex(AccentColor) : System.Windows.Media.Brushes.Transparent;
            text.Foreground = selected ? System.Windows.Media.Brushes.White : Hex(SubTextColor);
            text.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // Rows that only make sense while another setting is on stay visible but
    // greyed out. The theme greys the controls; a label is a plain TextBlock,
    // which takes no part in IsEnabled, so its colour is set here. A colour
    // row's swatches carry no disabled look of their own and are dimmed.
    private void RefreshEnabled()
    {
        foreach (var (element, enabled) in enabledRules)
        {
            bool on = enabled();
            element.IsEnabled = on;
            if (element is Border sample) sample.Opacity = on ? 1.0 : 0.35;
            if (element is not StackPanel row) continue;
            foreach (var child in row.Children)
            {
                if (child is TextBlock text) text.Foreground = Hex(on ? (text == row.Children[0] ? TextColor : SubTextColor) : DisabledColor);
                else if (child is Border swatch) swatch.Opacity = on ? 1.0 : 0.35;
            }
        }
    }

    private static System.Windows.Media.SolidColorBrush Hex(string color) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));

    // ---- Pages -----------------------------------------------------------

    private StackPanel BuildMapPage()
    {
        var page = new StackPanel { Margin = new Thickness(10, 0, 10, 10) };
        page.Children.Add(MakeHeading(S.T("View", "表示"), first: true));
        page.Children.Add(MakeComboRow(S.T("View mode", "表示モード"), settings.ViewMode,
            [(RadarViewMode.MiniMap, S.T("Mini map", "ミニマップ")), (RadarViewMode.ScanOverview, S.T("Scan overview", "全体スキャン"))],
            v => settings.ViewMode = v));
        page.Children.Add(MakeComboRow(S.T("Labels", "ラベル"), settings.LabelMode,
            [(RadarLabelMode.None, S.T("None", "なし")), (RadarLabelMode.PartyAndBoss, S.T("Party + Boss", "PT+ボス")),
                (RadarLabelMode.All, S.T("All", "すべて"))],
            v => settings.LabelMode = v));
        page.Children.Add(MakeSliderRow(S.T("Enemy range", "敵の表示距離"), settings.EnemyMaxDistanceMeters, 5, 500,
            v => settings.EnemyMaxDistanceMeters = v, v => $"{v:0} m"));
        page.Children.Add(MakeSliderRow(S.T("Map range", "マップ範囲"), settings.BaseDisplayRangeMeters, 20, 500,
            v => settings.BaseDisplayRangeMeters = v, v => $"{v:0} m"));
        page.Children.Add(MakeSliderRow(S.T("Background", "背景の濃さ"), settings.BackgroundOpacity, 0.1f, 1f,
            v => { settings.BackgroundOpacity = v; callbacks.BackgroundOpacityChanged(v); }, v => $"{v * 100:0}%"));

        page.Children.Add(MakeHeading(S.T("Show", "表示する対象")));
        var shown = new UniformGrid { Columns = 2 };
        var cell = new Thickness(0, 2, 0, 2);
        shown.Children.Add(MakeCheck(S.T("Self", "自分"), settings.ShowSelf, v => settings.ShowSelf = v, margin: cell));
        shown.Children.Add(MakeCheck(S.T("Party", "パーティー"), settings.ShowParty, v => settings.ShowParty = v, margin: cell));
        shown.Children.Add(MakeCheck(S.T("Other players", "他プレイヤー"), settings.ShowPlayers, v => settings.ShowPlayers = v, margin: cell));
        shown.Children.Add(MakeCheck(S.T("Normal monsters", "通常モンスター"), settings.ShowNormalMonsters, v => settings.ShowNormalMonsters = v, margin: cell));
        shown.Children.Add(MakeCheck(S.T("Elite", "エリート"), settings.ShowEliteMonsters, v => settings.ShowEliteMonsters = v, margin: cell));
        shown.Children.Add(MakeCheck(S.T("Boss", "ボス"), settings.ShowBosses, v => settings.ShowBosses = v, margin: cell));
        shown.Children.Add(MakeCheck(S.T("Unknown monsters", "種別不明"), settings.ShowUnknownMonsters, v => settings.ShowUnknownMonsters = v, margin: cell));
        shown.Children.Add(MakeCheck(S.T("Gimmicks", "ギミック"), settings.ShowGimmicks, v => settings.ShowGimmicks = v, margin: cell));
        page.Children.Add(shown);
        page.Children.Add(MakeCheck(S.T("Hide entities without HP", "HPのないエンティティを隠す"),
            settings.HideEntitiesWithoutHp, v => settings.HideEntitiesWithoutHp = v));

        page.Children.Add(MakeHeading(S.T("Guides", "補助表示")));
        page.Children.Add(MakeCheck(S.T("Self direction", "自分の向き"), settings.ShowSelfDirection, v => settings.ShowSelfDirection = v));
        page.Children.Add(MakeCheck(S.T("Distance rings", "距離リング"), settings.ShowDistanceRings, v => settings.ShowDistanceRings = v, asSwitch: true));
        page.Children.Add(MakeCheck(S.T("Ring labels", "リング距離表示"), settings.ShowDistanceRingLabels, v => settings.ShowDistanceRingLabels = v,
            enabled: () => settings.ShowDistanceRings, margin: new Thickness(18, 2, 0, 2)));
        return page;
    }

    private StackPanel BuildTargetPage()
    {
        var page = new StackPanel { Margin = new Thickness(10, 0, 10, 10) };
        page.Children.Add(MakeCheck(S.T("Lock target", "ロック対象"), settings.LockTargetEnabled, v =>
        {
            settings.LockTargetEnabled = v;
            callbacks.LockTargetChanged(v);
            callbacks.SyncTargetOverlay();
        }, asSwitch: true));
        page.Children.Add(MakeCheck(S.T("Show overlay", "オーバーレイ表示"), settings.TargetOverlayEnabled, v =>
        {
            settings.TargetOverlayEnabled = v;
            callbacks.SyncTargetOverlay();
        }, enabled: () => settings.LockTargetEnabled, asSwitch: true));

        // Everything below needs the overlay to exist, which needs both.
        Func<bool> overlayOn = () => settings.LockTargetEnabled && settings.TargetOverlayEnabled;
        page.Children.Add(MakeComboRow(S.T("HP display", "HP表示"), settings.EffectiveTargetOverlayHp,
            [(TargetOverlayHpMode.Off, S.T("None", "なし")), (TargetOverlayHpMode.Bar, S.T("Bar only", "バーのみ")),
                (TargetOverlayHpMode.Percent, "%"), (TargetOverlayHpMode.Actual, S.T("Current / max", "実HP"))],
            v => settings.SetTargetOverlayHp(v), ApplyTarget, overlayOn));
        page.Children.Add(MakeCheck(S.T("Always on top", "常に最前面"), settings.TargetOverlayTopMost,
            v => { settings.TargetOverlayTopMost = v; ApplyTarget(); }, enabled: overlayOn));

        page.Children.Add(MakeHeading(S.T("Look", "見た目")));
        page.Children.Add(MakeSliderRow(S.T("Opacity", "不透明度"), (float)settings.TargetOverlayOpacity, 0.15f, 1f,
            v => { settings.TargetOverlayOpacity = v; ApplyTarget(); }, v => $"{v * 100:0}%", enabled: overlayOn));
        page.Children.Add(MakeSliderRow(S.T("Text size", "文字サイズ"), (float)settings.TargetOverlayFontSize, 9f, 32f,
            v => { settings.TargetOverlayFontSize = v; ApplyTarget(); }, v => $"{v:0}", enabled: overlayOn));
        page.Children.Add(MakeSliderRow(S.T("Frame width", "枠の太さ"), (float)settings.TargetOverlayBorderThickness, 0f, 4f,
            v => { settings.TargetOverlayBorderThickness = v; ApplyTarget(); }, v => $"{v:0.0}", enabled: overlayOn));
        page.Children.Add(MakeColorRow(S.T("Frame", "枠色"), () => settings.TargetOverlayBorderArgb,
            v => settings.TargetOverlayBorderArgb = v, enabled: overlayOn));
        page.Children.Add(MakeColorRow(S.T("Background", "背景色"), () => settings.TargetOverlayBackgroundArgb,
            v => settings.TargetOverlayBackgroundArgb = v, enabled: overlayOn));
        page.Children.Add(MakeColorRow(S.T("Text", "文字色"), () => settings.TargetOverlayTextArgb,
            v => { settings.TargetOverlayTextArgb = v; callbacks.ApplyEnemyList(); }, allowTransparent: false,
            toolTip: S.T("Also used by the enemy list", "敵リストの文字にも使います"), exclude: TextPaletteExcluded,
            enabled: overlayOn));
        page.Children.Add(MakeSample(overlayOn));
        return page;
    }

    // The overlay's look in miniature: its frame, background and opacity with a
    // plain, an elite and a boss name. Redrawn on every change that reaches
    // the overlay. The backdrop is a flat colour so a transparent background
    // stays readable.
    private FrameworkElement MakeSample(Func<bool> enabled)
    {
        var names = new (string Text, RadarEntityKind? Kind)[]
        {
            (S.T("Normal", "通常"), null),
            (S.T("Elite", "精鋭"), RadarEntityKind.Elite),
            (S.T("Boss", "ボス"), RadarEntityKind.Boss),
        };
        var grid = new UniformGrid { Columns = 3 };
        sampleTexts = new TextBlock[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            sampleTexts[i] = new TextBlock
            {
                Text = names[i].Text,
                FontSize = 11,
                FontWeight = names[i].Kind == null ? FontWeights.Normal : FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = names[i].Kind,
            };
            grid.Children.Add(sampleTexts[i]);
        }
        sampleFrame = new Border
        {
            Height = 22,
            CornerRadius = new CornerRadius(5),
            Child = grid,
        };
        var backdrop = new Border
        {
            Background = Hex("#2B3A3F"),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(3),
            Child = sampleFrame,
        };
        RefreshSample();
        return Track(backdrop, enabled);
    }

    private void ApplyTarget()
    {
        callbacks.ApplyTargetOverlay();
        RefreshSample();
    }

    // Same alpha scaling as the overlay window applies to its frame colours.
    private static System.Windows.Media.SolidColorBrush ScaledBrush(uint argb, double opacity)
    {
        uint alpha = (uint)Math.Round((argb >> 24) * opacity);
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(
            (byte)alpha, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }

    private void RefreshSample()
    {
        if (sampleFrame == null) return;
        double opacity = Math.Clamp(settings.TargetOverlayOpacity, 0.15, 1.0);
        sampleFrame.Background = ScaledBrush(settings.TargetOverlayBackgroundArgb, opacity);
        sampleFrame.BorderBrush = ScaledBrush(settings.TargetOverlayBorderArgb, opacity);
        sampleFrame.BorderThickness = new Thickness(Math.Clamp(settings.TargetOverlayBorderThickness, 0, 6));
        foreach (var text in sampleTexts)
        {
            uint argb = TargetOverlayWindow.NameArgb(text.Tag as RadarEntityKind?, settings.TargetOverlayTextArgb);
            text.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        }
    }

    private StackPanel BuildEnemyListPage()
    {
        var page = new StackPanel { Margin = new Thickness(10, 0, 10, 10) };
        page.Children.Add(MakeCheck(S.T("Show enemy list", "敵リストを表示"), settings.EnemyListEnabled, v =>
        {
            settings.EnemyListEnabled = v;
            callbacks.SyncEnemyList();
        }, asSwitch: true));

        Func<bool> listOn = () => settings.EnemyListEnabled;
        page.Children.Add(MakeSliderRow(S.T("Radius (m)", "半径 (m)"), settings.EffectiveEnemyListRadiusMeters, 1, 200,
            v => settings.EnemyListRadiusMeters = MathF.Round(v), v => $"{v:0} m", wholeNumbers: true, enabled: listOn));
        page.Children.Add(MakeComboRow(S.T("Sort", "並び順"), settings.EnemyListSort,
            [(EnemyListSortMode.Distance, S.T("Distance", "距離順")), (EnemyListSortMode.HpRatio, S.T("Lowest HP %", "HP割合の低い順"))],
            v => settings.EnemyListSort = v, enabled: listOn));
        page.Children.Add(MakeCheck(S.T("Limit rows (rest as +N)", "表示数を制限（超過分は+N）"), settings.EnemyListLimitRows,
            v => { settings.EnemyListLimitRows = v; callbacks.ApplyEnemyList(); }, enabled: listOn));
        page.Children.Add(MakeSliderRow(S.T("Rows", "表示数"), Math.Clamp(settings.EnemyListMaxRows, 1, 50), 1, 50,
            v => { settings.EnemyListMaxRows = (int)MathF.Round(v); callbacks.ApplyEnemyList(); }, v => $"{v:0}",
            wholeNumbers: true, enabled: () => settings.EnemyListEnabled && settings.EnemyListLimitRows));
        page.Children.Add(MakeCheck(S.T("Always on top", "常に最前面"), settings.EnemyListTopMost,
            v => { settings.EnemyListTopMost = v; callbacks.ApplyEnemyList(); }, enabled: listOn));
        page.Children.Add(MakeSliderRow(S.T("Opacity", "不透明度"), (float)settings.EnemyListOpacity, 0.15f, 1f,
            v => { settings.EnemyListOpacity = v; callbacks.ApplyEnemyList(); }, v => $"{v * 100:0}%", enabled: listOn));
        page.Children.Add(MakeSliderRow(S.T("Text size", "文字サイズ"), (float)settings.EnemyListFontSize, 9f, 24f,
            v => { settings.EnemyListFontSize = v; callbacks.ApplyEnemyList(); }, v => $"{v:0}", enabled: listOn));
        return page;
    }

    private StackPanel BuildGeneralPage()
    {
        var page = new StackPanel { Margin = new Thickness(10, 0, 10, 10) };
        page.Children.Add(MakeComboRow(S.T("Language", "言語"), settings.Language,
            [("en", "English"), ("ja", "日本語")], v => settings.Language = v, callbacks.LanguageChanged));

        page.Children.Add(MakeHeading(S.T("Shortcuts", "操作")));
        page.Children.Add(MakeNote(S.T("Ctrl+Alt+R: toggle click-through", "Ctrl+Alt+R: クリック透過の切替")));
        page.Children.Add(MakeNote(S.T("Mouse wheel on the map: zoom", "地図上のホイール: 拡大・縮小")));
        return page;
    }

    // ---- Row parts -------------------------------------------------------

    // A small accent title with a thin rule running to the right edge.
    private static FrameworkElement MakeHeading(string text, bool first = false)
    {
        var grid = new Grid { Margin = new Thickness(0, first ? 0 : 8, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hex(AccentColor),
        });
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(8, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Hex("#2E4045"),
        };
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);
        return grid;
    }

    private static TextBlock MakeNote(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Hex(SubTextColor),
        Margin = new Thickness(0, 2, 0, 2),
    };

    private static TextBlock MakeLabel(string text) => new()
    {
        Text = text,
        Width = LabelWidth,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Hex(TextColor),
    };

    private T Track<T>(T element, Func<bool>? enabled) where T : UIElement
    {
        if (enabled != null) enabledRules.Add((element, enabled));
        return element;
    }

    private CheckBox MakeCheck(string label, bool value, Action<bool> onChange, Func<bool>? enabled = null,
        Thickness? margin = null, bool asSwitch = false)
    {
        // The colour comes from the theme, which also greys it when disabled.
        var check = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Margin = margin ?? new Thickness(0, 2, 0, 2),
        };
        if (asSwitch) check.Style = (Style)SettingsTheme.Resources["SwitchCheckBox"];
        void Changed(bool v)
        {
            onChange(v);
            RadarSettings.Save();
            callbacks.AfterWindowChange();
            RefreshEnabled();
        }
        check.Checked += (_, _) => Changed(true);
        check.Unchecked += (_, _) => Changed(false);
        return Track(check, enabled);
    }

    private FrameworkElement MakeSliderRow(string label, float value, float min, float max, Action<float> onChange,
        Func<double, string> format, bool wholeNumbers = false, Func<bool>? enabled = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(MakeLabel(label));
        var slider = SettingSlider.Create(value, min, max, wholeNumbers);
        var current = new TextBlock
        {
            Width = 44,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hex("#9FC4C8"),
            Text = format(slider.Value),
        };
        slider.ValueChanged += (_, e) =>
        {
            onChange((float)e.NewValue);
            current.Text = format(e.NewValue);
            callbacks.QueueSave();
        };
        row.Children.Add(slider);
        row.Children.Add(current);
        return Track(row, enabled);
    }

    private FrameworkElement MakeComboRow<T>(string label, T value, (T Value, string Text)[] items, Action<T> set,
        Action? after = null, Func<bool>? enabled = null) where T : notnull
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(MakeLabel(label));
        var combo = new ComboBox { Width = ControlWidth };
        foreach (var (itemValue, text) in items) AddComboItem(combo, itemValue, text);
        SelectComboItem(combo, value);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T chosen })
            {
                set(chosen);
                RadarSettings.Save();
                after?.Invoke();
                RefreshEnabled();
            }
        };
        row.Children.Add(combo);
        return Track(row, enabled);
    }

    private static void AddComboItem(ComboBox combo, object tag, string text)
    {
        combo.Items.Add(new ComboBoxItem { Tag = tag, Content = text });
    }

    private static void SelectComboItem(ComboBox combo, object tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (Equals(item.Tag, tag))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    // Picking a colour updates the swatch borders in place. Rebuilding the
    // popup here used to scroll it back to the top on every pick.
    private FrameworkElement MakeColorRow(string label, Func<uint> get, Action<uint> set, bool allowTransparent = true,
        string? toolTip = null, uint[]? exclude = null, Func<bool>? enabled = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (toolTip != null) row.ToolTip = toolTip;
        row.Children.Add(MakeLabel(label));
        var swatches = new List<(Border Swatch, uint Argb)>();
        void MarkSelected()
        {
            foreach (var (swatch, argb) in swatches)
            {
                swatch.BorderBrush = new System.Windows.Media.SolidColorBrush(argb == get()
                    ? System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)
                    : System.Windows.Media.Color.FromRgb(0x55, 0x5F, 0x62));
            }
        }
        // Transparent text would make the overlay (and the enemy list, which
        // shares its RGB) unreadable.
        foreach (var argb in OverlayPalette.Where(argb =>
                     (allowTransparent || argb >> 24 != 0) && exclude?.Contains(argb) != true))
        {
            var color = System.Windows.Media.Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            var swatch = new Border
            {
                Width = 15, Height = 15, Margin = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    argb == get()
                        ? System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)
                        : System.Windows.Media.Color.FromRgb(0x55, 0x5F, 0x62)),
                Background = new System.Windows.Media.SolidColorBrush(color),
                ToolTip = argb == 0 ? "Transparent" : $"#{argb:X8}",
                Cursor = Cursors.Hand,
            };
            ToolTipService.SetInitialShowDelay(swatch, 0);
            var captured = argb;
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                set(captured);
                RadarSettings.Save();
                ApplyTarget();
                MarkSelected();
                e.Handled = true;
            };
            swatches.Add((swatch, argb));
            row.Children.Add(swatch);
        }
        MarkSelected();
        return Track(row, enabled);
    }
}
