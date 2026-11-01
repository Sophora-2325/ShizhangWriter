namespace NovelGm.Desktop.Api;

/// <summary>顶部正文工具条的当前可用性；宿主计算，工具栏只负责展示。</summary>
public readonly record struct ToolbarState(
    bool HasWork,
    bool HasChapter,
    bool Editable,
    bool CanComment,
    bool Busy)
{
    public bool CanHistory => HasChapter && !Busy;

    public bool CanPublish => HasWork && !Busy;

    public static ToolbarState Empty => new(false, false, false, false, false);
}

/// <summary>听写展示状态：只影响界面，不改变录音/转写协议。</summary>
public enum DictationState
{
    Idle,
    Recording,
    Transcribing,
    Failed,
}
