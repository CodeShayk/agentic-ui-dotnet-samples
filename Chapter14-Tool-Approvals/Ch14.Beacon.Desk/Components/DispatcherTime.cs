using System.Globalization;
using System.Text.Json;
using Microsoft.JSInterop;

namespace Ch14.Beacon.Desk.Components;

/// <summary>
/// Deadlines in the dispatcher's own time. Under Interactive Server this code runs on the server, where
/// <see cref="TimeZoneInfo.Local"/> is the server's zone (UTC in most containers), so the zone comes from the
/// browser, once per circuit. Until it is known, clock times show in UTC and say so.
/// </summary>
public sealed class DispatcherTime(TimeZoneInfo? zone = null)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>How long before a deadline the Desk warns that time is running out.</summary>
    public static readonly TimeSpan WarningPeriod = TimeSpan.FromMinutes(2);

    /// <summary>The browser's time zone, or <see langword="null"/> while it isn't known.</summary>
    public TimeZoneInfo? Zone { get; private set; } = zone;

    /// <summary>Reads the browser's IANA time zone from <c>Intl.DateTimeFormat</c>, through JS interop alone.</summary>
    public async Task DetectAsync(IJSRuntime js)
    {
        if (Zone is not null)
        {
            return;
        }

        try
        {
            await using var format = await js.InvokeAsync<IJSObjectReference>("Intl.DateTimeFormat");
            var options = await format.InvokeAsync<JsonElement>("resolvedOptions");
            if (options.TryGetProperty("timeZone", out var id) && id.GetString() is { Length: > 0 } name
                && TimeZoneInfo.TryFindSystemTimeZoneById(name, out var found))
            {
                Zone = found;
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            // No browser to ask (prerendering), or it went away: times stay in UTC, labeled.
        }
    }

    /// <summary>"10:13 AM" in the dispatcher's zone, or "15:13 UTC" while it isn't known.</summary>
    public string ClockTime(DateTimeOffset instant) => Zone is { } known
        ? TimeZoneInfo.ConvertTime(instant, known).ToString("h:mm tt", Culture)
        : instant.UtcDateTime.ToString("HH:mm", Culture) + " UTC";

    /// <summary>"9 minutes (by 10:13 AM)": whole minutes left, rounded up, then the clock time.</summary>
    public string TimeLeft(DateTimeOffset deadline, DateTimeOffset now)
    {
        var minutes = MinutesLeft(deadline, now);
        return $"{minutes} {(minutes == 1 ? "minute" : "minutes")} (by {ClockTime(deadline)})";
    }

    /// <summary>Whether the deadline is close enough to warn about, and not yet past.</summary>
    public static bool IsClose(DateTimeOffset deadline, DateTimeOffset now) => deadline > now && deadline - now <= WarningPeriod;

    public static int MinutesLeft(DateTimeOffset deadline, DateTimeOffset now) =>
        Math.Max(0, (int)Math.Ceiling((deadline - now).TotalMinutes));

    /// <summary>
    /// How long until the minutes left change, which is when a countdown must render again. The warning and the
    /// deadline itself fall on those moments too. <see cref="Timeout.InfiniteTimeSpan"/> once the deadline passed.
    /// </summary>
    public static TimeSpan UntilNextChange(DateTimeOffset deadline, DateTimeOffset now)
    {
        var left = deadline - now;
        return left <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : left - TimeSpan.FromMinutes(Math.Ceiling(left.TotalMinutes) - 1);
    }
}
