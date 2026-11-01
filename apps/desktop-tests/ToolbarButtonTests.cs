using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ToolbarButtonTests
{
    [Fact]
    public void Body_toolbar_buttons_work_twice()
    {
        StaRunner.Run(Run, TimeSpan.FromSeconds(40), "正文按钮没有在限时内测完");
    }

    private static void Run()
    {
        var language = UiPrefs.Current.SttLanguage;
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-buttons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var box = new RichTextBox { Width = 760, Height = 480, FontSize = 17, IsReadOnly = false };
        var bar = new FormatToolbar { Target = box };
        var win = new Window
        {
            Content = new DockPanel { Children = { bar, box } },
            Width = 980,
            Height = 640,
            ShowInTaskbar = false,
        };
        DockPanel.SetDock(bar, Dock.Top);
        var images = 0;
        var mics = 0;
        var publishes = 0;
        bar.InsertImageRequested += (_, _) => images++;
        bar.DictationToggled += (_, _) => mics++;
        bar.PublishRequested += (_, _) => publishes++;
        win.Show();
        box.Focus();
        Pump(DispatcherPriority.Loaded);
        try
        {
            for (var pass = 1; pass <= 2; pass++)
            {
                box.Document.Blocks.Clear();
                box.Document.Blocks.Add(new Paragraph(new Run("转学到城南中学的第二天。")));
                if (pass == 2)
                {
                    var src = Path.Combine(root, "source.png");
                    if (!File.Exists(src)) File.WriteAllBytes(src, Png);
                    box.CaretPosition = box.Document.ContentEnd;
                    BodyFigures.Insert(box, BodyFigures.Store(root, src));
                }
                SelectText(box);

                Click(bar.BoldButton);
                Click(bar.ItalicButton);
                Click(bar.UnderlineButton);
                Assert.True(IsBold(box), "加粗后字重=" + Weight(box));
                Assert.True(IsItalic(box));
                Assert.True(HasUnderline(box));
                Click(bar.BoldButton);
                Click(bar.ItalicButton);
                Click(bar.UnderlineButton);
                Assert.False(IsBold(box));
                Assert.False(IsItalic(box));
                Assert.False(HasUnderline(box));

                Click(bar.AlignCenterButton);
                Assert.Equal(TextAlignment.Center, Alignment(box));
                Click(bar.AlignRightButton);
                Assert.Equal(TextAlignment.Right, Alignment(box));
                Click(bar.AlignJustifyButton);
                Assert.Equal(TextAlignment.Justify, Alignment(box));
                Click(bar.AlignLeftButton);
                Assert.Equal(TextAlignment.Left, Alignment(box));

                var indentBefore = Indent(box);
                Click(bar.IndentButton);
                Assert.NotEqual(indentBefore > 0.5, Indent(box) > 0.5);
                Click(bar.IndentButton);
                Assert.Equal(indentBefore > 0.5, Indent(box) > 0.5);

                bar.FontCombo.SelectedIndex = Math.Min(pass, bar.FontCombo.Items.Count - 1);
                bar.SizeCombo.SelectedIndex = pass == 1 ? 0 : 4;
                Assert.True(FontSize(box) > 20);

                bar.SpacingCombo.SelectedIndex = 0;
                var line = new TextRange(box.Document.ContentStart, box.Document.ContentEnd)
                    .GetPropertyValue(Block.LineHeightProperty);
                Assert.True(line is not double h || double.IsNaN(h));
                bar.SpacingCombo.SelectedIndex = pass == 1 ? 2 : 4;
                var spaced = LineHeight(box);
                Assert.True(spaced > FontSize(box));

                bar.StyleCombo.SelectedIndex = 1;
                Assert.Equal(TextAlignment.Center, Alignment(box));
                Assert.True(IsBold(box));
                bar.StyleCombo.SelectedIndex = 0;
                Assert.Equal(TextAlignment.Left, Alignment(box));
                Assert.False(IsBold(box));
                Assert.True(Indent(box) > 30);
                bar.StyleCombo.SelectedIndex = 3;
                Assert.True(IsItalic(box));
                bar.StyleCombo.SelectedIndex = 4;
                Assert.Equal(TextAlignment.Center, Alignment(box));
                Assert.Equal(0, Indent(box));
                bar.StyleCombo.SelectedIndex = 2;
                bar.StyleCombo.SelectedIndex = 0;

                SelectText(box);
                Click(bar.ColorButton);
                var swatch = bar.SwatchGrid.Children.OfType<Button>().First();
                Click(swatch);
                var colored = box.Selection.GetPropertyValue(TextElement.ForegroundProperty) as SolidColorBrush;
                Assert.NotNull(colored);
                Assert.Equal(((SolidColorBrush)swatch.Background).Color, colored!.Color);
                Click(bar.AutoColorButton);
                SelectText(box);

                Click(bar.ImageButton);
                Click(bar.MicButton);
                Click(bar.PublishButton);
                var lang = pass;
                bar.DictationLangCombo.SelectedIndex = lang % bar.DictationLangCombo.Items.Count;

                SelectAll(box);
                bar.ClearFormatting();
                Assert.False(IsBold(box));
                bar.ArmPainter();
                box.Selection.Select(box.Document.ContentStart.GetPositionAtOffset(1)!, box.Document.ContentStart.GetPositionAtOffset(3)!);
                Assert.Equal(1, BodyEditing.ReplaceAll(box, "城南", pass == 1 ? "江南" : "江北"));
                BodyReview.SetInvisibles(box.Document, true);
                BodyReview.SetInvisibles(box.Document, false);
                box.CaretPosition = box.Document.ContentEnd.GetNextInsertionPosition(LogicalDirection.Backward) ?? box.Document.ContentEnd;
                box.CaretPosition.InsertTextInRun(pass == 1 ? "…" : "—");
                var prose = RichBody.Capture(box.Document);
                Assert.DoesNotContain("«插图:", prose.Text);
                Assert.DoesNotContain("¶", prose.Text);
                Assert.Contains(pass == 1 ? "江南" : "江北", prose.Text);

                var start = box.Document.ContentStart.GetPositionAtOffset(1);
                var end = start?.GetPositionAtOffset(1);
                box.Selection.Select(start!, end!);
                BodyReview.MarkInserted(box.Selection);
                BodyReview.Accept(box);
                box.Selection.Select(start!, end!);
                BodyReview.MarkDeleted(box.Selection);
                BodyReview.Reject(box);
                Assert.Contains(pass == 1 ? "…" : "—", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text);
            }

            Assert.Equal(2, images);
            Assert.Equal(2, mics);
            Assert.Equal(2, publishes);
            Assert.Contains(box.Document.Blocks.OfType<BlockUIContainer>(), b => b.Child is Image);
        }
        finally
        {
            UiPrefs.Current.SttLanguage = language;
            UiPrefs.Save();
            win.Close();
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录 */ }
        }
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void SelectAll(RichTextBox box) => SelectText(box);

    private static void SelectText(RichTextBox box)
    {
        var run = box.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).First();
        box.Selection.Select(run.ContentStart, run.ContentEnd);
        box.Focus();
    }

    private static string Weight(RichTextBox box)
    {
        var value = box.Selection.GetPropertyValue(TextElement.FontWeightProperty);
        return value?.ToString() ?? "null";
    }

    private static bool IsBold(RichTextBox box) =>
        box.Selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600;

    private static bool IsItalic(RichTextBox box) =>
        box.Selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle s && s == FontStyles.Italic;

    private static bool HasUnderline(RichTextBox box) =>
        box.Selection.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection d
        && d.Any(x => x.Location == TextDecorationLocation.Underline);

    private static TextAlignment Alignment(RichTextBox box) =>
        box.Selection.GetPropertyValue(Paragraph.TextAlignmentProperty) is TextAlignment a ? a : TextAlignment.Left;

    private static double Indent(RichTextBox box) =>
        box.Selection.GetPropertyValue(Paragraph.TextIndentProperty) is double d ? d : 0;

    private static double FontSize(RichTextBox box) =>
        box.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double d ? d : box.FontSize;

    private static double LineHeight(RichTextBox box) =>
        box.Selection.GetPropertyValue(Block.LineHeightProperty) is double d ? d : 0;

    private static void Pump(DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
        0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, 0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
        0x00, 0x00, 0x03, 0x00, 0x01, 0x00, 0x05, 0xFE, 0x02, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
        0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];
}
