using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Threading;
using NovelGm.Desktop;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop.Controls;

public sealed partial class ReadingView
{
    private readonly DispatcherTimer _settingsTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private ReadingAnchor? _settingsAnchor;
    private bool _settingsWired;
    private void ClosePopups() { _directoryPopup.IsOpen = _findPopup.IsOpen = false; }
    public bool CloseSettings()
    {
        var open = _directoryPopup.IsOpen || _findPopup.IsOpen || _settingsWindow?.IsVisible == true;
        ClosePopups(); if (_settingsWindow is not null) OwnedWindowFix.Hide(_settingsWindow); return open;
    }
    private void ChangedDisplay()
    {
        _settingsAnchor ??= CurrentAnchor();
        _store.Data.Preferences.Normalize(); _store.Save();
        if (!_settingsWired)
        {
            _settingsWired = true;
            _settingsTimer.Tick += (_, _) => { _settingsTimer.Stop(); var anchor = _settingsAnchor; _settingsAnchor = null; Render(anchor); };
        }
        _settingsTimer.Stop(); _settingsTimer.Start();
    }
    internal void OpenSettings()
    {
        if (_settingsWindow is not null) { _settingsWindow.Show(); _settingsWindow.Activate(); return; }
        var window = new Window { Title = L.T("Read.ReviewSettings"), Width = 580, Height = 760, MinWidth = 440, MinHeight = 500,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        window.SetResourceReference(Window.BackgroundProperty, "BgBrush"); window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        var tabs = new TabControl { Margin = new Thickness(18), BorderThickness = new Thickness(0) };
        tabs.SetResourceReference(Control.BackgroundProperty, "BgBrush");
        var appearance = new StackPanel { Margin = new Thickness(6, 12, 6, 12) };
        var speech = new StackPanel { Margin = new Thickness(6, 12, 6, 12) };
        tabs.Items.Add(new TabItem { Header = L.T("Read.Appearance"), Content = new ScrollViewer { Content = appearance, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = L.T("Read.Speech"), Content = new ScrollViewer { Content = speech, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var p = _store.Data.Preferences;
        SliderRow(appearance, "Read.FontSize", 12, 40, p.FontSize, 1, n => p.FontSize = n);
        var fonts = new ComboBox { MinHeight = 32, ItemsSource = new[] { L.T("Read.DefaultFont") }.Concat(Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s)).ToList(),
            SelectedItem = string.IsNullOrEmpty(p.FontFamily) ? L.T("Read.DefaultFont") : p.FontFamily };
        fonts.SelectionChanged += (_, _) => { p.FontFamily = fonts.SelectedIndex == 0 ? "" : fonts.SelectedItem?.ToString() ?? ""; ChangedDisplay(); };
        Row(appearance, "Read.FontFamily", fonts);
        var align = new ComboBox { ItemsSource = new[] { L.T("Fmt.AlignLeft"), L.T("Fmt.AlignCenter"), L.T("Fmt.AlignRight") }, MinHeight = 32,
            SelectedIndex = p.TitleAlignment == TextAlignment.Left ? 0 : p.TitleAlignment == TextAlignment.Right ? 2 : 1 };
        align.SelectionChanged += (_, _) => { p.TitleAlignment = align.SelectedIndex switch { 0 => TextAlignment.Left, 2 => TextAlignment.Right, _ => TextAlignment.Center }; ChangedDisplay(); };
        Row(appearance, "Read.TitleAlignment", align);
        SliderRow(appearance, "Read.LineSpacing", .6, 2.4, p.LineSpacing, .1, n => p.LineSpacing = n);
        SliderRow(appearance, "Read.ParagraphSpacing", 0, 3, p.ParagraphSpacing, .1, n => p.ParagraphSpacing = n);
        SliderRow(appearance, "Read.PageMargin", 0, 48, p.PageMargin, 1, n => p.PageMargin = n);
        NumberRow(appearance, "Read.Width", p.Width, 240, 600, n => p.Width = n);
        NumberRow(appearance, "Read.Height", p.Height, 360, 1200, n => p.Height = n);
        var hint = new TextBlock { Text = L.T("Read.ScaleHint"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 8) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); appearance.Children.Add(hint);
        var effects = new ComboBox { ItemsSource = new[] { L.T("Read.Cover"), L.T("Read.Scroll"), L.T("Read.NoAnimation") }, MinHeight = 32,
            SelectedIndex = p.TurnEffect == "cover" ? 0 : p.TurnEffect == "none" ? 2 : 1 };
        effects.SelectionChanged += (_, _) => { p.TurnEffect = effects.SelectedIndex switch { 0 => "cover", 2 => "none", _ => "scroll" }; ChangedDisplay(); };
        Row(appearance, "Read.TurnEffect", effects);
        var indent = new CheckBox { Content = L.T("Read.FirstIndent"), IsChecked = p.FirstLineIndent, Margin = new Thickness(0, 16, 0, 16) };
        indent.Click += (_, _) => { p.FirstLineIndent = indent.IsChecked == true; ChangedDisplay(); }; appearance.Children.Add(indent);
        var reset = Button("Read.ResetDisplay", (_, _) =>
        {
            var savedVoice = p.Voice; var rate = p.SpeechRate; var volume = p.SpeechVolume; var auto = p.AutoNext;
            _store.Data.Preferences = new() { Voice = savedVoice, SpeechRate = rate, SpeechVolume = volume, AutoNext = auto };
            _store.Save(); _settingsWindow = null; window.Close(); Render(CurrentAnchor()); OpenSettings();
        }); appearance.Children.Add(reset);

        var voices = new ComboBox { DisplayMemberPath = "Name", MinHeight = 32 };
        Row(speech, "Read.Voice", voices);
        var voiceNotice = new TextBlock { Text = L.T("Read.Loading"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) }; speech.Children.Add(voiceNotice);
        _ = LoadVoices();
        async Task LoadVoices()
        {
            var choices = await Speech().VoicesAsync();
            voices.ItemsSource = choices; voices.SelectedItem = choices.FirstOrDefault(v => v.Id == p.Voice)
                ?? choices.FirstOrDefault(v => v.Id.Contains("ZH-CN", StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
            voices.IsEnabled = choices.Count > 0; _play.IsEnabled = choices.Count > 0;
            voiceNotice.Text = choices.Count == 0 ? L.T("Read.NoVoice") : L.T("Read.LocalSpeech");
            voices.SelectionChanged += (_, _) => { p.Voice = (voices.SelectedItem as ReadingVoice)?.Id ?? ""; _store.Save(); StopSpeech(); };
        }
        SliderRow(speech, "Read.SpeechRate", -10, 10, p.SpeechRate, 1, n => { p.SpeechRate = (int)n; StopSpeech(); }, false);
        SliderRow(speech, "Read.SpeechVolume", 0, 100, p.SpeechVolume, 1, n => { p.SpeechVolume = (int)n; StopSpeech(); }, false);
        var autoNext = new CheckBox { Content = L.T("Read.AutoNext"), IsChecked = p.AutoNext, Margin = new Thickness(0, 16, 0, 16) };
        autoNext.Click += (_, _) => { p.AutoNext = autoNext.IsChecked == true; _store.Save(); }; speech.Children.Add(autoNext);
        var playRow = new WrapPanel(); playRow.Children.Add(Button("Read.Speak", async (_, _) => await ToggleSpeech()));
        playRow.Children.Add(Button("Read.StopSpeech", (_, _) => StopSpeech())); speech.Children.Add(playRow);
        window.Content = tabs; window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { OwnedWindowFix.Hide(window); e.Handled = true; } };
        window.Closed += (_, _) => { if (_settingsWindow == window) _settingsWindow = null; };
        _settingsWindow = window; window.Show();
    }
    private static void Row(Panel panel, string key, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(115) }); grid.ColumnDefinitions.Add(new());
        var label = new Label { Content = L.T(key), Target = control, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
        label.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        grid.Children.Add(label); Grid.SetColumn(control, 1); grid.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, L.T(key)); panel.Children.Add(grid);
    }
    private void SliderRow(Panel panel, string key, double min, double max, double value, double step, Action<double> set, bool display = true)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(48) });
        var number = new TextBlock { Text = value.ToString("0.#", CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        slider.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
              <Grid Height="28" Background="Transparent">
                <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
                  <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" Background="{DynamicResource AccentBrush}" CornerRadius="2"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
                  <Track.Thumb><Thumb Width="20" Height="20"><Thumb.Template><ControlTemplate TargetType="Thumb"><Ellipse Fill="{DynamicResource AccentBrush}"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                  <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" Background="{DynamicResource BorderBrushKey}" CornerRadius="2"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
                </Track>
              </Grid>
            </ControlTemplate>
            """);
        slider.ValueChanged += (_, _) => { set(slider.Value); number.Text = slider.Value.ToString("0.#", CultureInfo.CurrentCulture); if (display) ChangedDisplay(); else _store.Save(); };
        System.Windows.Automation.AutomationProperties.SetName(slider, L.T(key)); grid.Children.Add(slider); Grid.SetColumn(number, 1); grid.Children.Add(number); Row(panel, key, grid);
    }
    private void NumberRow(Panel panel, string key, double value, double min, double max, Action<double> set)
    {
        var input = new TextBox { Text = value.ToString("0"), MinHeight = 32 };
        void Apply()
        {
            if (double.TryParse(input.Text, out var v) && double.IsFinite(v)) { v = Math.Clamp(v, min, max); set(v); input.Text = v.ToString("0"); ChangedDisplay(); }
            else input.Text = value.ToString("0");
        }
        input.LostKeyboardFocus += (_, _) => Apply();
        input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Apply(); e.Handled = true; } }; Row(panel, key, input);
    }
}
