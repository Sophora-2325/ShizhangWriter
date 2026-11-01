using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;
namespace NovelGm.Desktop;
public partial class MainWindow
{
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _previewFollowsEditor = true;
    private long _previewRequest;
    /// <summary>正文里的审阅同步提示；再次点击、编辑正文、切章或 Esc 时清掉。</summary>
    private bool _reviewLocateActive;
    private long _reviewLocateRequest;
    private bool ReviewVisible => _rightTab == RightTab.Reader && RightPane.Visibility == Visibility.Visible;
    private void InitializeReview()
    {
        ReaderView.Navigate += async delta => await NavigateReadingAsync(delta);
        ReaderView.Jump += async no => await ShowReviewChapterAsync(no);
        ReaderView.Locate += request => _ = LocateReviewAsync(request);
        ReaderView.RefreshRequested += async () => await RefreshReviewAsync();
        ReaderView.Modify += ModifyFromReading;
        ReaderView.CanSpeak = () => !_dictating && !_transcribing;
        // 正文一改，定位高亮就过期；换章会重建 Document（也走这里）。
        BodyEditor.TextChanged += (_, _) => ClearReviewHighlight();
        BodyPresentation.DismissLocateOnInteraction(this, BodyEditor, () =>
        {
            // 用户在跨章定位等待期间点击其他位置时，也取消尚未完成的定位。
            _reviewLocateRequest++;
            ClearReviewHighlight();
        });
        // Esc：先撤定位高亮并吃掉这次按键，避免同一次 Esc 又打开/关闭设置窗。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (!_reviewLocateActive && !BodyPresentation.HasLocate(BodyEditor.Document)) return;
            ClearReviewHighlight();
            e.Handled = true;
        };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); if (ReviewVisible && ReaderView.ChapterNo == _selected?.No && ReaderView.ProjectId == _project?.Id) ShowCurrentReview(true, false); };
    }
    /// <summary>正文在审阅栏看不到的时候被改过（或重新载入过），下次显示同一章时要重新取快照。</summary>
    private bool _reviewStale;

    private void ScheduleReviewUpdate()
    {
        _reviewStale = true;
        if (!ReviewVisible || _loadingBody || ReaderView.ProjectId != _project?.Id || ReaderView.ChapterNo != _selected?.No) return;
        _previewRequest++; ReaderView.PauseForEdit(); _previewTimer.Stop(); _previewTimer.Start();
    }
    private void ShowCurrentReview(bool force, bool locate = true)
    {
        if (!ReviewVisible) return;
        if (_project is null || _selected is null) { ReaderView.Clear(); return; }
        var key = Controls.ReadingView.PositionKey(_project.Id, _selected.No);
        var sameChapter = ReaderView.ChapterKey == key;
        if (!force && sameChapter && !_reviewStale) { ReaderView.SetNavigating(false); return; }
        // 同一章只是内容过期：刷新但保留阅读位置
        if (!force && sameChapter) locate = false;
        _reviewStale = false;
        _previewRequest++; _previewTimer.Stop();
        var live = _chapters.Where(c => c.Status != "superseded").OrderBy(c => c.No).ToList();
        var snapshot = ReadingSnapshot.Clone(BodyEditor.Document, _project.Root_Path);
        var anchor = locate ? ReadingText.Capture(BodyEditor.Document, BodyEditor.Selection.Start) : ReaderView.CurrentAnchor();
        ReaderView.SetChapters(live); ReaderView.ShowSnapshot(_project, _selected, snapshot, live.FindIndex(c => c.No == _selected.No), live.Count, anchor);
    }
    private async Task RefreshReviewAsync()
    {
        if (!ReviewVisible || _project is null || ReaderView.ChapterNo is not int no) { ShowCurrentReview(true); return; }
        if (no == _selected?.No) { ShowCurrentReview(true, false); return; }
        await ShowReviewChapterAsync(no, true);
    }
    private async Task NavigateReadingAsync(int delta)
    {
        if (!ReviewVisible || _project is null) return;
        var live = _chapters.Where(c => c.Status != "superseded").OrderBy(c => c.No).ToList();
        var current = live.FindIndex(c => c.No == ReaderView.ChapterNo); var target = current + delta;
        if (target < 0 || target >= live.Count) { ReaderView.ShowNotice(L.T("Read.End")); return; }
        await ShowReviewChapterAsync(live[target].No);
    }
    private async Task ShowReviewChapterAsync(int no, bool preserve = false)
    {
        if (_project is null || !ReviewVisible) return;
        ReaderView.StopSpeech();
        _previewFollowsEditor = false; var request = ++_previewRequest; var project = _project;
        if (no == _selected?.No) { ShowCurrentReview(true, !preserve); return; }
        ReaderView.SetNavigating(true); var anchor = preserve ? ReaderView.CurrentAnchor() : null;
        try
        {
            var chapter = await App.Api.GetChapterAsync(project.Id, no);
            if (request != _previewRequest || !ReviewVisible || _project?.Id != project.Id) return;
            var live = _chapters.Where(c => c.Status != "superseded").OrderBy(c => c.No).ToList();
            ReaderView.ShowChapter(project, chapter, live.FindIndex(c => c.No == no), live.Count, anchor);
        }
        catch (Exception) { if (request == _previewRequest) ReaderView.ShowNotice(L.T("Read.LoadFailed")); }
        finally { if (request == _previewRequest || !ReviewVisible) ReaderView.SetNavigating(false); }
    }
    private async void ModifyFromReading(ReadingAnchor anchor)
    {
        if (_project is null || ReaderView.ChapterNo is not int no) return;
        var projectId = _project.Id;
        if (_selected?.No != no)
        {
            await Safe(() => LoadProjectAsync(projectId, no));
            if (_project?.Id != projectId || _selected?.No != no) return;
        }
        _previewFollowsEditor = true; _openZones.Add(Zone.Body); LayoutZones();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_project?.Id != projectId || _selected?.No != no) return;
            var doc = BodyEditor.Document; var position = ReadingText.Pointer(doc.ContentStart, doc.ContentEnd, anchor.Resolve(RichBody.PlainText(doc)));
            BodyEditor.Selection.Select(position, position); BodyEditor.Focus(); position.Paragraph?.BringIntoView();
        }));
    }

    /// <summary>
    /// 「审阅→正文」：必要时先保存当前章并切到目标章，再在正文里定位并临时高亮。
    /// 审阅页保持原页不重排；匹配不可靠时只提示，不跳。
    /// </summary>
    private async Task LocateReviewAsync(ReviewLocateRequest request)
    {
        if (request is null || _project is null || _selected is null) return;
        var requestId = ++_reviewLocateRequest;
        var projectId = _project.Id;
        ClearReviewHighlight();
        if (request.ProjectId != projectId) return;

        if (request.ChapterNo != _selected.No)
        {
            await FlushEditsAsync();
            if (requestId != _reviewLocateRequest) return;
            if (AnyDirty) { ReaderView.ShowNotice(L.T("Read.LocateSaveFailed")); return; }
            try { await LoadProjectAsync(projectId, request.ChapterNo); }
            catch (Exception) { /* 下面按当前状态提示 */ }
            if (requestId != _reviewLocateRequest) return;
            if (_project?.Id != projectId || _selected?.No != request.ChapterNo)
            {
                ReaderView.ShowNotice(L.T("Read.LocateLoadFailed"));
                return;
            }
        }

        _openZones.Add(Zone.Body); LayoutZones();
        var doc = BodyEditor.Document;
        var bodyText = new BodyTextMap(doc).Text;
        if (!ReviewLocator.TryResolve(bodyText, request, out var start, out var length, out _))
        {
            ReaderView.ShowNotice(L.T("Read.LocateAmbiguous"));
            return;
        }
        BodyPresentation.ShowLocate(doc, start, length);
        _reviewLocateActive = true;
        var chapterNo = request.ChapterNo;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_reviewLocateActive || _project?.Id != projectId || _selected?.No != chapterNo) return;
            var map = new BodyTextMap(BodyEditor.Document);
            var pointer = map.Pointer(start);
            var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty) BodyEditor.ScrollToVerticalOffset(Math.Max(0, BodyEditor.VerticalOffset + rect.Top - 40));
            else pointer.Paragraph?.BringIntoView();
        }));
    }

    private void ClearReviewHighlight()
    {
        _reviewLocateActive = false;
        BodyPresentation.ClearLocate(BodyEditor.Document);
    }
}
