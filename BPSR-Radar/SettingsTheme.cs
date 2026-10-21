using System.Windows;
using System.Windows.Markup;

namespace BpsrRadar;

// Dark look for the settings popup's controls. Parsed from a string, once:
// the checks build the panel without an Application or pack URIs, so a .xaml
// file reference would not resolve there.
//
// Colours (shared tokens): face #121C20, frame #465F64, input face #1A2A2F
// (hover #22363C), input frame #36504F (hover/focus accent), accent #3FA7AD,
// text #E6F2F3, secondary text #9FC4C8, disabled #5E7377, track #2E4045.
internal static class SettingsTheme
{
    public static ResourceDictionary Resources { get; } = Load();

    // A theme that fails to parse must not stop the settings window from
    // opening: an empty dictionary leaves the controls in their default look.
    private static ResourceDictionary Load()
    {
        try { return (ResourceDictionary)XamlReader.Parse(ThemeXaml); }
        catch { return new ResourceDictionary(); }
    }

    private const string ThemeXaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <SolidColorBrush x:Key="FaceBrush" Color="#121C20"/>
    <SolidColorBrush x:Key="FrameBrush" Color="#465F64"/>
    <SolidColorBrush x:Key="InputFaceBrush" Color="#1A2A2F"/>
    <SolidColorBrush x:Key="InputHoverBrush" Color="#22363C"/>
    <SolidColorBrush x:Key="InputBorderBrush" Color="#36504F"/>
    <SolidColorBrush x:Key="AccentBrush" Color="#3FA7AD"/>
    <SolidColorBrush x:Key="TextBrush" Color="#E6F2F3"/>
    <SolidColorBrush x:Key="SubTextBrush" Color="#9FC4C8"/>
    <SolidColorBrush x:Key="DisabledBrush" Color="#5E7377"/>
    <SolidColorBrush x:Key="TrackBrush" Color="#2E4045"/>
    <SolidColorBrush x:Key="WhiteBrush" Color="#FFFFFF"/>

    <!-- Keyboard focus: WPF draws this only for keyboard navigation (never after
         a mouse click). A 1 px #E6F2F3 rounded frame just outside the control. -->
    <Style x:Key="FocusFrame">
        <Setter Property="Control.Template">
            <Setter.Value>
                <ControlTemplate>
                    <Border Margin="-2" CornerRadius="5" BorderThickness="1"
                            BorderBrush="{StaticResource TextBrush}" IsHitTestVisible="False"/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- CheckBox: a 16x16 box with a tick. -->
    <Style TargetType="CheckBox">
        <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
        <Setter Property="FocusVisualStyle" Value="{StaticResource FocusFrame}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                    <Grid Background="Transparent" SnapsToDevicePixels="True">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <Grid Width="16" Height="16" VerticalAlignment="Center">
                            <Border x:Name="box" CornerRadius="3"
                                    Background="{StaticResource InputFaceBrush}"
                                    BorderBrush="{StaticResource InputBorderBrush}" BorderThickness="1">
                                <Path x:Name="tick" Data="M3.5,7.8 L6.3,10.6 L11.5,4.6" Stroke="{StaticResource WhiteBrush}"
                                      StrokeThickness="1.8" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                                      StrokeLineJoin="Round" Visibility="Collapsed"/>
                            </Border>
                        </Grid>
                        <ContentPresenter x:Name="content" Grid.Column="1" Margin="6,0,0,0"
                                          VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="box" Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="box" Property="Background" Value="{StaticResource AccentBrush}"/>
                            <Setter TargetName="box" Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
                            <Setter TargetName="tick" Property="Visibility" Value="Visible"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="box" Property="BorderBrush" Value="{StaticResource DisabledBrush}"/>
                            <Setter TargetName="content" Property="TextElement.Foreground" Value="{StaticResource DisabledBrush}"/>
                        </Trigger>
                        <MultiTrigger>
                            <MultiTrigger.Conditions>
                                <Condition Property="IsChecked" Value="True"/>
                                <Condition Property="IsEnabled" Value="False"/>
                            </MultiTrigger.Conditions>
                            <Setter TargetName="box" Property="Background" Value="{StaticResource DisabledBrush}"/>
                        </MultiTrigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- The main on/off switch of a tab: a 30x16 track with a 12 dot. -->
    <Style x:Key="SwitchCheckBox" TargetType="CheckBox">
        <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
        <Setter Property="FocusVisualStyle" Value="{StaticResource FocusFrame}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                    <Grid Background="Transparent" SnapsToDevicePixels="True">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <Grid Width="30" Height="16" VerticalAlignment="Center">
                            <Border x:Name="track" CornerRadius="8" Background="{StaticResource TrackBrush}">
                                <Ellipse x:Name="thumb" Width="12" Height="12" Fill="{StaticResource WhiteBrush}"
                                         HorizontalAlignment="Left" Margin="2,0,0,0"/>
                            </Border>
                        </Grid>
                        <ContentPresenter x:Name="content" Grid.Column="1" Margin="8,0,0,0"
                                          VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="track" Property="Background" Value="{StaticResource AccentBrush}"/>
                            <Setter TargetName="thumb" Property="HorizontalAlignment" Value="Right"/>
                            <Setter TargetName="thumb" Property="Margin" Value="0,0,2,0"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="thumb" Property="Fill" Value="{StaticResource DisabledBrush}"/>
                            <Setter TargetName="content" Property="TextElement.Foreground" Value="{StaticResource DisabledBrush}"/>
                        </Trigger>
                        <MultiTrigger>
                            <MultiTrigger.Conditions>
                                <Condition Property="IsChecked" Value="True"/>
                                <Condition Property="IsEnabled" Value="False"/>
                            </MultiTrigger.Conditions>
                            <Setter TargetName="track" Property="Background" Value="{StaticResource DisabledBrush}"/>
                            <Setter TargetName="thumb" Property="Fill" Value="{StaticResource InputFaceBrush}"/>
                        </MultiTrigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ComboBox. The ToggleButton is only the click target (IsChecked is
         IsDropDownOpen); the look is on the template so one trigger set
         covers hover, open, focus and disabled. -->
    <Style x:Key="ComboItemStyle" TargetType="ComboBoxItem">
        <Setter Property="Foreground" Value="{StaticResource SubTextBrush}"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBoxItem">
                    <Border x:Name="bd" Background="Transparent" BorderBrush="Transparent"
                            BorderThickness="2,0,0,0" Padding="8,4,8,4" SnapsToDevicePixels="True">
                        <ContentPresenter VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="{StaticResource InputHoverBrush}"/>
                        </Trigger>
                        <Trigger Property="IsHighlighted" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="{StaticResource InputHoverBrush}"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
                            <Setter Property="Foreground" Value="{StaticResource WhiteBrush}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Foreground" Value="{StaticResource DisabledBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style TargetType="ComboBoxItem" BasedOn="{StaticResource ComboItemStyle}"/>

