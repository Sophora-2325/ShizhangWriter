using System.Globalization;
using System.Windows;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class UsageTests
{
    static UsageTests() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
    [Fact]
    public async Task Retry_keeps_the_original_startup_boundary_and_last_good_numbers()
    {
        var start = DateTimeOffset.UtcNow;
        var session = new SessionUsage(start);
        await session.RefreshAsync((since, _) => { Assert.Equal(start, since); throw new IOException(); });
        Assert.True(session.IsStale);
        Assert.False(session.HasRead);
        var first = new UsageDto { Cost_Usd = .1m, Input_Tokens = 100, Output_Tokens = 25, Cached_Tokens = 40 };
        await session.RefreshAsync((since, _) => { Assert.Equal(start, since); return Task.FromResult(first); });
        Assert.False(session.IsStale);
        Assert.Equal(125, session.Snapshot.TotalTokens);
        await session.RefreshAsync((_, _) => throw new IOException());
        Assert.Same(first, session.Snapshot);
        Assert.True(session.IsStale);
        await session.RefreshAsync((_, _) => Task.FromResult(new UsageDto { Calls = 3 }));
        Assert.False(session.IsStale);
        Assert.Equal(3, session.Snapshot.Calls);
    }

    [Fact]
    public async Task Refreshes_do_not_overlap_or_publish_after_close()
    {
        var pending = new TaskCompletionSource<UsageDto>();
        var session = new SessionUsage(DateTimeOffset.UtcNow);
        using var cancel = new CancellationTokenSource();
        var read = session.RefreshAsync((_, _) => pending.Task, cancel.Token);
        await session.RefreshAsync((_, _) => throw new Exception("Must not start another read"));
        Assert.False(session.IsStale);
        cancel.Cancel();
        pending.SetResult(new UsageDto { Calls = 1 });
        await read;
        Assert.False(session.HasRead);
    }

    [Fact]
    public void Amounts_convert_to_yuan_and_preserve_small_nonzero_costs()
    {
        Assert.Equal("≈ ¥0.70", UsageDisplay.Money(.1m, 7));
        Assert.Equal("≈ ¥0.75", UsageDisplay.Money(.1m, 7.5m));
        Assert.Equal("< ¥0.0001", UsageDisplay.Money(.000001m, 7));
        Assert.Equal("≈ ¥0.00", UsageDisplay.Money(0, 7));
        Assert.Equal("48.2K", UsageDisplay.Tokens(48200));
        Assert.Equal(UsageDisplay.DefaultRate, UsageDisplay.Rate(-1));
        Assert.False(UsageDisplay.TryRate("0", out _));
        Assert.False(UsageDisplay.TryRate("NaN", out _));
        Assert.False(UsageDisplay.TryRate("1001", out _));
        Assert.True(UsageDisplay.TryRate("7.25", out _));
    }

    [Fact]
    public void Drag_positions_stay_inside_the_workspace_after_resizing()
    {
        Assert.Equal(new Point(0, 0), UsageDisplay.Clamp(new(-100, -1), new(900, 600), new(270, 44)));
        Assert.Equal(new Point(630, 556), UsageDisplay.Clamp(new(4000, 2000), new(900, 600), new(270, 44)));
        Assert.Equal(new Point(0, 0), UsageDisplay.Clamp(new(double.NaN, double.PositiveInfinity), new(200, 30), new(270, 44)));
    }

    [Theory]
    [InlineData("zh-CN")] [InlineData("en-US")] [InlineData("ja-JP")]
    public void Usage_text_is_available_in_all_languages(string locale) => StaRunner.Run(() =>
    {
        var resources = LanguageManager.LoadStrings(locale);
        foreach (var key in new[] { "Title", "Short", "Scope", "Estimate", "UnknownCalls", "Stale", "RateLabel", "RateError", "Show", "Reset", "Footer" })
            Assert.IsType<string>(resources["Usage." + key]);
    });
}
