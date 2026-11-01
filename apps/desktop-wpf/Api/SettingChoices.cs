using System.Windows.Controls;

namespace NovelGm.Desktop.Api;

internal static class SettingChoices
{
    public static void Fill(ComboBox box, string[] values, string current)
    {
        box.Items.Clear();
        if (string.IsNullOrEmpty(current)) current = values[0];
        foreach (var value in values.Contains(current) ? values : values.Append(current))
        {
            var key = "Choice." + value;
            var label = L.T(key);
            var item = new ComboBoxItem { Content = label == key ? value : label, Tag = value };
            box.Items.Add(item);
            if (value == current) box.SelectedItem = item;
        }
    }
    public static string Read(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
}