    <Style TargetType="ComboBox">
        <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="ItemContainerStyle" Value="{StaticResource ComboItemStyle}"/>
        <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBox">
                    <Grid x:Name="root" SnapsToDevicePixels="True" MinHeight="22">
                        <Border x:Name="bd" CornerRadius="4" BorderThickness="1"
                                Background="{StaticResource InputFaceBrush}"
                                BorderBrush="{StaticResource InputBorderBrush}"/>
                        <ContentPresenter x:Name="ContentSite" Margin="8,3,24,3" IsHitTestVisible="False"
                                          HorizontalAlignment="Left" VerticalAlignment="Center"
                                          Content="{TemplateBinding SelectionBoxItem}"
                                          ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                          ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}"/>
                        <Path x:Name="arrow" Data="M0,0 L4,4 L8,0" Stroke="{StaticResource SubTextBrush}"
                              StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                              StrokeLineJoin="Round" HorizontalAlignment="Right" VerticalAlignment="Center"
                              Margin="0,1,9,0" IsHitTestVisible="False"/>
                        <ToggleButton x:Name="toggle" Focusable="False" ClickMode="Press"
                                      IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                            <ToggleButton.Template>
                                <ControlTemplate TargetType="ToggleButton">
                                    <Border Background="Transparent"/>
                                </ControlTemplate>
                            </ToggleButton.Template>
                        </ToggleButton>
                        <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}"
                               AllowsTransparency="True" Focusable="False" PopupAnimation="None">
                            <Border MinWidth="{Binding ActualWidth, ElementName=root}"
                                    MaxHeight="{TemplateBinding MaxDropDownHeight}" Margin="0,2,0,0"
                                    Background="{StaticResource FaceBrush}" BorderBrush="{StaticResource FrameBrush}"
                                    BorderThickness="1" CornerRadius="4" SnapsToDevicePixels="True">
                                <ScrollViewer Margin="0,3,0,3" SnapsToDevicePixels="True">
                                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                                </ScrollViewer>
                            </Border>
                        </Popup>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="{StaticResource InputHoverBrush}"/>
                            <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
                        </Trigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                            <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
                        </Trigger>
                        <Trigger Property="IsDropDownOpen" Value="True">
                            <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="bd" Property="Background" Value="{StaticResource InputFaceBrush}"/>
                            <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource TrackBrush}"/>
                            <Setter TargetName="arrow" Property="Stroke" Value="{StaticResource DisabledBrush}"/>
                            <Setter Property="Foreground" Value="{StaticResource DisabledBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Slider (horizontal only): a 4 px rail, accent up to the value, a 14 px dot. -->
    <Style x:Key="SliderFillButton" TargetType="RepeatButton">
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="IsTabStop" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="RepeatButton">
                    <Grid Background="Transparent">
                        <Border x:Name="fill" Height="4" CornerRadius="2,0,0,2" VerticalAlignment="Center"
                                Background="{StaticResource AccentBrush}"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="fill" Property="Background" Value="{StaticResource DisabledBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style x:Key="SliderRestButton" TargetType="RepeatButton">
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="IsTabStop" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="RepeatButton">
                    <Grid Background="Transparent"/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style x:Key="SliderThumbStyle" TargetType="Thumb">
        <Setter Property="Width" Value="14"/>
        <Setter Property="Height" Value="14"/>
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Thumb">
                    <Grid>
                        <Ellipse x:Name="dot" Width="14" Height="14" Fill="{StaticResource TextBrush}"
                                 Stroke="{StaticResource AccentBrush}" StrokeThickness="1"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="dot" Property="Fill" Value="{StaticResource DisabledBrush}"/>
                            <Setter TargetName="dot" Property="Stroke" Value="{StaticResource DisabledBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style TargetType="Slider">
        <Setter Property="FocusVisualStyle" Value="{StaticResource FocusFrame}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Slider">
                    <Grid Height="20" Background="Transparent" SnapsToDevicePixels="True">
                        <Border Height="4" CornerRadius="2" VerticalAlignment="Center"
                                Background="{StaticResource TrackBrush}"/>
                        <Track x:Name="PART_Track" VerticalAlignment="Center">
                            <Track.DecreaseRepeatButton>
                                <RepeatButton Style="{StaticResource SliderFillButton}" Command="Slider.DecreaseLarge"/>
                            </Track.DecreaseRepeatButton>
                            <Track.IncreaseRepeatButton>
                                <RepeatButton Style="{StaticResource SliderRestButton}" Command="Slider.IncreaseLarge"/>
                            </Track.IncreaseRepeatButton>
                            <Track.Thumb>
                                <Thumb Style="{StaticResource SliderThumbStyle}"/>
                            </Track.Thumb>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ScrollBar (vertical): 8 px wide, thumb only. -->
    <Style x:Key="ScrollThumbStyle" TargetType="Thumb">
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Thumb">
                    <Border x:Name="bar" CornerRadius="4" Background="{StaticResource InputBorderBrush}"/>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="bar" Property="Background" Value="#4A6A6E"/>
                        </Trigger>
                        <Trigger Property="IsDragging" Value="True">
                            <Setter TargetName="bar" Property="Background" Value="#4A6A6E"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style x:Key="ScrollPageButton" TargetType="RepeatButton">
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="IsTabStop" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="RepeatButton">
                    <Border Background="Transparent"/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style TargetType="ScrollBar">
        <Setter Property="Width" Value="8"/>
        <Setter Property="MinWidth" Value="8"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                    <Grid Background="Transparent" SnapsToDevicePixels="True">
                        <Track x:Name="PART_Track" IsDirectionReversed="True">
                            <Track.DecreaseRepeatButton>
                                <RepeatButton Style="{StaticResource ScrollPageButton}" Command="ScrollBar.PageUpCommand"/>
                            </Track.DecreaseRepeatButton>
                            <Track.IncreaseRepeatButton>
                                <RepeatButton Style="{StaticResource ScrollPageButton}" Command="ScrollBar.PageDownCommand"/>
                            </Track.IncreaseRepeatButton>
                            <Track.Thumb>
                                <Thumb Style="{StaticResource ScrollThumbStyle}"/>
                            </Track.Thumb>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="ToolTip">
        <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ToolTip">
                    <Border Background="{StaticResource FaceBrush}" BorderBrush="{StaticResource FrameBrush}"
                            BorderThickness="1" CornerRadius="4" Padding="7,3,7,3">
                        <ContentPresenter/>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
""";
}
