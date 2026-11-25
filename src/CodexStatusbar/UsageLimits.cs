using System.Globalization;
using System.Text.Json;

namespace CodexStatusbar;

internal enum LimitFreshness { Unavailable, Unsupported, Valid, Stale, AwaitingRefresh }

internal sealed record UsageLimitWindow(double Used, DateTimeOffset? Reset)
{
    public double Remaining => Math.Clamp(100 - Used, 0, 100);

    public LimitFreshness Freshness(DateTimeOffset recorded, DateTimeOffset now) =>
        Reset <= now ? LimitFreshness.AwaitingRefresh
        : Reset is null || now - recorded >= TimeSpan.FromMinutes(30) ? LimitFreshness.Stale
        : LimitFreshness.Valid;

    public static string Percent(double value) =>
        Math.Round(value, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture) + "%";

    public string Countdown(DateTimeOffset now)
    {
        if (Reset is not { } reset) return "N/A";
        if (reset <= now) return "Awaiting refresh";
        var minutes = (long)Math.Ceiling((reset - now).TotalMinutes);
        return minutes >= 1440 ? $"{minutes / 1440}d {minutes % 1440 / 60}h"
            : minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";
    }
}

internal sealed record UsageLimits(DateTimeOffset Recorded, UsageLimitWindow? FiveHour, UsageLimitWindow? Weekly,
    LimitFreshness MissingStatus, DateTimeOffset? LastValid = null)
{
    public const string Source = "Rollout token_count";
    public const string Id = "codex";

    public static UsageLimits? Read(JsonElement root, JsonElement payload, UsageLimits? previous)
    {
        if (!payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind == JsonValueKind.Null)
            return previous;
        if (!root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var recorded)
            || recorded > DateTimeOffset.UtcNow.AddMinutes(2))
            return previous;
        if (limits.ValueKind != JsonValueKind.Object || !limits.TryGetProperty("limit_id", out var id)
            || id.ValueKind != JsonValueKind.String || id.GetString() != Id)
            return previous ?? new UsageLimits(recorded, null, null, LimitFreshness.Unsupported);
        if (previous is not null && previous.MissingStatus != LimitFreshness.Unsupported && recorded <= previous.Recorded)
            return previous;

        var primary = ReadWindow(limits, "primary");
        var secondary = ReadWindow(limits, "secondary");
        var fiveHour = Match(300);
        var weekly = Match(10080);
        return new UsageLimits(recorded, fiveHour, weekly, LimitFreshness.Unavailable,
            fiveHour is not null || weekly is not null ? recorded : previous?.LastValid);

        UsageLimitWindow? Match(int expected) =>
            Math.Abs(primary.Minutes - (double)expected) <= expected * .05 ? primary.Window
            : Math.Abs(secondary.Minutes - (double)expected) <= expected * .05 ? secondary.Window
            : null;
    }

    private static (long Minutes, UsageLimitWindow? Window) ReadWindow(JsonElement limits, string key)
    {
        if (!limits.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("window_minutes", out var duration) || duration.ValueKind != JsonValueKind.Number
            || !duration.TryGetInt64(out var minutes) || !window.TryGetProperty("used_percent", out var percent)
            || percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var used)
            || !double.IsFinite(used) || used is < 0 or > 100)
            return default;
        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resets_at", out var time) && time.ValueKind == JsonValueKind.Number
            && time.TryGetInt64(out var seconds) && seconds is > 0 and <= 253402300799)
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return (minutes, new UsageLimitWindow(used, reset));
    }

    public string DisplayClockKey(DateTimeOffset now, bool expanded, DisplayField visible) =>
        $"{Clock(FiveHour, now, expanded || visible.HasFlag(DisplayField.FiveHourReset))}|{Clock(Weekly, now, expanded || visible.HasFlag(DisplayField.WeeklyReset))}"
        + (expanded ? $"|{Age(now)}" : "");

    public string Age(DateTimeOffset now) =>
        $"{Math.Max(0, (long)(now - (LastValid ?? Recorded)).TotalMinutes)}m ago";

    private string Clock(UsageLimitWindow? window, DateTimeOffset now, bool showCountdown) =>
        window is null ? "" : window.Freshness(Recorded, now).ToString() + (showCountdown ? window.Countdown(now) : "");

    public static string Status(LimitFreshness status) =>
        status == LimitFreshness.AwaitingRefresh ? "Awaiting refresh" : status.ToString();

    public static string LocalTime(DateTimeOffset? time, TimeZoneInfo? zone = null) =>
        time is { } utc ? TimeZoneInfo.ConvertTime(utc, zone ?? TimeZoneInfo.Local)
            .ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) : "N/A";
}
