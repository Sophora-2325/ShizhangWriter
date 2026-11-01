using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NovelGm.Desktop;

/// <summary>菜单和快捷键共用的可执行判断。存储值不变，只决定当前能不能做。</summary>
internal static class ShellCommandState
{
    internal static bool CanUseWork(bool hasProject) => hasProject;

    internal static bool CanStep(IReadOnlyList<int> orderedNos, int? selectedNo, int delta)
    {
        if (selectedNo is null) return false;
        var index = -1;
        for (var i = 0; i < orderedNos.Count; i++)
            if (orderedNos[i] == selectedNo.Value) { index = i; break; }
        if (index < 0) return false;
        var next = index + delta;
        return next >= 0 && next < orderedNos.Count;
    }

    internal static bool CanCopyText(bool hasTarget) => hasTarget;

    internal static bool CanModifyText(bool hasTarget, bool readOnly) => hasTarget && !readOnly;
}

internal static class SettingsPage
{
    internal const string General = "general";
    internal const string Appearance = "appearance";
    internal const string Models = "models";
    internal const string Advanced = "advanced";
    internal const string Skills = "skills";
    internal const string Plugins = "plugins";

    internal static bool IsPrimary(string? page) =>
        page is General or Appearance or Models or Advanced;

    internal static bool IsKnown(string? page) =>
        IsPrimary(page) || page is Skills or Plugins;
}

internal readonly record struct MenuMark(bool First, bool Second, string LabelKey);

internal static class MenuMarks
{
    internal static MenuMark Layout(bool pure) => new(pure, !pure, pure ? "Mode.Pure" : "Mode.Assist");

    internal static MenuMark Width(bool paper) => new(paper, !paper, paper ? "Menu.WidthPaper" : "Menu.WidthFull");

    internal static MenuMark Usage(bool visible) => new(visible, false, visible ? "Menu.UsageOn" : "Menu.UsageOff");
}

internal static class EditTargetState
{
    internal static TextBoxBase? Remember(TextBoxBase? current, TextBoxBase? focused, bool insideMenu) =>
        focused is null || insideMenu ? current : focused;
}

internal static class ModelChoiceRules
{
    internal static bool AllowLocalOnly(bool turningOn, bool alreadyOn, bool hasLocal) =>
        !turningOn || alreadyOn || hasLocal;

    internal static string Confirmed(bool saved, string requested, string previous) =>
        saved ? requested : previous;
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _run;
    private readonly Func<bool> _can;

    internal RelayCommand(Action run, Func<bool> can)
    {
        _run = run;
        _can = can;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _can();
    public void Execute(object? parameter) => _run();
}
