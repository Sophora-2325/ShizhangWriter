using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class PaperLayoutTests
{
    [Fact]
    public void Paper_width_is_34_chars_plus_padding_at_default_size()
    {
        Assert.Equal(640, PaperLayout.Width(17, paper: true));
        Assert.Equal(572, PaperLayout.Width(15, paper: true));
    }

    [Fact]
    public void Full_mode_has_no_width_limit_and_falls_back_to_default_size()
    {
        Assert.True(double.IsPositiveInfinity(PaperLayout.Width(17, paper: false)));
        Assert.Equal(640, PaperLayout.Width(double.NaN, paper: true));
        Assert.Equal(PaperLayout.MinWidth, PaperLayout.Width(8, paper: true));
    }
}
