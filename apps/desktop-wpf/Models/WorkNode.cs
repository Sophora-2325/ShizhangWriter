using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop.Models;

public abstract class ObservableNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
}

/// <summary>目录区中的一个作品（项目），子节点为章节。</summary>
public sealed class WorkNode : ObservableNode
{
    private ProjectDto _project;
    private bool _isExpanded;
    private bool _isCurrent;

    public WorkNode(ProjectDto project) => _project = project;

    public ProjectDto Project
    {
        get => _project;
        set
        {
            Set(ref _project, value);
            Raise(nameof(Title));
            Raise(nameof(DisplayTitle));
            Raise(nameof(IsMissing));
            Raise(nameof(HasFolder));
            Raise(nameof(ToolTipText));
        }
    }

    public string ToolTipText => string.IsNullOrEmpty(Project.Root_Path) ? Title : $"{Title}\n{Project.Root_Path}";

    public string Title => string.IsNullOrWhiteSpace(Project.Title) ? L.T("Work.Untitled") : Project.Title;

    public bool IsMissing => Project.Root_Missing;

    /// <summary>有作品文件夹且文件夹还在（旧版集中存储的作品没有文件夹）。</summary>
    public bool HasFolder => !string.IsNullOrEmpty(Project.Root_Path) && !Project.Root_Missing;

    public string DisplayTitle => IsMissing ? L.T("Work.FolderMissing", Title) : Title;

    /// <summary>切换界面语言后刷新目录树上的文字。</summary>
    public void RefreshTexts()
    {
        Raise(nameof(Title));
        Raise(nameof(DisplayTitle));
        Raise(nameof(ToolTipText));
        foreach (var c in Chapters) c.RefreshTexts();
    }

    public ObservableCollection<ChapterNode> Chapters { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    public bool Loaded { get; private set; }

    public void SetChapters(IEnumerable<ChapterDto> chapters)
    {
        Chapters.Clear();
        foreach (var c in chapters)
            Chapters.Add(new ChapterNode(this, c));
        Loaded = true;
    }
}

public sealed class ChapterNode : ObservableNode
{
    private bool _isSelected;

    public ChapterNode(WorkNode work, ChapterDto chapter)
    {
        Work = work;
        Chapter = chapter;
    }

    public WorkNode Work { get; }
    public ChapterDto Chapter { get; }
    public string Display => Chapter.Display;
    /// <summary>目录行只显示标题，状态收成行尾标记；完整状态仍走 ToolTip 的 Display。</summary>
    public string TitleText => Chapter.Title;
    public string StatusText => L.T("ChapterStatus." + Chapter.Status);
    public bool IsFinal => Chapter.Status == "final";
    public bool IsSuperseded => Chapter.Status == "superseded";

    public void RefreshTexts()
    {
        Raise(nameof(Display));
        Raise(nameof(TitleText));
        Raise(nameof(StatusText));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
