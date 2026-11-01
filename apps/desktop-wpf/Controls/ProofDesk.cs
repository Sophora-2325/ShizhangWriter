using System.Windows;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop.Controls;

/// <summary>Local aliases for the existing theme. Never overrides application resources.</summary>
internal static class ProofDesk
{
    internal static void Apply(FrameworkElement target)
    {
        // Independent control hosts have no Application resources.
        if (Application.Current is null && target.TryFindResource("BgBrush") is null)
            target.Resources.MergedDictionaries.Add(ThemeManager.CreatePalette(ThemeManager.IsDark, ThemeManager.Accent));
        foreach (var (alias, resource) in new[] {
            ("Desk.Shell", "Bg2Brush"), ("Desk.Page", "BgBrush"),
            ("Desk.Ink", "TextBrush"), ("Desk.Muted", "MutedBrush"),
            ("Desk.Line", "BorderBrushKey"), ("Desk.Accent", "AccentBrush"),
            ("Desk.PrimaryInk", "PrimaryFgBrush"), ("Desk.Tint", "InfoBgBrush"),
            ("Desk.Green", "OkBrush"), ("Desk.GreenTint", "Bg2Brush"),
            ("Desk.Warning", "DangerBrush"), ("Desk.Review", "ReviewBrush") })
            target.Resources[alias] = target.FindResource(resource);
    }
}
