using System.Windows.Input;

namespace NovelGm.Desktop.Api;

internal static class ChapterNavigation
{
    internal static int Direction(Key key, ModifierKeys modifiers) => modifiers != ModifierKeys.Control ? 0
        : key == Key.PageUp ? -1 : key == Key.PageDown ? 1 : 0;
}
