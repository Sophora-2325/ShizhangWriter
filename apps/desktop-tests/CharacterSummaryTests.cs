using System.Windows;
using System.Windows.Controls;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class CharacterSummaryTests
{
    [Theory]
    [InlineData(220)] [InlineData(340)]
    public void Portrait_placeholder_sits_left_of_three_copyable_fields_and_long_name_wraps(double width) => StaRunner.Run(() =>
    {
        var card = new CharacterSummaryIdentity(new CharacterDto { Name = "很长的角色姓名用于检查窄卡片中的换行", Age = "17", Gender = "女", Role = "不在简略卡显示" }, "");
        card.Measure(new Size(width, double.PositiveInfinity)); card.Arrange(new Rect(0, 0, width, card.DesiredSize.Height)); card.UpdateLayout();
        var portrait = card.PortraitFrame.TransformToAncestor(card).TransformBounds(new Rect(card.PortraitFrame.RenderSize));
        var fields = card.IdentityFields.TransformToAncestor(card).TransformBounds(new Rect(card.IdentityFields.RenderSize));
        Assert.True(portrait.Right < fields.Left); Assert.True(portrait.Height >= 115.2);
        Assert.NotNull(card.PortraitFrame.Child);
        var text = card.IdentityFields.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<TextBox>()).ToArray();
        Assert.Equal(3, text.Length); Assert.All(text, t => { Assert.True(t.IsReadOnly); Assert.Equal(TextWrapping.Wrap, t.TextWrapping); });
        Assert.Equal("17", text[1].Text); Assert.Equal("女", text[2].Text);
        Assert.True(text[0].ActualHeight > text[1].ActualHeight);
        Assert.InRange(fields.Right, width - 1, width + 1);
    });

    [Fact]
    public void Typical_identity_height_is_approximately_sixty_percent_of_previous_height() => StaRunner.Run(() =>
    {
        var card = new CharacterSummaryIdentity(new CharacterDto { Name = "许晚", Age = "17", Gender = "女" }, "");
        card.Measure(new Size(280, double.PositiveInfinity));
        Assert.InRange(card.DesiredSize.Height, 192 * .6 - 1, 192 * .6 + 1);
    });
}
