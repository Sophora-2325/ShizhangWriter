using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>设定区简略卡：画像与三项身份信息；不改变人物编辑窗口。</summary>
internal sealed class CharacterSummaryIdentity : Grid
{
    internal Border PortraitFrame { get; }
    internal Grid IdentityFields { get; } = new();

    public CharacterSummaryIdentity(CharacterDto character, string root)
    {
        ColumnDefinitions.Add(new() { Width = new GridLength(1.2, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        PortraitFrame = new Border { MinHeight = 115.2, Margin = new Thickness(0, 0, 12, 0), BorderThickness = new Thickness(1) };
        PortraitFrame.SetResourceReference(Border.BackgroundProperty, "Bg2Brush");
        PortraitFrame.SetResourceReference(Border.BorderBrushProperty, "BorderBrushKey");
        if (Portraits.Load(Portraits.Resolve(root, character.Portrait), 320) is { } image)
            PortraitFrame.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        else
        {
            var empty = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            empty.SetResourceReference(TextBlock.TextProperty, "Common.None");
            empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            PortraitFrame.Child = empty;
        }
        System.Windows.Automation.AutomationProperties.SetName(PortraitFrame, L.T("Char.Portrait"));
        Children.Add(PortraitFrame);
        SetColumn(IdentityFields, 1); Children.Add(IdentityFields);
        AddIdentity("Field.Name", character.Name, true);
        AddIdentity("Field.Age", character.Age);
        AddIdentity("Field.Gender", character.Gender);
    }

    private void AddIdentity(string labelKey, string value, bool prominent = false)
    {
        var row = IdentityFields.RowDefinitions.Count;
        IdentityFields.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 38.4 });
        var group = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 1) };
        var label = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };
        label.SetResourceReference(TextBlock.TextProperty, labelKey);
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        group.Children.Add(label);
        var text = new TextBox { Text = string.IsNullOrWhiteSpace(value) ? "—" : value, IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, FontSize = prominent ? 15 : 14, FontWeight = prominent ? FontWeights.SemiBold : FontWeights.Normal };
        text.SetResourceReference(StyleProperty, "SelectableText");
        text.SetResourceReference(Control.ForegroundProperty, string.IsNullOrWhiteSpace(value) ? "MutedBrush" : "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(text, L.T(labelKey));
        group.Children.Add(text);
        SetRow(group, row); IdentityFields.Children.Add(group);
    }
}
