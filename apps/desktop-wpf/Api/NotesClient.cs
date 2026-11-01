using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

/// <summary>
/// 作品级个人笔记与分类接口。仅执行本地资料管理，不调用模型。
/// </summary>
public interface INotesApi
{
    Task<List<NoteDto>> ListAsync(string projectId);
    Task<List<NoteFolderDto>> FoldersAsync(string projectId);
    Task<NoteFolderDto> SaveFolderAsync(string projectId, string title, string? folderId = null);
    Task<NoteDto> CreateAsync(string projectId, string folder, string title, string body);
    Task<NoteDto> UpdateAsync(string projectId, string noteId, string title, string body, int contentVersion, string folder);
    Task DeleteAsync(string projectId, string noteId);
    Task<List<NoteDto>> PinAsync(string projectId, string noteId, bool pinned);
    Task<List<NoteDto>> MoveAsync(string projectId, string noteId, string direction, string? operationId = null);
    Task<NoteDto> CopyAsync(string projectId, string noteId, string title, string? operationId = null);
}

/// <summary>INotesApi 的 HTTP 实现，行为见 docs/PERSONAL_NOTES_UI.md。</summary>
public sealed class NotesClient : INotesApi
{
    private readonly NovelApiClient _api;

    public NotesClient(NovelApiClient api) => _api = api;

    public Task<List<NoteDto>> ListAsync(string projectId) => _api.ListNotesAsync(projectId);

    public Task<List<NoteFolderDto>> FoldersAsync(string id) => _api.ListNoteFoldersAsync(id);
    public Task<NoteFolderDto> SaveFolderAsync(string id, string title, string? folderId = null) => _api.SaveNoteFolderAsync(id, title, folderId);
    public Task<NoteDto> CreateAsync(string projectId, string folder, string title, string body) =>
        _api.CreateNoteAsync(projectId, 0, title, body, folder);

    public Task<NoteDto> UpdateAsync(string projectId, string noteId, string title, string body, int contentVersion, string folder) =>
        _api.UpdateNoteAsync(projectId, noteId, title, body, contentVersion, folder);

    public Task DeleteAsync(string projectId, string noteId) => _api.DeleteNoteAsync(projectId, noteId);

    public Task<List<NoteDto>> PinAsync(string projectId, string noteId, bool pinned) => _api.PinNoteAsync(projectId, noteId, pinned);
    public Task<List<NoteDto>> MoveAsync(string projectId, string noteId, string direction, string? operationId = null) => _api.MoveNoteAsync(projectId, noteId, direction, operationId);
    public Task<NoteDto> CopyAsync(string projectId, string noteId, string title, string? operationId = null) => _api.CopyNoteAsync(projectId, noteId, title, operationId);

}
