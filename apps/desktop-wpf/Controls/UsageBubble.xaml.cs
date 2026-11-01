using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>In-window overlay only. No transparent full-surface input catcher or native popup.</summary>
public partial class UsageBubble : UserControl
{
    private UsageDto _snapshot = new();
    private bool _hasRead, _stale, _generating;
    private Point _position, _defaultPosition, _mouseStart, _dragOrigin;
    private bool _pressed, _dragged;
    private Window? _window;
    public bool IsExpanded => Details.Visibility == Visibility.Visible;
    public double CapsuleWidth => Capsule.ActualWidth;
    public event EventHandler? CapsuleResized;
    public event EventHandler? Hidden;

    public UsageBubble()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.PreviewMouseDown += Outside_MouseDown;
                _window.PreviewKeyDown += Window_KeyDown;
                _window.Deactivated += Window_Deactivated;
            }
            LanguageManager.LanguageChanged += Language_Changed;
            RefreshLabels();
            Reposition();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null)
            {
                _window.PreviewMouseDown -= Outside_MouseDown;
                _window.PreviewKeyDown -= Window_KeyDown;
                _window.Deactivated -= Window_Deactivated;
            }
            LanguageManager.LanguageChanged -= Language_Changed;
            Collapse();
        };
        Capsule.SizeChanged += (_, _) => { Reposition(); CapsuleResized?.Invoke(this, EventArgs.Empty); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) Collapse(); };
        Capsule.LostMouseCapture += (_, _) => { _pressed = false; _dragged = false; };
        RefreshLabels();
    }

    public void SetUsage(UsageDto snapshot, bool hasRead = true, bool stale = false, bool generating = false)
    {
        _snapshot = snapshot; _hasRead = hasRead; _stale = stale; _generating = generating;
        RefreshLabels();
    }

    public void SetDefaultPosition(Point point) { _defaultPosition = point; Reposition(); }
    internal Point Position => _position;
    internal Rect DetailBounds => new(Canvas.GetLeft(Details), Canvas.GetTop(Details), Details.ActualWidth, Details.ActualHeight);
    private Size Area => new(Math.Max(0, Surface.ActualWidth), Math.Max(0, Surface.ActualHeight));
    private Size CapsuleSize => new(Capsule.ActualWidth, Capsule.ActualHeight);

    private void RefreshLabels()
    {
        var rate = UsageDisplay.Rate(UiPrefs.Current.UsageCnyRate);
        var money = !_hasRead ? L.T("Usage.Pending") : _snapshot.Unknown_Price_Calls > 0 && _snapshot.Cost_Usd == 0
            ? L.T("Usage.Unknown") : UsageDisplay.Money(_snapshot.Cost_Usd, rate);
        Amount.Text = money + ((_stale && _hasRead) || (_snapshot.Unknown_Price_Calls > 0 && _snapshot.Cost_Usd > 0) ? " *" : "");
        ScopeLabel.Text = L.T(_generating ? "Usage.GeneratingShort" : "Usage.Short");
        DetailAmount.Text = money;
        TokenCount.Text = _hasRead ? UsageDisplay.Tokens(_snapshot.TotalTokens) + " Tokens" : "— Tokens";
        TotalValue.Text = _snapshot.TotalTokens.ToString("N0");
        InputValue.Text = _snapshot.Input_Tokens.ToString("N0");
        OutputValue.Text = _snapshot.Output_Tokens.ToString("N0");
        CachedValue.Text = _snapshot.Cached_Tokens.ToString("N0");
        var notices = new List<string>();
        if (_stale) notices.Add(L.T("Usage.Stale"));
        if (_generating) notices.Add(L.T("Usage.Generating"));
        if (_snapshot.Unknown_Price_Calls > 0) notices.Add(L.T("Usage.UnknownCalls", _snapshot.Unknown_Price_Calls));
        if (!_hasRead && !_stale) notices.Add(L.T("Usage.Loading"));
        if (_hasRead && _snapshot.Calls == 0) notices.Add(L.T("Usage.Empty"));
        Notice.Text = string.Join("\n", notices);
        Capsule.ToolTip = L.T("Usage.Tip") + (notices.Count > 0 ? "\n" + Notice.Text : "");
        Notice.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var last = _snapshot.Last;
        var purpose = last?.Purpose switch
        {
            "generate" => "Usage.Generate", "plan" or "proposal" => "Usage.Plan",
            "audit" or "plan_check" or "rhythm_check" => "Usage.Audit",
            "extract" or "summary" or "memory_extract" => "Usage.Extract", _ => "Usage.Call",
        };
        LastCall.Text = last is null ? L.T("Usage.NoCalls") : last.Model + " · " + L.T(purpose);
        LastCall.ToolTip = last is null ? null : last.Provider + " · " + last.Model + " · " + last.Purpose;
        LastAmount.Text = last is null ? "" : last.Price_Known ? UsageDisplay.Money(last.Cost_Usd, rate) : L.T("Usage.Unknown");
        if (!RateInput.IsKeyboardFocusWithin) RateInput.Text = rate.ToString("0.####", CultureInfo.CurrentCulture);
        System.Windows.Automation.AutomationProperties.SetName(Capsule,
            L.T("Usage.Title") + ", " + money + ", " + TokenCount.Text + (_stale ? ", " + L.T("Usage.Stale") : ""));
        if (IsExpanded) LayoutDetails();
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e) => Reposition();
    private void Language_Changed(object? sender, EventArgs e) { RefreshLabels(); Reposition(); }
    private void Window_Deactivated(object? sender, EventArgs e) => Collapse();

    private void Reposition()
    {
        if (Capsule is null || Capsule.ActualWidth <= 0 || Area.Width <= 0) return;
        var prefs = UiPrefs.Current;
        var remembered = prefs.UsageX is { } x && double.IsFinite(x) && prefs.UsageY is { } y && double.IsFinite(y);
        var point = remembered ? new Point(Math.Clamp(prefs.UsageX!.Value, 0, 1) * Math.Max(0, Area.Width - Capsule.ActualWidth),
            Math.Clamp(prefs.UsageY!.Value, 0, 1) * Math.Max(0, Area.Height - Capsule.ActualHeight)) : _defaultPosition;
        Place(point, false);
    }

    internal void Place(Point point, bool remember)
    {
        _position = UsageDisplay.Clamp(point, Area, CapsuleSize);
        Canvas.SetLeft(Capsule, _position.X); Canvas.SetTop(Capsule, _position.Y);
        if (remember)
        {
            UiPrefs.Current.UsageX = _position.X / Math.Max(1, Area.Width - Capsule.ActualWidth);
            UiPrefs.Current.UsageY = _position.Y / Math.Max(1, Area.Height - Capsule.ActualHeight);
            UiPrefs.Save();
        }
        if (IsExpanded) LayoutDetails();
    }

    internal void Expand()
    {
        Details.Visibility = Visibility.Visible;
        RateError.Visibility = Visibility.Collapsed;
        RefreshLabels();
        LayoutDetails();
    }

    public void Collapse() => Details.Visibility = Visibility.Collapsed;

    private void LayoutDetails()
    {
        Details.Width = Math.Max(0, Math.Min(320, Area.Width - 16));
        DetailScroll.MaxHeight = Math.Max(0, Area.Height - Capsule.ActualHeight - 24);
        Details.Measure(new Size(Details.Width, double.PositiveInfinity));
        var height = Details.DesiredSize.Height;
        var above = _position.Y - 8;
        var below = Area.Height - _position.Y - Capsule.ActualHeight - 8;
        var top = above >= height || above >= below ? _position.Y - height - 8 : _position.Y + Capsule.ActualHeight + 8;
        // Shorten the scrolling panel on the chosen side instead of covering the capsule.
        DetailScroll.MaxHeight = Math.Max(0, Math.Min(Area.Height - 24, Math.Max(above, below)) - 2);
        Details.Measure(new Size(Details.Width, double.PositiveInfinity));
        height = Details.DesiredSize.Height;
        top = above >= height || above >= below ? _position.Y - height - 8 : _position.Y + Capsule.ActualHeight + 8;
        var point = UsageDisplay.Clamp(new Point(_position.X + Capsule.ActualWidth - Details.Width, top), Area, new Size(Details.Width, height));
        Canvas.SetLeft(Details, point.X); Canvas.SetTop(Details, point.Y);
    }

    private void Capsule_Click(object sender, RoutedEventArgs e) { if (IsExpanded) Collapse(); else Expand(); }
    private void Collapse_Click(object sender, RoutedEventArgs e) { Collapse(); Capsule.Focus(); }
    private void Outside_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsExpanded && !Capsule.IsMouseOver && !Details.IsMouseOver) Collapse();
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsExpanded) { Collapse(); Capsule.Focus(); e.Handled = true; }
    }

    private void Drag_Start(object sender, MouseButtonEventArgs e)
    {
        _pressed = true; _dragged = false;
        _mouseStart = e.GetPosition(Surface); _dragOrigin = _position;
    }
    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(Surface) - _mouseStart;
        if (!_dragged && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragged = true; Capsule.CaptureMouse(); Collapse();
        Place(_dragOrigin + delta, false); e.Handled = true;
    }
    private void Drag_End(object sender, MouseButtonEventArgs e)
    {
        // Ordinary mouse-up belongs to ButtonBase: releasing its capture in the preview
        // route clears IsPressed before ButtonBase can raise Click.
        if (!_dragged) { _pressed = false; return; }
        Place(_position, true); e.Handled = true;
        _pressed = false; _dragged = false;
        if (Capsule.IsMouseCaptured) Capsule.ReleaseMouseCapture();
    }
    private void Capsule_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || e.SystemKey is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var delta = e.SystemKey switch { Key.Left => new Vector(-10, 0), Key.Right => new Vector(10, 0), Key.Up => new Vector(0, -10), _ => new Vector(0, 10) };
        Collapse(); Place(_position + delta, true); e.Handled = true;
    }
    public void ResetPosition()
    {
        UiPrefs.Current.UsageX = UiPrefs.Current.UsageY = null;
        UiPrefs.Save(); Collapse(); Reposition();
    }
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetPosition();
    private void Hide_Click(object sender, RoutedEventArgs e) { Collapse(); Hidden?.Invoke(this, EventArgs.Empty); }
    private void ApplyRate_Click(object sender, RoutedEventArgs e) => ApplyRate();
    private void Rate_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { ApplyRate(); e.Handled = true; } }
    private void ApplyRate()
    {
        if (!UsageDisplay.TryRate(RateInput.Text, out var value)) { RateError.Visibility = Visibility.Visible; LayoutDetails(); return; }
        UiPrefs.Current.UsageCnyRate = value; UiPrefs.Save();
        RateError.Visibility = Visibility.Collapsed; RefreshLabels();
    }
}
