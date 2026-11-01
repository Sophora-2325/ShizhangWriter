using System.Globalization;
using System.Windows;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

/// <summary>One startup boundary for all works, including retries after a failed first read.</summary>
public sealed class SessionUsage(DateTimeOffset startedAt)
{
    public DateTimeOffset StartedAt { get; } = startedAt;
    public UsageDto Snapshot { get; private set; } = new();
    public bool HasRead { get; private set; }
    public bool IsStale { get; private set; }
    private bool _refreshing;

    public async Task RefreshAsync(Func<DateTimeOffset, CancellationToken, Task<UsageDto>> fetch,
        CancellationToken cancellation = default)
    {
        if (_refreshing || cancellation.IsCancellationRequested) return;
        _refreshing = true;
        try
        {
            var value = await fetch(StartedAt, cancellation);
            if (cancellation.IsCancellationRequested) return;
            Snapshot = value;
            HasRead = true;
            IsStale = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { IsStale = true; } // Keep the last successful numbers for a later retry.
        finally { _refreshing = false; }
    }
}

public static class UsageDisplay
{
    // A deliberately labelled, editable reference rate; never presented as a live quote.
    public const decimal DefaultRate = 7m;
    public static decimal Rate(decimal value) => value is >= 0.0001m and <= 1000m ? value : DefaultRate;

    public static bool TryRate(string value, out decimal rate) =>
        (decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out rate) ||
         decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out rate)) &&
        rate is >= 0.0001m and <= 1000m;

    public static string Money(decimal usd, decimal rate)
    {
        var cny = Math.Max(0m, usd) * Rate(rate);
        return cny is > 0m and < 0.0001m ? "< ¥0.0001" : "≈ ¥" + cny.ToString("0.00##", CultureInfo.InvariantCulture);
    }

    public static string Tokens(long value) => value >= 1_000_000
        ? (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : value >= 1000 ? (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : value.ToString(CultureInfo.InvariantCulture);

    public static Point Clamp(Point point, Size area, Size item) => new(
        Math.Clamp(double.IsFinite(point.X) ? point.X : 0, 0, Math.Max(0, area.Width - item.Width)),
        Math.Clamp(double.IsFinite(point.Y) ? point.Y : 0, 0, Math.Max(0, area.Height - item.Height)));
}
