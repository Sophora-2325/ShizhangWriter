using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop.Controls;

/// <summary>作用于某个 RichTextBox 当前选区的 Word 式文字操作栏。</summary>
public partial class FormatToolbar : UserControl
{
    public sealed record Choice(string Display, object Value);

    private enum Preset { Body, Heading1, Heading2, Quote, Caption }

    /// <summary>行距 1.0 对应的行高 = 字号 × 该系数（接近 Word 的单倍行距）。</summary>
    private const double SingleLine = 1.3;
    private const double PtToDip = 96.0 / 72.0;

    public static readonly Thickness BodyMargin = new(0, 0, 0, 8);

    private static readonly (string Name, string Family)[] CommonFonts =
    {
        ("微软雅黑", "Microsoft YaHei"), ("宋体", "SimSun"), ("黑体", "SimHei"),
        ("楷体", "KaiTi"), ("仿宋", "FangSong"), ("等线", "DengXian"),
    };

    private static readonly (string Name, double Pt)[] NamedSizes =
    {
        ("初号", 42), ("小初", 36), ("一号", 26), ("小一", 24), ("二号", 22), ("小二", 18),
        ("三号", 16), ("小三", 15), ("四号", 14), ("小四", 12), ("五号", 10.5), ("小五", 9),
    };

    private static readonly double[] PointSizes = { 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72 };

    private static readonly double[] Spacings = { 1.0, 1.15, 1.5, 2.0, 2.5, 3.0 };

    private static readonly string[] Palette =
    {
        "#262626", "#595959", "#8C8C8C", "#BFBFBF", "#8B4513", "#000000",
        "#F5222D", "#FA541C", "#FA8C16", "#FAAD14", "#A0D911", "#52C41A",
        "#13C2C2", "#1677FF", "#2F54EB", "#722ED1", "#EB2F96", "#C41D7F",
    };

    private static readonly (string Display, string Code)[] DictationLangs =
    {
        ("中文", "zh"), ("English", "en"), ("日本語", "ja"),
    };

    private RichTextBox? _target;
    private bool _syncing;
    private bool _applyingOverflow;
    private bool _compact;
    private bool _busyVisible;
    private bool _widthPaper;
    private ToolbarState _state = ToolbarState.Empty;
    private DictationState _dictationState = DictationState.Idle;
    private Window? _moreOwner;

    private readonly DispatcherTimer _recTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private DateTime _recStarted;

    public FormatToolbar() : this(true)
    {
    }

