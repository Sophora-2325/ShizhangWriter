using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NovelGm.Desktop;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;
namespace NovelGm.Desktop.Controls;

/// <summary>Independent phone pagination. Authoring state never belongs to this view.</summary>
public sealed partial class ReadingView : UserControl
{
    private readonly FlowDocumentPageViewer _viewer = new();
    private readonly Border _paper = new() { BorderThickness = new Thickness(4), CornerRadius = new CornerRadius(20) };
    private readonly TextBlock _work = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private readonly TextBlock _wordCount = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _progress = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _clock = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12,0,12,6), FontSize = 11 };
    private readonly Grid _pageSurface = new() { ClipToBounds = true };
    private readonly Image _outgoing = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Button _prev, _next, _display, _play;
    /// <summary>顶部工具栏 / 提示 / 底部导航之间剩下的区域；手机页在这一块里居中。</summary>
    private readonly Grid _stage = new();
    private readonly ReadingPositionStore _store;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, ReadingAnchor> _inMemory = new();
    private const int MaxLocateQuote = 2000;
    private ChapterDto? _chapter;
    private FlowDocument? _source;
    private DynamicDocumentPaginator? _paginator;
    private string _projectId = "", _root = "", _text = "";
    private TextPointer? _start, _end;
    private int _index, _count, _generation;
    private bool _restoring;
    private Window? _settingsWindow;
    private ReadingSpeech? _speech;
    private bool _speaking, _speechPaused, _continueSpeech;
    private int _speechOffset;
    private long _speechRequest;
    private ReadingAnchor? _pausedSpeechAnchor;
    public string ChapterKey { get; private set; } = "";
    public int? ChapterNo => _chapter?.No;
    public string ProjectId => _projectId;
    public event Action<int>? Navigate;
    public event Action<int>? Jump;
    public event Action<ReadingAnchor>? Modify;
    public event Action<ReviewLocateRequest>? Locate;
    public event Action? RefreshRequested;
    public Func<bool>? CanSpeak { get; set; }
    internal FlowDocument Snapshot => (FlowDocument)_viewer.Document;
    internal int PageCount => _viewer.PageCount;
    internal int PageNumber => _viewer.MasterPageNumber;
    /// <summary>手机页在 ReadingView 坐标系里（含 Viewbox 缩放）的实际边界。</summary>
    internal Rect PaperBounds
    {
        get
        {
            try
            {
                if (_paper.ActualWidth <= 0 || _paper.ActualHeight <= 0) return Rect.Empty;
                return _paper.TransformToAncestor(this).TransformBounds(new Rect(_paper.RenderSize));
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return Rect.Empty; }
        }
    }
    /// <summary>手机页可用的剩余区域（工具栏 / 提示 / 章节导航之外）在 ReadingView 坐标系里的边界。</summary>
    internal Rect ReadingAreaBounds
    {
        get
        {
            try
            {
                if (_stage.ActualWidth <= 0 || _stage.ActualHeight <= 0) return Rect.Empty;
                return _stage.TransformToAncestor(this).TransformBounds(new Rect(_stage.RenderSize));
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return Rect.Empty; }
        }
    }
    public void FocusReading() => _viewer.Focus();
    public void Clear()
    {
        Pause(); _source = null; _chapter = null; ChapterKey = ""; _projectId = ""; _start = _end = null; _index = -1; _count = 0;
        _viewer.Document = new FlowDocument(new Paragraph(new Run(L.T("Read.Empty")))) { FontSize = 20 };
        _work.Text = _wordCount.Text = _progress.Text = ""; SetNavigating(true);
    }
    public void ShowNotice(string text) => _notice.Text = text;
    public void SetNavigating(bool loading)
    {
        _prev.IsEnabled = !loading && _index > 0; _next.IsEnabled = !loading && _index < _count - 1;
        if (loading) ShowNotice(L.T("Read.Loading"));
        else if (_notice.Text == L.T("Read.Loading")) ShowNotice("");
    }
    public ReadingView() : this(null) { }
    public ReadingView(ReadingPositionStore? store)
    {
        _store = store ?? new(); Focusable = true;
        SetResourceReference(Control.ForegroundProperty, "TextBrush");
        var layout = new DockPanel(); layout.SetResourceReference(Panel.BackgroundProperty, "PageBrush");
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(10,8,10,8) };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 17, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.TextProperty, "Read.Review"); DockPanel.SetDock(label, Dock.Left); header.Children.Add(label);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Icon("Read.Locate", "M10,0 V5 M10,15 V20 M0,10 H5 M15,10 H20 M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10", (_,_) => LocateCurrent()));
        _play = Icon("Read.Speak", "M2,7 H6 L11,3 V17 L6,13 H2 Z M14,6 C18,8 18,12 14,14 M16,3 C23,7 23,13 16,17", async (_,_) => await ToggleSpeech()); actions.Children.Add(_play);
        actions.Children.Add(Icon("Read.Refresh", "M17,7 A8,8 0 1 0 18,13 M17,1 V7 H11", (_,_) => RefreshRequested?.Invoke()));
        _display = Icon("Read.ReviewSettings", "M7,1 H13 L17,5 V13 L13,17 H7 L3,13 V5 Z M13,9 A3,3 0 1 1 7,9 A3,3 0 1 1 13,9", (_,_) => OpenSettings()); actions.Children.Add(_display);
        var more = Icon("Read.More", "M3,9 H4 M9,9 H10 M15,9 H16", (sender,_) => OpenMore((Button)sender));
        _directoryButton = _findButton = more; actions.Children.Add(more);
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        DockPanel.SetDock(_notice, Dock.Top); _notice.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); layout.Children.Add(_notice);
        var nav = new Grid { Margin = new Thickness(8,6,8,8) }; nav.ColumnDefinitions.Add(new()); nav.ColumnDefinitions.Add(new());
        _prev = Button("Read.Prev", (_,_) => Navigate?.Invoke(-1)); _next = Button("Read.Next", (_,_) => Navigate?.Invoke(1)); nav.Children.Add(_prev); Grid.SetColumn(_next,1); nav.Children.Add(_next);
        DockPanel.SetDock(nav,Dock.Bottom); layout.Children.Add(nav);
        var phone = new Grid(); phone.RowDefinitions.Add(new() { Height = new GridLength(24) }); phone.RowDefinitions.Add(new()); phone.RowDefinitions.Add(new() { Height = new GridLength(24) });
        var status = new Grid(); status.ColumnDefinitions.Add(new()); status.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        status.Children.Add(_work); Grid.SetColumn(_wordCount,1); status.Children.Add(_wordCount); phone.Children.Add(status);
        _viewer.Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='FlowDocumentPageViewer'><AdornerDecorator><DocumentPageView Name='PART_DocumentPageView' DocumentViewerBase.IsMasterPage='True' Stretch='None'/></AdornerDecorator></ControlTemplate>");
        _viewer.Background = Brushes.Transparent; _viewer.BorderThickness = new Thickness(0);
        _viewer.SetResourceReference(Control.ForegroundProperty,"TextBrush"); _viewer.SetResourceReference(Control.FontFamilyProperty,"BodyFontFamily");
        _pageSurface.Children.Add(_viewer); _pageSurface.Children.Add(_outgoing); Grid.SetRow(_pageSurface,1); phone.Children.Add(_pageSurface);
        var footer = new Grid(); footer.Children.Add(_progress); footer.Children.Add(_clock); Grid.SetRow(footer,2); phone.Children.Add(footer);
        _progress.Cursor = Cursors.Hand; _progress.MouseLeftButtonUp += (_,_) => OpenPageJump();
        _paper.Child = phone; _paper.SetResourceReference(Border.BackgroundProperty,"BgBrush"); _paper.SetResourceReference(Border.BorderBrushProperty,"MutedBrush");
        // 手机页在工具栏、提示、章节导航之间水平且垂直居中；空间不足时 Viewbox 等比缩小，永不放大。
        _stage.Children.Add(new Viewbox
        {
            Child = _paper,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Margin = new Thickness(10, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        layout.Children.Add(_stage); Content = layout;
        var menu = new ContextMenu(); menu.Items.Add(new MenuItem { Header = L.T("Read.Copy"), Command = ApplicationCommands.Copy, CommandTarget = _viewer });
        var edit = new MenuItem { Header = L.T("Read.ModifyHere") }; edit.Click += (_,_) => Modify?.Invoke(EditAnchor()); menu.Items.Add(edit); _viewer.ContextMenu = menu;
        _viewer.PageViewsChanged += (_,_) => UpdateProgress();
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(DocumentViewerBase.MasterPageNumberProperty, typeof(FlowDocumentPageViewer)).AddValueChanged(_viewer,(_,_) => { UpdateProgress(); if (!_restoring) SavePosition(); });
        PreviewMouseWheel += (_,e) => { TurnPage(e.Delta < 0 ? 1 : -1); e.Handled = true; }; PreviewKeyDown += ReadingKeys;
        _clockTimer.Tick += (_,_) => _clock.Text = DateTime.Now.ToString("HH:mm");
        Loaded += (_,_) => { _clock.Text = DateTime.Now.ToString("HH:mm"); _clockTimer.Start(); ThemeManager.ThemeChanged += DisplayContextChanged; LanguageManager.LanguageChanged += DisplayContextChanged; };
        Unloaded += (_,_) => { Pause(); _clockTimer.Stop(); ThemeManager.ThemeChanged -= DisplayContextChanged; LanguageManager.LanguageChanged -= DisplayContextChanged; };
        IsVisibleChanged += (_,_) => { if (!IsVisible) Pause(); }; ApplyDimensions();
    }
    private static Button Button(string key,RoutedEventHandler action)
    {
        var b = new Button { MinHeight = 30, Padding = new Thickness(8,3,8,3), Margin = new Thickness(2), Focusable = false };
        b.SetResourceReference(ContentControl.ContentProperty,key); b.SetResourceReference(StyleProperty,"QuietButton"); b.Click += action; return b;
    }
    private static Button Icon(string key,string path,RoutedEventHandler action)
    {
        var b = new Button { Width = 30, Height = 30, MinHeight = 30, Padding = new Thickness(5), Margin = new Thickness(1), Focusable = false };
        b.SetResourceReference(StyleProperty,"QuietButton"); b.SetResourceReference(ToolTipProperty,key); System.Windows.Automation.AutomationProperties.SetName(b,L.T(key));
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(path), StrokeThickness = 1.5, Stretch = Stretch.Uniform, Width = 18, Height = 18, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"TextBrush"); b.Content = icon; b.Click += action; return b;
    }
    private void OpenMore(Button target)
    {
        _directoryButton = _findButton = target; var menu = new ContextMenu { PlacementTarget = target };
        foreach (var item in new (string Key,Action Run)[] { ("Read.Directory",OpenDirectory),("Read.Find",OpenFind),("Read.ModifyHere",()=>Modify?.Invoke(EditAnchor())) })
        { var m = new MenuItem { Header = L.T(item.Key) }; m.Click += (_,_) => item.Run(); menu.Items.Add(m); } menu.IsOpen = true;
    }
    public void ShowChapter(ProjectDto project,ChapterDto chapter,int index,int count,ReadingAnchor? initial = null) => ShowSnapshot(project,chapter,RichBody.Build(chapter.Body_Text,chapter.Body_Rich,project.Root_Path),index,count,initial);
    internal void ShowSnapshot(ProjectDto project,ChapterDto chapter,FlowDocument source,int index,int count,ReadingAnchor? initial = null)
    {
        var same = ChapterKey == PositionKey(project.Id,chapter.No); var anchor = initial ?? (same ? CurrentAnchor() : null);
        var resumeEdited = same && _speechPaused && !_speaking;
        SavePosition(); StopSpeech(); _speechPaused = resumeEdited; _projectId = project.Id; _root = project.Root_Path; _chapter = chapter; _index = index; _count = count; _source = source;
        ChapterKey = PositionKey(project.Id,chapter.No); _notice.Text = resumeEdited ? L.T("Read.SpeechEdited") : ""; _work.Text = HtmlPreview.Heading(chapter); _work.ToolTip = _work.Text;
        anchor ??= _inMemory.GetValueOrDefault(ChapterKey) ?? _store.Find(ChapterKey); Render(anchor); SetNavigating(false);
        if (_continueSpeech) { _continueSpeech = false; Dispatcher.BeginInvoke(new Action(async ()=>await ToggleSpeech())); }
    }
    public static string PositionKey(string projectId,int no) => $"{projectId}:{no}";
    private void DisplayContextChanged(object? sender,EventArgs e) { if (_source is not null && IsVisible) Render(CurrentAnchor()); }
    private void ApplyDimensions()
    {
        var p = _store.Data.Preferences; p.Normalize(); _paper.Width = p.Width; _paper.Height = p.Height; _paper.Padding = new Thickness(p.PageMargin);
        _pageSurface.Width = p.Width - 8 - p.PageMargin*2; _pageSurface.Height = p.Height - 8 - p.PageMargin*2 - 48;
    }
    private void Render(ReadingAnchor? anchor)
    {
        if (_source is null || _chapter is null) return; _restoring = true; var generation = ++_generation;
        if (_paginator is not null) { _paginator.CancelAsync(null); _paginator.PaginationProgress -= PaginationChanged; _paginator.PaginationCompleted -= PaginationChanged; }
        ApplyDimensions(); var p = _store.Data.Preferences; var doc = ReadingSnapshot.Clone(_source, _root);
        doc.FontFamily = string.IsNullOrWhiteSpace(p.FontFamily) ? _viewer.FontFamily : new FontFamily(p.FontFamily); doc.Foreground = _viewer.Foreground;
        ReadingText.Scale(doc,p); if (doc.Blocks.FirstBlock is null) doc.Blocks.Add(new Paragraph());
        foreach (var paragraph in BodyDocument.Blocks(doc.Blocks).OfType<Paragraph>().ToList())
        {
            if (!BodyFigures.TryParseMarker(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text, out _)) continue;
            var parent = BodyDocument.ParentBlocks(paragraph)!;
            var missing = new TextBlock { Text = L.T("Read.MissingImage"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 12) };
            missing.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            parent.InsertBefore(paragraph, new BlockUIContainer(missing)); parent.Remove(paragraph);
        }
        _start = doc.Blocks.FirstBlock!.ContentStart; _end = doc.Blocks.LastBlock!.ContentEnd; _text = ReadingAnchor.Normalize(new TextRange(_start,_end).Text);
        if (_speechPaused && _pausedSpeechAnchor is not null) _speechOffset = _pausedSpeechAnchor.Resolve(_text);
        _wordCount.Text = L.T("Read.EffectiveWordCount", BodyFigures.StripMarkers(_text).Count(c=>!char.IsWhiteSpace(c)&&c!='\uFFFC'));
        _wordCount.SetResourceReference(ToolTipProperty, "Read.EffectiveWordCountTip");
        doc.Blocks.InsertBefore(doc.Blocks.FirstBlock,new Paragraph(new Run(HtmlPreview.Heading(_chapter))) { FontSize=p.FontSize*1.3,FontWeight=FontWeights.SemiBold,TextAlignment=p.TitleAlignment,TextIndent=0,Margin=new Thickness(0,14,0,28) });
        if (string.IsNullOrWhiteSpace(_text)) doc.Blocks.Add(new Paragraph(new Run(L.T("Read.Empty"))) {TextIndent=0});
        foreach (var block in BodyDocument.Blocks(doc.Blocks))
        {
            if (block is BlockUIContainer { Child: Image image }) FitImage(image);
            if (block is Paragraph para) foreach (var inline in BodyDocument.Inlines(para.Inlines).OfType<InlineUIContainer>()) if(inline.Child is Image img) FitImage(img);
        }
        doc.PagePadding=new Thickness(0); doc.ColumnWidth=double.PositiveInfinity;
        doc.PageWidth=doc.MinPageWidth=doc.MaxPageWidth=_pageSurface.Width; doc.PageHeight=doc.MinPageHeight=doc.MaxPageHeight=_pageSurface.Height;
        _viewer.Document=doc; _paginator=(DynamicDocumentPaginator)((IDocumentPaginatorSource)doc).DocumentPaginator;
        _paginator.IsBackgroundPaginationEnabled=true; _paginator.PageSize=new Size(_pageSurface.Width,_pageSurface.Height);
        _paginator.PaginationProgress+=PaginationChanged; _paginator.PaginationCompleted+=PaginationChanged; ResetFindForChapter();
        Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=> { if(generation!=_generation)return; if(anchor is not null)GoToOffset(anchor.Resolve(_text));else _viewer.GoToPage(1); _restoring=false; UpdateProgress(); }));
    }
    private void FitImage(Image image) { image.Width=double.NaN; image.Height=double.NaN; image.MaxWidth=_pageSurface.Width; image.MaxHeight=_pageSurface.Height*.8; }
    private void PaginationChanged(object? sender,EventArgs e)=>UpdateProgress();
    private void UpdateProgress()=>_progress.Text=$"{Math.Max(1,_viewer.MasterPageNumber)} / {Math.Max(1,_viewer.PageCount)}{(_paginator?.IsPageCountValid==false?"…":"")}";
    internal void TurnPage(int direction)
    {
        if(direction>0&&!_viewer.CanGoToNextPage) {if(_paginator?.IsPageCountValid==true&&_index<_count-1)Navigate?.Invoke(1);return;}
        if(direction<0&&!_viewer.CanGoToPreviousPage) {if(_index>0)Navigate?.Invoke(-1);return;}
        var effect=_store.Data.Preferences.TurnEffect; _outgoing.Visibility=Visibility.Collapsed; _viewer.RenderTransform=Transform.Identity;
        if(effect!="none"&&SystemParameters.ClientAreaAnimation&&_viewer.ActualWidth>0)
        {var bitmap=new RenderTargetBitmap(Math.Max(1,(int)_viewer.ActualWidth),Math.Max(1,(int)_viewer.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(_viewer);_outgoing.Source=bitmap;_outgoing.Visibility=Visibility.Visible;}
        if(direction>0)_viewer.NextPage();else _viewer.PreviousPage();
        if(_outgoing.Visibility==Visibility.Visible)
        {
            var width=_pageSurface.Width*Math.Sign(direction);var incoming=new TranslateTransform(width,0);_viewer.RenderTransform=incoming;
            Panel.SetZIndex(_viewer,effect=="cover"?2:0);Panel.SetZIndex(_outgoing,1);
            var animation=new DoubleAnimation(width,0,TimeSpan.FromMilliseconds(200)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
            animation.Completed+=(_,_)=>{_outgoing.Visibility=Visibility.Collapsed;_viewer.RenderTransform=Transform.Identity;};incoming.BeginAnimation(TranslateTransform.XProperty,animation);
            var outgoing=new TranslateTransform();_outgoing.RenderTransform=outgoing;
            if(effect=="scroll")outgoing.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(0,-width,TimeSpan.FromMilliseconds(200)));
        }
        UpdateProgress();SavePosition();
    }
    private void GoToOffset(int offset)
    {
        if(_paginator is null||_start is null||_end is null)return;
        var paginator=_paginator;var generation=_generation;var token=new object();var point=ReadingText.Pointer(_start,_end,Math.Clamp(offset,0,_text.Length));
        GetPageNumberCompletedEventHandler? handler=null;
        handler=(_,e)=>{if(e.UserState!=token)return;paginator.GetPageNumberCompleted-=handler;if(!e.Cancelled&&e.Error is null&&generation==_generation){_viewer.GoToPage(e.PageNumber+1);UpdateProgress();}};
        paginator.GetPageNumberCompleted+=handler;paginator.GetPageNumberAsync(point,token);
    }
    public ReadingAnchor CurrentAnchor()
    {
        if(_paginator is null||_start is null||_end is null||_viewer.MasterPageNumber<1)return ReadingAnchor.Capture(_text,0);
        try {var point=_paginator.GetPagePosition(_paginator.GetPage(_viewer.MasterPageNumber-1))as TextPointer;return ReadingAnchor.Capture(_text,point is null||point.CompareTo(_start)<0?0:Math.Min(_text.Length,ReadingText.Offset(_start,point)));}
        catch(InvalidOperationException){return ReadingAnchor.Capture(_text,0);}
    }
    private ReadingAnchor EditAnchor()
    {
        var selection=_viewer.Selection;
        if(selection is not null&&!selection.IsEmpty&&_start is not null&&_end is not null&&selection.Start.CompareTo(_start)>=0&&selection.Start.CompareTo(_end)<=0)return ReadingAnchor.Capture(_text,ReadingText.Offset(_start,selection.Start));
        return CurrentAnchor();
    }
    /// <summary>「定位」：把审阅选区（没有选区时取当前页首个正文段落）整理成给正文的请求。</summary>
    internal ReviewLocateRequest? BuildLocateRequest()
    {
        if (_chapter is null || string.IsNullOrEmpty(_projectId) || _start is null || _end is null) return null;
        var selection = _viewer.Selection;
        if (selection is not null && !selection.IsEmpty
            && selection.Start.CompareTo(_start) >= 0 && selection.Start.CompareTo(_end) <= 0
            && selection.End.CompareTo(_start) >= 0 && selection.End.CompareTo(_end) <= 0)
        {
            var quote = ReadingAnchor.Normalize(selection.Text);
            if (quote.Length == 0) return null;
            if (quote.Length > MaxLocateQuote) quote = quote[..MaxLocateQuote];
            var from = Math.Clamp(ReadingText.Offset(_start, selection.Start), 0, _text.Length);
            var to = Math.Clamp(ReadingText.Offset(_start, selection.End), from, _text.Length);
            return new ReviewLocateRequest(_projectId, _chapter.No,
                ReadingAnchor.Capture(_text, from), ReadingAnchor.Capture(_text, to), quote);
        }
        return PageStartRequest();
    }
    /// <summary>当前页第一个正文段落；跳过 Render 插在正文前的自动章节标题、空段和纯插图段。</summary>
    private ReviewLocateRequest? PageStartRequest()
    {
        if (_chapter is null || _start is null || _end is null) return null;
        TextPointer? pageStart = null;
        if (_paginator is not null && _viewer.MasterPageNumber >= 1)
        {
            try { pageStart = _paginator.GetPagePosition(_paginator.GetPage(_viewer.MasterPageNumber - 1)) as TextPointer; }
            catch (Exception e) when (e is InvalidOperationException or ArgumentOutOfRangeException) { pageStart = null; }
        }
        foreach (var paragraph in BodyDocument.Blocks(Snapshot.Blocks).OfType<Paragraph>())
        {
            if (paragraph.ContentEnd.CompareTo(_start) <= 0) continue; // 自动章节标题在正文首块之前
            if (paragraph.ContentStart.CompareTo(_end) >= 0) continue; // 正文快照之外（如「本章尚无正文」占位段）
            if (pageStart is not null && paragraph.ContentEnd.CompareTo(pageStart) <= 0) continue;
            var quote = ReadingAnchor.Normalize(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
            if (quote.Length == 0) continue;
            if (string.IsNullOrWhiteSpace(quote.Replace("\uFFFC", " "))) continue;
            if (BodyFigures.TryParseMarker(quote, out _)) continue;
            if (quote.Length > MaxLocateQuote) quote = quote[..MaxLocateQuote];
            var from = Math.Clamp(ReadingText.Offset(_start, paragraph.ContentStart), 0, _text.Length);
            var to = Math.Clamp(ReadingText.Offset(_start, paragraph.ContentEnd), from, _text.Length);
            return new ReviewLocateRequest(_projectId, _chapter.No,
                ReadingAnchor.Capture(_text, from), ReadingAnchor.Capture(_text, to), quote);
        }
        return null;
    }
    private void LocateCurrent()
    {
        if (BuildLocateRequest() is not { } request) { ShowNotice(L.T("Read.LocateNoTarget")); return; }
        Locate?.Invoke(request);
    }
    /// <summary>测试用：直接按正文纯文本偏移在审阅页里划一段。</summary>
    internal void SelectForTest(int start, int length)
    {
        if (_start is null || _end is null || length <= 0) return;
        var from = ReadingText.Pointer(_start, _end, Math.Clamp(start, 0, _text.Length));
        var to = ReadingText.Pointer(_start, _end, Math.Clamp(start + length, 0, _text.Length));
        _viewer.Selection?.Select(from, to);
    }
    private void SavePosition(){if(_chapter is null||_restoring||_start is null)return;var anchor=CurrentAnchor();_inMemory[ChapterKey]=anchor;_store.Remember(ChapterKey,anchor);_store.Save();}
    public void Pause(){SavePosition();StopSpeech();_continueSpeech=false;_generation++;ClosePopups();if(_settingsWindow?.IsVisible==true)OwnedWindowFix.Hide(_settingsWindow);}
    public void DisposeReader(){Pause();_settingsWindow?.Close();_speech?.Dispose();_clockTimer.Stop();}
    public void StopSpeech(){_speechRequest++;_speech?.Stop();_speaking=false;_speechPaused=false;_play.SetResourceReference(ToolTipProperty,"Read.Speak");}
    public void PauseForEdit(){if(!_speaking)return;_speechRequest++;_pausedSpeechAnchor=ReadingAnchor.Capture(_text,_speechOffset);_speech?.Stop();_speaking=false;_speechPaused=true;ShowNotice(L.T("Read.SpeechEdited"));}
    private ReadingSpeech Speech()
    {
        if(_speech is not null)return _speech;_speech=new ReadingSpeech();
        _speech.Progress+=offset=>Dispatcher.BeginInvoke(new Action(()=>{if(_speaking)_speechOffset=offset;}));
        _speech.Failed+=_=>Dispatcher.BeginInvoke(new Action(()=>{StopSpeech();ShowNotice(L.T("Read.NoVoice"));}));
        _speech.Completed+=()=>Dispatcher.BeginInvoke(new Action(()=>{var next=_speaking&&_store.Data.Preferences.AutoNext&&_index<_count-1;StopSpeech();if(next){_continueSpeech=true;Navigate?.Invoke(1);}}));return _speech;
    }
    internal async Task ToggleSpeech()
    {
        if(_chapter is null||CanSpeak?.Invoke()==false){ShowNotice(L.T("Read.WaitForOperation"));return;}
        if(_speaking){if(_speechPaused){_speechPaused=false;Speech().Resume();}else{_speechPaused=true;Speech().Pause();}return;}
        var request=++_speechRequest;var chapterKey=ChapterKey;
        var voices=await Speech().VoicesAsync();if(request!=_speechRequest||chapterKey!=ChapterKey||!IsVisible)return;
        if(voices.Count==0){_play.IsEnabled=false;ShowNotice(L.T("Read.NoVoice"));return;}
        _notice.Text="";_speaking=true;var offset=_speechPaused?_speechOffset:CurrentAnchor().Offset;_speechPaused=false;
        await Speech().SpeakAsync(_text,offset,_store.Data.Preferences);_play.SetResourceReference(ToolTipProperty,"Read.PauseSpeech");
    }
    private void OpenPageJump()
    {
        var menu=new ContextMenu{PlacementTarget=_progress};var input=new TextBox{Width=100,Text=_viewer.MasterPageNumber.ToString()};
        System.Windows.Automation.AutomationProperties.SetName(input,L.T("Read.PageNumber"));menu.Items.Add(new MenuItem{Header=input,StaysOpenOnClick=true});
        input.KeyDown+=(_,e)=>{if(e.Key==Key.Enter&&int.TryParse(input.Text,out var page)){_viewer.GoToPage(Math.Clamp(page,1,Math.Max(1,_viewer.PageCount)));menu.IsOpen=false;}};
        menu.IsOpen=true;Dispatcher.BeginInvoke(new Action(()=>{input.Focus();input.SelectAll();}));
    }
    private void ReadingKeys(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape&&CloseSettings()){e.Handled=true;return;}
        if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.F){OpenFind();e.Handled=true;return;}
        if(e.Key==Key.F3){FindMatch(Keyboard.Modifiers==ModifierKeys.Shift?-1:1);e.Handled=true;return;}
        if(e.Key is Key.PageDown or Key.Right or Key.Space){TurnPage(1);e.Handled=true;}
        if(e.Key is Key.PageUp or Key.Left){TurnPage(-1);e.Handled=true;}
    }
}
