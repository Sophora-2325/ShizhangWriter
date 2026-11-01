using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class BodyRevisionTests
{
    [Fact]
    public void Stale_reports_remain_viewable_in_readonly_chapters() => StaRunner.Run(() =>
    {
        var ch = new ChapterDto { No = 1, Chapter_Uid = "stale", Body_Text = "正文" };
        foreach (var key in new[] { "consistency_report", "plan_check" })
            ch.Stale_Results[key] = JsonSerializer.SerializeToElement(new ConsistencyReportDto {
                Flags = new() { new() { Point = "原报告问题", Evidence = "正文" } }, Semantic_Checked = true });
        Assert.Equal("原报告问题", AuditZone.StoredReport(ch, "consistency_report")!.Flags.Single().Point);
        Assert.Equal("原报告问题", AuditZone.StoredReport(ch, "plan_check")!.Flags.Single().Point);
        var zone = new AuditZone();
        zone.Resources.MergedDictionaries.Add(ThemeManager.CreatePalette(false, ThemeManager.Zhusha));
        zone.Render(ch);
        Assert.True(zone.FlagList.Children.Count > 2);
        var panel = new AiPanel(); panel.SetWorkflow(ch);
        panel.SetChapterState(true, false, true, true, false, true);
        Assert.True(panel.AuditButton.IsEnabled);
        Assert.True(panel.PlanCheckButton.IsEnabled);
    });

    static BodyRevisionTests() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static BodyRevisionViewDto View() => new() { Writable = true, Input_Token = "body-token",
        Chapter = new() { No = 1, Chapter_Uid = "uid", Title = "来信", Body_Text = "😀他说了很多话。\n保留的尾声。" } };

    [Fact]
    public void Emoji_offsets_and_unselected_formatted_text_are_preserved() => StaRunner.Run(() =>
    {
        var doc = new FlowDocument();
        var untouched = new Run("保留的尾声。") { FontWeight = FontWeights.Bold, Foreground = Brushes.Purple };
        doc.Blocks.Add(new Paragraph(new Run("😀他说了很多话。")));
        doc.Blocks.Add(new Paragraph(untouched));
        var body = RichBody.Capture(doc);
        var chapter = new ChapterDto { Body_Text = body.Text, Body_Rich = body.Rich };
        var result = BodyRevisionEdits.Apply(chapter, new[] { new BodyRevisionPatchDto { Id = "p", Before = "他说了很多话", After = "他沉默了", Start = 1, End = 7 } });
        Assert.Equal("😀他沉默了。\n保留的尾声。", result.Text);
        var revised = RichBody.BuildVerified(result.Text, result.Rich);
        var tail = revised.Blocks.OfType<Paragraph>().Last().Inlines.OfType<Run>().Single();
        Assert.Equal(FontWeights.Bold, tail.FontWeight);
        Assert.Equal(Colors.Purple, ((SolidColorBrush)tail.Foreground).Color);
        Assert.Equal(body.Text, RichBody.Capture(doc).Text);
    });

    [Fact]
    public void Ambiguous_cross_paragraph_and_protected_edits_fail_without_changing_source() => StaRunner.Run(() =>
    {
        var chapter = new ChapterDto { Body_Text = "同一句。同一句。\n尾声" };
        Assert.Throws<InvalidOperationException>(() => BodyRevisionEdits.Apply(chapter,
            new[] { new BodyRevisionPatchDto { Id = "p", Before = "同一句", After = "改", Start = 0, End = 3 } }));
        Assert.Equal("同一句。同一句。\n尾声", chapter.Body_Text);
        Assert.Throws<InvalidOperationException>(() => BodyRevisionEdits.Apply(chapter,
            new[] { new BodyRevisionPatchDto { Id = "p", Before = "。\n尾声", After = "改", Start = 7, End = 11 } }));
    });

    [Fact]
    public void Failed_save_keeps_input_and_explicit_empty_save_is_not_lost() => StaRunner.Run(() =>
    {
        var server = View(); var fail = true;
        using var session = new BodyRevisionSession("pid", Copy(server), (_, command) =>
        {
            if (fail) throw new IOException("offline");
            server.Workspace.Instruction = command.Instruction; server.Workspace.Revision++;
            return Task.FromResult(Copy(server));
        }, () => Task.FromResult(Copy(server)));
        session.Edit("作者要求", Array.Empty<string>());
        WpfPump.Wait(session.FlushAsync());
        Assert.True(session.HasPendingSave); Assert.Equal("作者要求", session.Instruction);
        fail = false; WpfPump.Wait(session.FlushAsync());
        Assert.False(session.HasPendingSave); Assert.Equal("作者要求", server.Workspace.Instruction);
        session.Edit("", Array.Empty<string>()); WpfPump.Wait(session.FlushAsync());
        Assert.Equal("", server.Workspace.Instruction);
    });
}
