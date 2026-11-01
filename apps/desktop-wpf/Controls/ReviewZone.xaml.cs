using System.Windows;
using System.Windows.Controls;

namespace NovelGm.Desktop.Controls;

/// <summary>评注区：本章评注，提交「下一步」前作为草稿自动保存。</summary>
public partial class ReviewZone : UserControl
{
    private bool _loading;

    public ReviewZone()
    {
        InitializeComponent();
    }

    public event EventHandler? Changed;

    /// <summary>用户点了「提交评注」；由主窗口走与「下一步」完全相同的付费路径。</summary>
    public event EventHandler? SubmitRequested;

    public string Notes => NotesBox.Text.Replace("\r\n", "\n");

    public bool IsReadOnly
    {
        set
        {
            NotesBox.IsReadOnly = value;
            // 忙碌中 / 已作废章节 / 未选作品时也不该能提交（RunStep 会在操作前后切这个值）
            SubmitButton.IsEnabled = !value;
        }
    }

    public void Load(string notes)
    {
        _loading = true;
        try
        {
            NotesBox.Text = notes;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Box_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Submit_Click(object sender, RoutedEventArgs e) => SubmitRequested?.Invoke(this, EventArgs.Empty);
}