    public FormatToolbar(bool showFocus)
    {
        ShowFocusToggle = showFocus;
        InitializeComponent();
        FontCombo.ItemsSource = BuildFonts();
        SizeCombo.ItemsSource = NamedSizes.Select(s => new Choice(s.Name, s.Pt))
            .Concat(PointSizes.Select(p => new Choice(FormatPt(p), p))).ToList();
        SizeCombo.IsTextSearchEnabled = false;
        SpacingCombo.ItemsSource = Spacings.Select(s => new Choice(s.ToString("0.0#", CultureInfo.InvariantCulture), s)).ToList();
        var styles = BuildStyles();
        StyleCombo.ItemsSource = styles;
        MoreStyleCombo.ItemsSource = styles;
        DictationLangCombo.ItemsSource = DictationLangs.Select(l => new Choice(l.Display, l.Code)).ToList();
        SelectedDictationLang = NormalizeLang(UiPrefs.Current.SttLanguage);
        _recTimer.Tick += (_, _) => UpdateRecElapsed();
        _errorTimer.Tick += (_, _) => SetDictationState(DictationState.Idle);
        foreach (var hex in Palette)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
            brush.Freeze();
            var b = new Button { Style = (Style)Resources["Swatch"], Background = brush, ToolTip = hex };
            b.Click += (_, _) => PickColor(brush);
            SwatchGrid.Children.Add(b);
        }
        FocusGroup.Visibility = ShowFocusToggle ? Visibility.Visible : Visibility.Collapsed;
        SetWidthMode(PaperLayout.IsPaper(UiPrefs.Current.BodyWidthMode));
        SetState(ToolbarState.Empty);
        SetDictationState(DictationState.Idle);
        UpdateCommentState();
    }

    /// <summary>独立正文窗不显示「专注」。</summary>
    public bool ShowFocusToggle { get; }

    /// <summary>宿主在每次选区/章节/忙碌状态变化时调用。</summary>
    public void SetState(ToolbarState state)
    {
        _state = state;
        UpdateEnabled();
    }

    internal void ApplyOverflowForTest(double width) => ApplyOverflow(width);

    /// <summary>切换显示宽度后由宿主回写当前模式。</summary>
    public void SetWidthMode(bool paper)
    {
        _widthPaper = paper;
        WidthText.Text = L.T(paper ? "Fmt.WidthPaper" : "Fmt.WidthFull");
        WidthButton.IsChecked = paper;
        WidthButton.ToolTip = L.T("Fmt.WidthTip");
        AutomationProperties.SetName(WidthButton, WidthText.Text);
    }

    public void SetFocusMode(bool on)
    {
        FocusButton.IsChecked = on;
        FocusText.Text = L.T(on ? "Fmt.ExitFocus" : "Fmt.Focus");
        FocusButton.ToolTip = L.T(on ? "Fmt.ExitFocusTip" : "Fmt.FocusTip");
        AutomationProperties.SetName(FocusButton, FocusText.Text);
        if (RootBar.ActualWidth > 0) ApplyOverflow(RootBar.ActualWidth);
    }

    public bool IsDictating
    {
        get => _dictationState == DictationState.Recording;
        set => SetDictationState(value ? DictationState.Recording : DictationState.Idle);
    }

    /// <summary>录音 / 转写 / 失败状态；失败消息为空时用默认文案。</summary>
    public void SetDictationState(DictationState state, string? message = null)
    {
        _dictationState = state;
        _errorTimer.Stop();
        MicButton.IsChecked = state == DictationState.Recording;
        MicButton.IsEnabled = _state.Editable && _state.HasChapter && !_state.Busy && state != DictationState.Transcribing;
        DictationLangCombo.IsEnabled = _state.Editable && _state.HasChapter && !_state.Busy && state is DictationState.Idle or DictationState.Failed;
        StopButton.Visibility = state == DictationState.Recording ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = state == DictationState.Recording;
        RecIndicator.Visibility = state == DictationState.Recording ? Visibility.Visible : Visibility.Collapsed;
        if (state == DictationState.Recording)
        {
            if (!_recTimer.IsEnabled)
            {
                _recStarted = DateTime.UtcNow;
                RecElapsedText.Text = "0:00";
                _recTimer.Start();
            }
        }
        else
        {
            _recTimer.Stop();
            RecElapsedText.Text = "0:00";
        }

        DictationStatus.Text = state switch
        {
            DictationState.Transcribing => L.T("Stt.Transcribing"),
            DictationState.Failed => string.IsNullOrWhiteSpace(message) ? L.T("Stt.Failed") : message!,
            _ => "",
        };
        MicButton.ToolTip = state switch
        {
            DictationState.Recording => L.T("Stt.Recording"),
            DictationState.Transcribing => L.T("Stt.Transcribing"),
            DictationState.Failed => DictationStatus.Text,
            _ => L.T("Fmt.Dictation"),
        };
        UpdateDictationStatusVisibility();
        if (state == DictationState.Failed) _errorTimer.Start();
    }

    private void UpdateDictationStatusVisibility()
    {
        var show = _dictationState is DictationState.Transcribing or DictationState.Failed;
        // 极窄时保留麦克风/停止按钮，状态改由麦克风提示承载，避免挤出主行。
        DictationStatus.Visibility = show && !_compact ? Visibility.Visible : Visibility.Collapsed;
        RecElapsedText.Visibility = _dictationState == DictationState.Recording && !_compact
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>关闭“更多”和颜色浮层；返回是否确实关掉了东西。</summary>
    public bool CloseOverlays()
    {
        var closed = false;
        if (ColorPopup.IsOpen) { ColorPopup.IsOpen = false; ColorButton.IsChecked = false; closed = true; }
        if (MorePopup.IsOpen) { CloseMore(); closed = true; }
        return closed;
    }

    public bool HasOpenOverlay => MorePopup.IsOpen || ColorPopup.IsOpen;

    private void UpdateEnabled()
    {
        var editable = _state.Editable && _state.HasChapter && !_state.Busy;
        FocusButton.IsEnabled = true;
        WidthButton.IsEnabled = true;
        MoreButton.IsEnabled = true;
        SizeCombo.IsEnabled = editable;
        StyleCombo.IsEnabled = editable;
        FontCombo.IsEnabled = editable;
        SpacingCombo.IsEnabled = editable;
        MoreStyleCombo.IsEnabled = editable;
        IndentButton.IsEnabled = editable;
        BoldButton.IsEnabled = editable;
        ItalicButton.IsEnabled = editable;
        UnderlineButton.IsEnabled = editable;
        AlignLeftButton.IsEnabled = editable;
        AlignCenterButton.IsEnabled = editable;
        AlignRightButton.IsEnabled = editable;
        AlignJustifyButton.IsEnabled = editable;
        ColorButton.IsEnabled = editable;
        ImageButton.IsEnabled = editable;
        MoreImageButton.IsEnabled = editable;
        ParagraphButton.IsEnabled = editable;
        MicButton.IsEnabled = editable && _dictationState != DictationState.Transcribing;
        DictationLangCombo.IsEnabled = editable && _dictationState is DictationState.Idle or DictationState.Failed;
        CommentButton.IsEnabled = _state.Editable && !_state.Busy && _state.HasChapter && _state.CanComment;
        MoreCommentButton.IsEnabled = CommentButton.IsEnabled;
        HistoryButton.IsEnabled = _state.CanHistory;
        MoreHistoryButton.IsEnabled = _state.CanHistory;
        PublishButton.IsEnabled = _state.CanPublish;
        StopButton.IsEnabled = _dictationState == DictationState.Recording;
        UpdateCommentState();
    }

    private void UpdateCommentState()
    {
        var can = _state.Editable && !_state.Busy && _state.HasChapter && _state.CanComment;
        CommentButton.IsEnabled = can;
        MoreCommentButton.IsEnabled = can;
        var tip = can ? L.T("Fmt.CommentAdd") : L.T("Fmt.CommentNeedSelection");
        CommentButton.ToolTip = tip;
        MoreCommentButton.ToolTip = tip;
    }

    private void RootBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || e.NewSize.Width <= 0) return;
        ApplyOverflow(e.NewSize.Width);
    }

    private void ApplyOverflow(double width)
    {
        if (_applyingOverflow || width <= 0) return;
        _applyingOverflow = true;
        try
        {
            ApplyCompact(width);
            StyleGroup.Visibility = Visibility.Visible;
            ImageGroup.Visibility = Visibility.Visible;
            CommentGroup.Visibility = Visibility.Visible;
            HistoryGroup.Visibility = Visibility.Visible;
            UpdateMoreProxies();

            var available = Math.Max(0, width - RootBar.Padding.Left - RootBar.Padding.Right);
            double Total()
            {
                double total = 0;
                foreach (var group in new[]
                         {
                             FocusGroup, WidthGroup, SizeGroup, StyleGroup, ImageGroup,
                             DictationGroup, CommentGroup, HistoryGroup, MoreGroup,
                         })
                {
                    if (group.Visibility != Visibility.Visible) continue;
                    group.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    total += group.DesiredSize.Width;
                }
                return total;
            }

            foreach (var group in new[] { StyleGroup, ImageGroup, CommentGroup, HistoryGroup })
            {
                if (Total() <= available + 0.5) break;
                group.Visibility = Visibility.Collapsed;
            }
            UpdateMoreProxies();

            // 420 DIP 仍不够时再收一点内边距和字号列，不改变收纳顺序。
            if (Total() > available + 0.5)
            {
                FocusButton.Padding = new Thickness(2, 1, 2, 1);
                WidthButton.Padding = new Thickness(2, 1, 2, 1);
                MoreButton.Padding = new Thickness(2, 1, 2, 1);
                SizeCombo.Width = 66;
                SizeCombo.Padding = new Thickness(2, 1, 2, 1);
                DictationLangCombo.Width = 46;
                SetSeparatorMargin(new Thickness(4, 0, 4, 0));
                if (!_compact)
                {
                    _compact = true;
                    UpdateDictationStatusVisibility();
                }
            }
            // 极窄窗口（主窗最小宽度附近）：再收边框与分隔线，保证主行不越过容器。
            if (width < 460)
            {
                RootBar.Padding = new Thickness(4, 4, 4, 4);
                SetSeparatorMargin(new Thickness(2, 0, 2, 0));
                SizeCombo.Width = 66;
                DictationLangCombo.Width = 46;
                _compact = true;
                UpdateDictationStatusVisibility();
            }
            BusyHint.Visibility = _busyVisible && width >= 700 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _applyingOverflow = false;
        }
    }

    private void ApplyCompact(double width)
    {
        var compact = width < 700;
        if (compact == _compact && FocusButton.Padding.Left > 0) return;
        if (compact)
        {
            _compact = true;
            RootBar.Padding = new Thickness(6, 5, 6, 5);
            SizeCombo.Width = 70;
            SizeCombo.Padding = new Thickness(4, 2, 4, 2);
            DictationLangCombo.Width = 52;
            FocusButton.Padding = new Thickness(6, 3, 6, 3);
            WidthButton.Padding = new Thickness(6, 3, 6, 3);
            MoreButton.Padding = new Thickness(6, 3, 6, 3);
            SetSeparatorMargin(new Thickness(4, 0, 4, 0));
        }
        else
        {
            _compact = false;
            RootBar.Padding = new Thickness(10, 6, 10, 6);
            SizeCombo.Width = 76;
            SizeCombo.Padding = new Thickness(6, 2, 6, 2);
            DictationLangCombo.Width = 66;
            FocusButton.Padding = new Thickness(8, 4, 8, 4);
            WidthButton.Padding = new Thickness(8, 4, 8, 4);
            MoreButton.Padding = new Thickness(8, 4, 8, 4);
            SetSeparatorMargin(new Thickness(7, 0, 7, 0));
        }
        UpdateDictationStatusVisibility();
    }

    private void SetSeparatorMargin(Thickness margin)
    {
        foreach (var group in new[]
                 {
                     FocusGroup, WidthGroup, SizeGroup, StyleGroup, ImageGroup,
                     DictationGroup, CommentGroup, HistoryGroup, MoreGroup,
                 })
        {
            foreach (var border in group.Children.OfType<Border>())
                border.Margin = margin;
        }
    }

    private void UpdateMoreProxies()
    {
        MoreStyleGroup.Visibility = StyleGroup.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        MoreImageButton.Visibility = ImageGroup.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        MoreCommentButton.Visibility = CommentGroup.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        MoreHistoryButton.Visibility = HistoryGroup.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    public RichTextBox? Target
    {
        get => _target;
        set
        {
            if (_target is not null) _target.SelectionChanged -= Target_SelectionChanged;
            _target = value;
            if (_target is not null) _target.SelectionChanged += Target_SelectionChanged;
            Sync();
            UpdateCommentState();
        }
    }

    /// <summary>麦克风切换：true=开始录音，false=结束并转写。</summary>
    public event EventHandler<bool>? DictationToggled;

    /// <summary>听写语言变更（zh / en / ja）。</summary>
    public event EventHandler<string>? DictationLangChanged;

    public event EventHandler? InsertImageRequested;
    public event EventHandler? FocusModeToggleRequested;
    public event EventHandler? WidthModeToggleRequested;
    public event EventHandler? CommentAddRequested;
    public event EventHandler? HistoryRequested;
    public event EventHandler? PublishRequested;
    public event EventHandler? ParagraphSettingsRequested;

    private void Focus_Click(object sender, RoutedEventArgs e) =>
        FocusModeToggleRequested?.Invoke(this, EventArgs.Empty);

    private void Width_Click(object sender, RoutedEventArgs e) =>
        WidthModeToggleRequested?.Invoke(this, EventArgs.Empty);

    private void Comment_Click(object sender, RoutedEventArgs e)
    {
        CloseMore();
        CommentAddRequested?.Invoke(this, EventArgs.Empty);
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        CloseMore();
        HistoryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        CloseMore();
        PublishRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Paragraph_Click(object sender, RoutedEventArgs e)
    {
        CloseMore();
        ParagraphSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Image_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
        CloseMore();
        InsertImageRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void RequestImage() => Image_Click(this, new RoutedEventArgs());

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (MorePopup.IsOpen) CloseMore();
        else OpenMore();
    }

    private void OpenMore()
    {
        Sync();
        UpdateMoreProxies();
        MoreButton.IsChecked = true;
        MorePopup.IsOpen = true;
        AttachMoreOwner();
        if (FontCombo.IsEnabled) FontCombo.Focus();
        else if (PublishButton.IsEnabled) PublishButton.Focus();
    }

    private void CloseMore()
    {
        if (ColorPopup.IsOpen) { ColorPopup.IsOpen = false; ColorButton.IsChecked = false; }
        if (MorePopup.IsOpen) MorePopup.IsOpen = false;
        MoreButton.IsChecked = false;
        DetachMoreOwner();
    }

    private void MorePopup_Closed(object? sender, EventArgs e)
    {
        MoreButton.IsChecked = false;
        DetachMoreOwner();
    }

    private void AttachMoreOwner()
    {
        DetachMoreOwner();
        _moreOwner = Window.GetWindow(this);
        if (_moreOwner is null) return;
        _moreOwner.PreviewMouseDown += MoreOwner_PreviewMouseDown;
        _moreOwner.PreviewKeyDown += MoreOwner_PreviewKeyDown;
        _moreOwner.Deactivated += MoreOwner_Deactivated;
    }

    private void DetachMoreOwner()
    {
        if (_moreOwner is null) return;
        _moreOwner.PreviewMouseDown -= MoreOwner_PreviewMouseDown;
        _moreOwner.PreviewKeyDown -= MoreOwner_PreviewKeyDown;
        _moreOwner.Deactivated -= MoreOwner_Deactivated;
        _moreOwner = null;
    }

    private void MoreOwner_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!MorePopup.IsOpen) return;
        if (MoreButton.IsMouseOver) return;
        if (MorePopup.Child is UIElement child && child.IsMouseOver) return;
        CloseMore();
    }

    private void MoreOwner_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!MorePopup.IsOpen || e.Key != Key.Escape) return;
        CloseMore();
        e.Handled = true;
    }

    private void MoreOwner_Deactivated(object? sender, EventArgs e) => CloseMore();

    public void ShowBusy(string label)
    {
        BusyHint.Text = label;
        _busyVisible = true;
        BusyHint.Visibility = ActualWidth >= 700 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void HideBusy()
    {
        BusyHint.Text = "";
        _busyVisible = false;
        BusyHint.Visibility = Visibility.Collapsed;
    }

    private void ApplyStyle(Choice choice)
    {
        if (_target is null || choice.Value is not Preset preset) return;
        var range = WholeParagraphs();
        var body = _target.FontSize;
        var (size, bold, italic, align, margin) = preset switch
        {
            Preset.Heading1 => (HeadingSize(preset), true, false, TextAlignment.Center, new Thickness(0, 8, 0, 12)),
            Preset.Heading2 => (HeadingSize(preset), true, false, TextAlignment.Left, new Thickness(0, 6, 0, 8)),
            Preset.Quote => (body, false, true, TextAlignment.Left, new Thickness(24, 4, 0, 8)),
            Preset.Caption => (Math.Max(12, body - 2), false, true, TextAlignment.Center, new Thickness(0, 0, 0, 16)),
            _ => (body, false, false, TextAlignment.Left, BodyMargin),
        };
        range.ApplyPropertyValue(TextElement.FontSizeProperty, size);
        range.ApplyPropertyValue(TextElement.FontWeightProperty, bold ? FontWeights.Bold : FontWeights.Normal);
        range.ApplyPropertyValue(TextElement.FontStyleProperty, italic ? FontStyles.Italic : FontStyles.Normal);
        range.ApplyPropertyValue(TextElement.ForegroundProperty,
            preset == Preset.Quote ? new SolidColorBrush(Color.FromRgb(0x59, 0x59, 0x59)) : _target.Foreground);
        range.ApplyPropertyValue(Paragraph.TextAlignmentProperty, align);
        range.ApplyPropertyValue(Block.MarginProperty, margin);
        range.ApplyPropertyValue(Paragraph.TextIndentProperty, preset == Preset.Body ? body * 2 : 0.0);
        _target.Focus();
    }

    internal UIElement BuildFormattingRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var selection = _target?.Selection;
        void Add(string label, string key, RoutedUICommand command, bool active)
        {
            var glyph = new TextBlock
            {
                Text = label,
                FontWeight = label == "B" ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = label == "I" ? FontStyles.Italic : FontStyles.Normal,
                FontSize = 15,
            };
            if (label == "U") glyph.TextDecorations = TextDecorations.Underline;
            var button = new ToggleButton
            {
                Content = glyph, IsChecked = active, Focusable = false,
                Width = 32, Height = 30, MinHeight = 30, Padding = new Thickness(4), Margin = new Thickness(1),
                IsEnabled = CanEdit, ToolTip = L.T(key), Style = (Style)Resources["ToolToggle"],
            };
            System.Windows.Automation.AutomationProperties.SetName(button, L.T(key));
            button.Click += (_, _) => Toggle(command);
            row.Children.Add(button);
        }
        Add("B", "Fmt.Bold", EditingCommands.ToggleBold, selection?.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600);
        Add("I", "Fmt.Italic", EditingCommands.ToggleItalic, selection?.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle s && s != FontStyles.Normal);
        Add("U", "Fmt.Underline", EditingCommands.ToggleUnderline, selection?.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection d && d.Count > 0);
        var color = new Button
        {
            Content = "A", Width = 36, Height = 30, MinHeight = 30, Padding = new Thickness(4), Margin = new Thickness(6, 1, 1, 1),
            Focusable = false, IsEnabled = CanEdit, Foreground = ColorBar.Fill, ToolTip = L.T("Fmt.Color"),
            Style = (Style)Resources["ToolIconButton"],
        };
        System.Windows.Automation.AutomationProperties.SetName(color, L.T("Fmt.Color"));
        color.Click += (_, _) => { ColorPopup.PlacementTarget = color; ColorPopup.IsOpen = true; };
        row.Children.Add(color);
        return row;
    }

    public string SelectedDictationLang
    {
        get => DictationLangCombo.SelectedItem is Choice c ? (string)c.Value : "zh";
        set
        {
            var code = NormalizeLang(value);
            _syncing = true;
            try
            {
                DictationLangCombo.SelectedItem = DictationLangCombo.Items.Cast<Choice>()
                    .FirstOrDefault(c => (string)c.Value == code);
            }
            finally
            {
                _syncing = false;
            }
        }
    }

    public static string NormalizeLang(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        return c is "zh" or "en" or "ja" ? c : "zh";
    }

    private void UpdateRecElapsed()
    {
        var secs = Math.Max(0, (int)(DateTime.UtcNow - _recStarted).TotalSeconds);
        RecElapsedText.Text = $"{secs / 60}:{secs % 60:D2}";
    }

    private bool CanEdit => _target is { IsReadOnly: false };

    private static List<Choice> BuildStyles() => new()
    {
        new(L.T("Fmt.StyleBody"), Preset.Body),
        new(L.T("Fmt.StyleH1"), Preset.Heading1),
        new(L.T("Fmt.StyleH2"), Preset.Heading2),
        new(L.T("Fmt.StyleQuote"), Preset.Quote),
        new(L.T("Fmt.StyleCaption"), Preset.Caption),
    };

    /// <summary>切换界面语言后重建样式下拉框的文字。</summary>
    public void RefreshTexts()
    {
        var preset = (StyleCombo.SelectedItem as Choice)?.Value;
        var styles = BuildStyles();
        _syncing = true;
        try
        {
            StyleCombo.ItemsSource = styles;
            MoreStyleCombo.ItemsSource = styles;
            if (preset is Preset p)
            {
                var item = styles.FirstOrDefault(c => (Preset)c.Value == p);
                StyleCombo.SelectedItem = item;
                MoreStyleCombo.SelectedItem = item;
            }
        }
        finally
        {
            _syncing = false;
        }
        SetWidthMode(_widthPaper);
        SetFocusMode(FocusButton.IsChecked == true);
        UpdateCommentState();
        Sync();
    }

    private static List<Choice> BuildFonts()
    {
        var installed = Fonts.SystemFontFamilies
            .GroupBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var list = new List<Choice>();
        foreach (var (name, family) in CommonFonts)
            if (installed.TryGetValue(family, out var f)) list.Add(new Choice(name, f));
        var zh = XmlLanguage.GetLanguage("zh-cn");
        list.AddRange(installed.Values
            .Where(f => !CommonFonts.Any(c => c.Family.Equals(f.Source, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new Choice(f.FamilyNames.TryGetValue(zh, out var n) ? n : f.Source, f))
            .OrderBy(c => c.Display, StringComparer.CurrentCulture));
        return list;
    }

    private static string FormatPt(double pt) => pt.ToString("0.#", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ state from selection

    private void Target_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_paintArmed && !_syncing && CanEdit && _target!.Selection.IsEmpty == false)
            ApplyPainter();
        Sync();
        UpdateCommentState();
        _ = e;
    }

    private bool _paintArmed;
    private readonly Dictionary<DependencyProperty, object> _paint = new();

    public void ArmPainter()
    {
        if (!CanEdit) return;
        var sel = _target!.Selection;
        object Take(DependencyProperty property) => sel.GetPropertyValue(property);
        _paint[TextElement.FontFamilyProperty] = Take(TextElement.FontFamilyProperty);
        _paint[TextElement.FontSizeProperty] = Take(TextElement.FontSizeProperty);
        _paint[TextElement.FontWeightProperty] = Take(TextElement.FontWeightProperty);
        _paint[TextElement.FontStyleProperty] = Take(TextElement.FontStyleProperty);
        _paint[Inline.TextDecorationsProperty] = Take(Inline.TextDecorationsProperty);
        _paint[TextElement.ForegroundProperty] = Take(TextElement.ForegroundProperty);
        _paint[Paragraph.TextAlignmentProperty] = Take(Paragraph.TextAlignmentProperty);
        _paint[Block.MarginProperty] = Take(Block.MarginProperty);
        _paint[Paragraph.TextIndentProperty] = Take(Paragraph.TextIndentProperty);
        _paintArmed = true;
    }

    public void ApplyPainter()
    {
        if (!_paintArmed || !CanEdit) return;
        _paintArmed = false;
        var range = WholeParagraphs();
        foreach (var pair in _paint)
        {
            if (pair.Value is DependencyProperty || pair.Value == DependencyProperty.UnsetValue) continue;
            range.ApplyPropertyValue(pair.Key, pair.Value);
        }
        Sync();
    }

    public void ClearFormatting()
    {
        if (!CanEdit) return;
        MatchBody(WholeParagraphs());
        _target!.Focus();
        Sync();
    }

    public void MatchBody(TextRange range)
    {
        if (!CanEdit) return;
        range.ApplyPropertyValue(TextElement.FontFamilyProperty, _target!.FontFamily);
        range.ApplyPropertyValue(TextElement.FontSizeProperty, _target.FontSize);
        range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
        range.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
        range.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
        range.ApplyPropertyValue(TextElement.ForegroundProperty, _target.Foreground);
        range.ApplyPropertyValue(Paragraph.TextAlignmentProperty, TextAlignment.Left);
        range.ApplyPropertyValue(Block.MarginProperty, BodyMargin);
        range.ApplyPropertyValue(Paragraph.TextIndentProperty, _target.FontSize * 2);
    }

    public void ApplyBox(double before, double after, double left, double right)
    {
        if (!CanEdit) return;
        ApplyParagraph(Block.MarginProperty, new Thickness(left, before, right, after));
        _target!.Focus();
        Sync();
    }

    public void Sync()
    {
        if (_target is null) return;
        _syncing = true;
        try
        {
            var sel = _target.Selection;

            var family = sel.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily;
            var first = family?.Source.Split(',')[0].Trim();
            FontCombo.SelectedItem = FontCombo.Items.Cast<Choice>()
                .FirstOrDefault(c => ((FontFamily)c.Value).Source.Equals(first, StringComparison.OrdinalIgnoreCase));

            if (sel.GetPropertyValue(TextElement.FontSizeProperty) is double dip)
            {
                var pt = Math.Round(dip / PtToDip, 1);
                var match = SizeCombo.Items.Cast<Choice>().FirstOrDefault(c => Math.Abs((double)c.Value - pt) < 0.05);
                SizeCombo.SelectedItem = match;
                SizeCombo.Text = match?.Display ?? FormatPt(pt);
            }
            else
            {
                SizeCombo.SelectedItem = null;
                SizeCombo.Text = "";
            }

            BoldButton.IsChecked = sel.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600;
            ItalicButton.IsChecked = sel.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle s && s != FontStyles.Normal;
            UnderlineButton.IsChecked = sel.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection d
                                        && d.Any(x => x.Location == TextDecorationLocation.Underline);

            var align = sel.GetPropertyValue(Paragraph.TextAlignmentProperty) as TextAlignment?;
            AlignLeftButton.IsChecked = align == TextAlignment.Left;
            AlignCenterButton.IsChecked = align == TextAlignment.Center;
            AlignRightButton.IsChecked = align == TextAlignment.Right;
            AlignJustifyButton.IsChecked = align == TextAlignment.Justify;
            IndentButton.IsChecked = sel.GetPropertyValue(Paragraph.TextIndentProperty) is double indent && indent > 0.5;

            var fs = CurrentFontSize();
            var factor = sel.GetPropertyValue(Block.LineHeightProperty) is double lh && !double.IsNaN(lh)
                ? lh / (fs * SingleLine)
                : 1.0;
            SpacingCombo.SelectedItem = SpacingCombo.Items.Cast<Choice>()
                .OrderBy(c => Math.Abs((double)c.Value - factor)).First();

            var preset = DetectPreset();
            var styleItem = StyleCombo.Items.Cast<Choice>().FirstOrDefault(c => (Preset)c.Value == preset);
            StyleCombo.SelectedItem = styleItem;
            MoreStyleCombo.SelectedItem = MoreStyleCombo.Items.Cast<Choice>()
                .FirstOrDefault(c => (Preset)c.Value == preset);
        }
        finally
        {
            _syncing = false;
        }
    }

    private double CurrentFontSize() =>
        _target?.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double d ? d : _target?.FontSize ?? 16;

    private Preset? DetectPreset()
    {
        if (_target is null) return null;
        var sel = _target.Selection;
        var bold = BoldButton.IsChecked == true;
        var size = sel.GetPropertyValue(TextElement.FontSizeProperty) as double?;
        if (bold && size >= HeadingSize(Preset.Heading1) - 0.5) return Preset.Heading1;
        if (bold && size >= HeadingSize(Preset.Heading2) - 0.5) return Preset.Heading2;
        var paragraph = sel.Start.Paragraph;
        if (ItalicButton.IsChecked == true && paragraph?.TextAlignment == TextAlignment.Center && paragraph.TextIndent == 0)
            return Preset.Caption;
        if (ItalicButton.IsChecked == true && paragraph?.Margin.Left >= 20) return Preset.Quote;
        if (!bold && size is double s && Math.Abs(s - _target.FontSize) < 0.5) return Preset.Body;
        return null;
    }

    private static double HeadingSize(Preset p) => p switch
    {
        Preset.Heading1 => 22 * PtToDip,
        Preset.Heading2 => 16 * PtToDip,
        _ => 0,
    };

    // ------------------------------------------------------------------ apply

    private void ApplyChar(DependencyProperty property, object value)
    {
        if (!CanEdit) return;
        _target!.Selection.ApplyPropertyValue(property, value);
        _target.Focus();
        Sync();
    }

    private TextRange WholeParagraphs()
    {
        var sel = _target!.Selection;
        var start = sel.Start.Paragraph?.ContentStart ?? sel.Start;
        var end = sel.End.Paragraph?.ContentEnd ?? sel.End;
        return new TextRange(start, end);
    }

    private void ApplyParagraph(DependencyProperty property, object value) =>
        WholeParagraphs().ApplyPropertyValue(property, value);

    private void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || FontCombo.SelectedItem is not Choice c) return;
        ApplyChar(TextElement.FontFamilyProperty, c.Value);
    }

    private void SizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SizeCombo.SelectedItem is not Choice c) return;
        ApplyChar(TextElement.FontSizeProperty, (double)c.Value * PtToDip);
    }

    private void SizeCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyTypedSize();
    }

    private void SizeCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!SizeCombo.IsKeyboardFocusWithin) ApplyTypedSize();
    }

    private void ApplyTypedSize()
    {
        if (_syncing || !CanEdit) return;
        var text = SizeCombo.Text.Trim();
        double pt;
        var named = SizeCombo.Items.Cast<Choice>().FirstOrDefault(c => c.Display == text);
        if (named is not null) pt = (double)named.Value;
        else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out pt) || pt < 1 || pt > 400)
        {
            Sync();
            return;
        }
        var current = CurrentFontSize() / PtToDip;
        if (Math.Abs(current - pt) < 0.05) return;
        ApplyChar(TextElement.FontSizeProperty, pt * PtToDip);
    }

    private void Bold_Click(object sender, RoutedEventArgs e) => Toggle(EditingCommands.ToggleBold);
    private void Italic_Click(object sender, RoutedEventArgs e) => Toggle(EditingCommands.ToggleItalic);
    private void Underline_Click(object sender, RoutedEventArgs e) => Toggle(EditingCommands.ToggleUnderline);

    private void Toggle(RoutedUICommand command)
    {
        if (!CanEdit) { Sync(); return; }
        command.Execute(null, _target);
        _target!.Focus();
        Sync();
    }

    private void Color_Click(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = ColorButton.IsChecked == true;

    private void ColorPopup_Closed(object? sender, EventArgs e)
    {
        if (!ColorButton.IsMouseOver) ColorButton.IsChecked = false;
    }

    private void PickColor(Brush brush)
    {
        ColorBar.Fill = brush;
        ColorPopup.IsOpen = false;
        ColorButton.IsChecked = false;
        ApplyChar(TextElement.ForegroundProperty, brush);
    }

    private void AutoColor_Click(object sender, RoutedEventArgs e)
    {
        if (_target is not null) PickColor(_target.Foreground);
    }

    private void Align_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || sender is not FrameworkElement { Tag: string tag }) { Sync(); return; }
        ApplyParagraph(Paragraph.TextAlignmentProperty, Enum.Parse<TextAlignment>(tag));
        _target!.Focus();
        Sync();
    }

    private void Indent_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) { Sync(); return; }
        var on = _target!.Selection.GetPropertyValue(Paragraph.TextIndentProperty) is double d && d > 0.5;
        ApplyParagraph(Paragraph.TextIndentProperty, on ? 0.0 : CurrentFontSize() * 2);
        _target.Focus();
        Sync();
    }

    private void SpacingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !CanEdit || SpacingCombo.SelectedItem is not Choice c) return;
        var factor = (double)c.Value;
        ApplyParagraph(Block.LineHeightProperty, factor <= 1.0 ? double.NaN : CurrentFontSize() * SingleLine * factor);
        _target!.Focus();
        Sync();
    }

    private void StyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !CanEdit || StyleCombo.SelectedItem is not Choice c) return;
        ApplyStyle(c);
        Sync();
    }

    private void MoreStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !CanEdit || MoreStyleCombo.SelectedItem is not Choice c) return;
        ApplyStyle(c);
        _syncing = true;
        try
        {
            StyleCombo.SelectedItem = StyleCombo.Items.Cast<Choice>()
                .FirstOrDefault(x => (Preset)x.Value == (Preset)c.Value);
        }
        finally
        {
            _syncing = false;
        }
        Sync();
    }

    private void Mic_Click(object sender, RoutedEventArgs e) =>
        DictationToggled?.Invoke(this, MicButton.IsChecked == true);

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        MicButton.IsChecked = false;
        DictationToggled?.Invoke(this, false);
    }

    private void DictationLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || DictationLangCombo.SelectedItem is not Choice c) return;
        var code = NormalizeLang((string)c.Value);
        if (UiPrefs.Current.SttLanguage != code)
        {
            UiPrefs.Current.SttLanguage = code;
            UiPrefs.Save();
        }
        DictationLangChanged?.Invoke(this, code);
    }
}
