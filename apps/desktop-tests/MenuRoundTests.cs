using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class MenuRoundTests
{
    [Fact]
    public void More_menu_actions_survive_three_rounds()
    {
        StaRunner.Run(Run, TimeSpan.FromSeconds(40), "正文菜单操作没有在限时内结束");
    }

    private static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var box = new RichTextBox { Width = 700, Height = 480, FontSize = 17 };
        var bar = new FormatToolbar { Target = box };
        var win = new Window
        {
            Content = new DockPanel { Children = { bar, box } },
            Width = 900,
            Height = 640,
            ShowInTaskbar = false,
        };
        DockPanel.SetDock(bar, Dock.Top);
        win.Show();
        box.Focus();
        Pump(DispatcherPriority.Loaded);
        try
        {
            for (var round = 1; round <= 3; round++)
            {
                box.Document.Blocks.Clear();
                box.Document.Blocks.Add(new Paragraph(new Run($"第{round}天转到城南中学。")));
                box.CaretPosition = box.Document.ContentEnd;

                var all = new TextRange(box.Document.ContentStart, box.Document.ContentEnd);
                all.Select(all.Start, all.End);
                box.Selection.Select(all.Start, all.End);
                bar.ClearFormatting();
                bar.ApplyBox(2, 6, 4, 4);
                bar.ArmPainter();
                box.Selection.Select(box.Document.ContentStart, box.Document.ContentEnd);
                Assert.False(box.Selection.IsEmpty);

                Assert.Equal(1, BodyEditing.ReplaceAll(box, "城南", "江南"));
                Assert.Contains("江南", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text);

                box.CaretPosition = box.Document.ContentEnd;
                box.CaretPosition.InsertTextInRun("…—");
                Assert.Contains("…—", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text);

                BodyReview.SetInvisibles(box.Document, true);
                BodyReview.SetInvisibles(box.Document, false);
                var captured = RichBody.Capture(box.Document);
                Assert.DoesNotContain("¶", captured.Text);
                Assert.DoesNotContain("«插图:", captured.Text);

                box.Selection.Text = "加粗的句子";
                bar.MatchBody(box.Selection);
                Assert.Contains("加粗的句子", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text);

                var body = new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;
                var start = box.Document.ContentStart.GetPositionAtOffset(2);
                var end = start?.GetPositionAtOffset(2);
                Assert.NotNull(start);
                Assert.NotNull(end);
                box.Selection.Select(start!, end!);
                BodyReview.MarkInserted(box.Selection);
                BodyReview.Accept(box);
                Pump();
                Assert.Contains("江南", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text);

                start = box.Document.ContentStart.GetPositionAtOffset(4);
                end = start?.GetPositionAtOffset(2);
                box.Selection.Select(start!, end!);
                var rejected = box.Selection.Text;
                BodyReview.MarkDeleted(box.Selection);
                BodyReview.Reject(box);
                Pump();
                Assert.Contains(rejected, new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text);

                start = box.Document.ContentStart.GetPositionAtOffset(6);
                end = start?.GetPositionAtOffset(1);
                box.Selection.Select(start!, end!);
                var removed = box.Selection.Text;
                BodyReview.MarkDeleted(box.Selection);
                BodyReview.Accept(box);
                Pump();
                if (removed.Length > 0)
                    Assert.DoesNotContain(removed, new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.Replace("加粗的句子", ""));

                box.CaretPosition = box.Document.ContentEnd;
                box.CaretPosition.InsertTextInRun("甲");
                var added = box.Document.ContentEnd.GetPositionAtOffset(-1);
                if (added is not null)
                {
                    box.Selection.Select(added, box.Document.ContentEnd);
                    BodyReview.MarkInserted(box.Selection);
                    BodyReview.Accept(box);
                }
                Pump();
                Assert.Contains("甲", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text);
                _ = body;
            }

            var src = Path.Combine(root, "source.png");
            File.WriteAllBytes(src, Png);
            for (var round = 1; round <= 3; round++)
            {
                box.Document.Blocks.Clear();
                box.Document.Blocks.Add(new Paragraph(new Run("上文")));
                box.CaretPosition = box.Document.ContentEnd;
                var stored = BodyFigures.Store(root, src);
                BodyFigures.Insert(box, stored);
                Assert.Contains(box.Document.Blocks.OfType<BlockUIContainer>(), b => b.Child is Image);
                var image = box.Document.Blocks.OfType<BlockUIContainer>().Select(b => b.Child).OfType<Image>().First();
                Assert.IsType<string>(image.Tag);
                box.CaretPosition = box.Document.ContentEnd;
                EditingCommands.Backspace.Execute(null, box);
                Pump();
                BodyFigures.Delete(box, image);
                Assert.DoesNotContain(box.Document.Blocks.OfType<BlockUIContainer>(), b => b.Child is Image);
                var after = RichBody.Capture(box.Document);
                Assert.DoesNotContain("«插图:", after.Text);
                Assert.Contains("上文", after.Text);
            }
        }
        finally
        {
            win.Close();
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录 */ }
        }
    }

    private static void Pump(DispatcherPriority priority = DispatcherPriority.ApplicationIdle)
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
