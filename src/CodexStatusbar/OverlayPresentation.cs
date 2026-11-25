using System.Globalization;

namespace CodexStatusbar;

internal sealed record OverlayMetric(
    DisplayField Field,
    string CompactLabel,
    string ExpandedLabel,
    string Value,
    bool HasValue, double? RemainingBar = null, bool ShowLimitBar = false);

internal sealed record OverlayPresentation(
    OverlayMetric Primary,
    OverlayMetric Secondary,
    IReadOnlyList<OverlayMetric> ExpandedRows,
    double ContextPercent,
    bool ShowContextProgress,
    string? StatusText,
    string? CapsuleText = null,
    IReadOnlyList<string>? CapsuleVariants = null, string? ChatDetails = null)
{
    /// <summary>
    /// The collapsed strip's text at every supported verbosity, widest first. The last entry is the
    /// narrowest form that still carries all three metrics; callers pick the first one that fits and
    /// hide the strip when none does.
    /// </summary>
    public IReadOnlyList<string> Variants => CapsuleVariants ?? (CapsuleText is null ? [] : [CapsuleText]);

    /// <summary>The separator between the three metrics. A middle dot with single spaces: the double
    /// and triple spaces the strip used to carry read as a HUD, not as a native toolbar label.</summary>
    private const string Separator = " · ";

    /// <summary>The narrowest variant that is still worth showing: total and cache, no speed.</summary>
    public string NarrowestVariant => Variants.Count > 0 ? Variants[^1] : string.Empty;
}

internal static class OverlayPresentationBuilder
{
    private const string NoValue = "—";

    /// <summary>The separator between the three metrics. A middle dot with single spaces: the double
    /// and triple spaces the strip used to carry read as a HUD, not as a native toolbar label.</summary>
    private const string Separator = " · ";

    /// <summary>The collapsed strip before any conversation is known.</summary>
    public const string WaitingCapsuleText = "⚡ -- tok/s · -- tok · Cache --";

    /// <summary>
    /// The strip's verbosity ladder, widest first. Level 0 is what the user sees at normal widths —
    /// the full wording is never abbreviated by default; the shorter forms exist only to keep the
    /// strip from colliding with the composer's own buttons when Codex is dragged very narrow.
    /// </summary>
    public static IReadOnlyList<string> BuildCapsuleVariants(
        string tps,
        string total,
        string cache,
        bool hasUsage)
    {
        var tokens = hasUsage ? total : "--";
        var cacheText = hasUsage ? cache : "--";
        return
        [
            // 0 — full
            $"⚡ {tps} tok/s{Separator}{tokens} tok{Separator}Cache {cacheText}",

            // 1 — drop the unit suffix from the token count and the "Cache" word
            $"⚡ {tps} t/s{Separator}{tokens}{Separator}{cacheText}",

            // 2 — drop the lightning glyph too
            $"{tps} t/s{Separator}{tokens}{Separator}{cacheText}",

            // 3 — the two quantities that need no unit: total tokens and cache ratio
            $"{tokens}{Separator}{cacheText}"
        ];
    }

    public static OverlayPresentation CreateWaiting(
        string statusText,
        DisplayField primaryField,
        DisplayField secondaryField,
        DisplayField visibleFields)
    {
        ValidateHighlightedFields(primaryField, secondaryField);
        return new OverlayPresentation(
            CreateWaitingMetric(primaryField),
            CreateWaitingMetric(secondaryField),
            UsageRows(null, visibleFields, DateTimeOffset.UtcNow).Concat(
                CreateExpandedRows(visibleFields, primaryField, secondaryField, CreateWaitingMetric)).ToArray(),
            0,
            false,
            SanitizeSingleLine(statusText),
            WaitingCapsuleText,
            BuildCapsuleVariants(new TokenSnapshot("", "", 0, 0, 0, 0, 0, 0, 0, default,
                KnownUsageFields: DisplayField.None), visibleFields));
    }

    public static OverlayPresentation Create(
        TokenSnapshot snapshot,
        DisplayField primaryField,
        DisplayField secondaryField,
        DisplayField visibleFields, DateTimeOffset? clock = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateHighlightedFields(primaryField, secondaryField);
        var contextPercent = snapshot.ContextPercent;
        var now = clock ?? DateTimeOffset.UtcNow;
        var variants = BuildCapsuleVariants(snapshot, visibleFields, now);
        return new OverlayPresentation(
            CreateMetric(snapshot, primaryField, contextPercent),
            CreateMetric(snapshot, secondaryField, contextPercent),
            UsageRows(snapshot.Limits, visibleFields, now).Concat(
                CreateExpandedRows(visibleFields, primaryField, secondaryField,
                    field => CreateMetric(snapshot, field, contextPercent))).ToArray(),
            contextPercent,
            (visibleFields & DisplayField.ContextPercent) != 0 && snapshot.ContextUsedTokens > 0 && snapshot.ContextWindowTokens > 0,
            null,
            variants[0],
            variants);
    }

    internal static OverlayPresentation WithChat(OverlayPresentation presentation, ChatBinding binding, TokenSnapshot? snapshot)
    {
        var manual = binding.Mode == ChatBindingMode.Manual;
        var id = manual ? binding.ThreadId : snapshot?.ThreadId;
        var details = new List<OverlayMetric>();
        void Add(string label, string? value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "—" : SanitizeSingleLine(value);
            details.Add(new OverlayMetric(DisplayField.Thread, label, label, text, true));
        }
        Add("Chat", snapshot?.Title);
        Add("Project", snapshot?.Project);
        Add("Binding", manual ? binding.FromCommandLine ? "Manual (--thread)" : "Manual" : "Automatic");
        Add("Chat ID", id);
        var variants = presentation.Variants.Select(value => manual
            ? "📌 " + value.TrimStart('⚡', ' ') : value).ToArray();
        var panelRows = new List<OverlayMetric>(details);
        if (id?.Length == 36)
        {
            var idRow = panelRows[^1];
            panelRows[^1] = idRow with { Value = id[..18] };
            panelRows.Add(idRow with { ExpandedLabel = "", Value = id[18..] });
        }
        return presentation with
        {
            StatusText = null,
            CapsuleText = variants.FirstOrDefault(),
            CapsuleVariants = variants,
            ChatDetails = string.Join(Environment.NewLine, details.Select(row => $"{row.ExpandedLabel}: {row.Value}")),
            ExpandedRows = presentation.ExpandedRows.Where(row => row.Field != DisplayField.Thread).Concat(panelRows).ToArray()
        };
    }

    public static string FormatTokenCount(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    public static string ShortThreadId(string threadId, int maximumLength = 12)
    {
        var singleLineThreadId = SanitizeSingleLine(threadId);
        if (singleLineThreadId.Length <= maximumLength)
        {
            return singleLineThreadId;
        }

        if (maximumLength <= 1)
        {
            return maximumLength == 1 ? "…" : string.Empty;
        }

        var prefixLength = Math.Min(4, maximumLength - 1);
        var suffixLength = Math.Min(6, maximumLength - prefixLength - 1);
        return string.Concat(
            singleLineThreadId.AsSpan(0, prefixLength),
            "…",
            singleLineThreadId.AsSpan(singleLineThreadId.Length - suffixLength, suffixLength));
    }

    public static string GetFieldMenuText(DisplayField field)
    {
        return field switch
        {
            DisplayField.Total => "Total tokens",
            DisplayField.Input => "Input tokens",
            DisplayField.Output => "Output tokens",
            DisplayField.CacheHit => "Cached input tokens",
            DisplayField.CacheHitRate => "Cache hit rate",
            DisplayField.CacheMiss => "Uncached input tokens",
            DisplayField.Context => "Context usage",
            DisplayField.ContextPercent => "Context percentage",
            DisplayField.Reasoning => "Reasoning output tokens",
            DisplayField.ContextWindow => "Context window size",
            DisplayField.ContextRemaining => "Remaining context",
            DisplayField.LastTurn => "Last completed turn tokens",
            DisplayField.FiveHourLimit => "5-hour remaining",
            DisplayField.WeeklyLimit => "Weekly remaining",
            DisplayField.FiveHourReset => "5-hour reset countdown",
            DisplayField.WeeklyReset => "Weekly reset countdown",
            DisplayField.Thread => "Chat ID",
            DisplayField.Tps => "Output speed (tokens/s)",
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported display field.")
        };
    }

    public static IReadOnlyList<string> BuildCapsuleVariants(TokenSnapshot snapshot,
        DisplayField fields = DisplayFieldRules.SupportedMask, DateTimeOffset? clock = null)
    {
        string Count(long? count) => count is long value ? MetricFormat.CompactTokens(value) : "--";
        var tps = MetricFormat.FormatTps(snapshot.Tps, snapshot.TpsHeld);
        var used = Count(snapshot.UsedTokens ?? (snapshot.Has(DisplayField.Total) ? snapshot.TotalTokens : null));
        var usedLabel = snapshot.UsedTokens.HasValue ? "used" : "total";
        var input = Count(snapshot.Has(DisplayField.Input) ? snapshot.InputTokens : null);
        var output = Count(snapshot.Has(DisplayField.Output) ? snapshot.OutputTokens : null);
        var cache = snapshot.CacheHitPercent?.ToString("0", CultureInfo.InvariantCulture) is string percent
            ? percent + "%" : "--";
        string Line(params (DisplayField Field, string Text)[] parts) =>
            string.Join(Separator, parts.Where(part => (fields & part.Field) != 0).Select(part => part.Text));

        var now = clock ?? DateTimeOffset.UtcNow;
        var five = snapshot.Limits?.FiveHour;
        var week = snapshot.Limits?.Weekly;
        string Remaining(UsageLimitWindow? window) => window is null ? "N/A"
            : window.Freshness(snapshot.Limits!.Recorded, now) switch
            {
                LimitFreshness.AwaitingRefresh => "wait",
                LimitFreshness.Stale => "~" + UsageLimitWindow.Percent(window.Remaining),
                _ => UsageLimitWindow.Percent(window.Remaining)
            };
        string Clock(UsageLimitWindow? window) => window?.Countdown(now) is { } countdown && countdown != "Awaiting refresh"
            ? countdown : window is null ? "N/A" : "wait";
        var fiveText = "5h " + Remaining(five);
        var weekText = "W " + Remaining(week);
        var full = Line((DisplayField.Tps, $"⚡ {tps} tok/s"), (DisplayField.Total, $"{used} {usedLabel}"),
            (DisplayField.Input, $"{input} in"), (DisplayField.Output, $"{output} out"),
            (DisplayField.CacheHitRate, $"Cache {cache}"), (DisplayField.FiveHourLimit, fiveText),
            (DisplayField.WeeklyLimit, weekText), (DisplayField.FiveHourReset, "5h reset " + Clock(five)),
            (DisplayField.WeeklyReset, "W reset " + Clock(week)));
        return new[]
        {
            full,
            Line((DisplayField.Tps, $"⚡ {tps} tok/s"), (DisplayField.Total, $"{used} {usedLabel}"),
                (DisplayField.Input, $"{input} in"), (DisplayField.Output, $"{output} out"),
                (DisplayField.CacheHitRate, $"Cache {cache}"), (DisplayField.FiveHourLimit, fiveText), (DisplayField.WeeklyLimit, weekText)),
            Line((DisplayField.Tps, $"{tps} t/s"), (DisplayField.Total, $"{used} {usedLabel}"),
                (DisplayField.Input, $"↑{input}"), (DisplayField.Output, $"↓{output}"),
                (DisplayField.CacheHitRate, cache), (DisplayField.FiveHourLimit, fiveText), (DisplayField.WeeklyLimit, weekText)),
            Line((DisplayField.Tps, $"{tps} t/s"), (DisplayField.Total, $"{used} {usedLabel}"),
                (DisplayField.CacheHitRate, cache), (DisplayField.FiveHourLimit, fiveText), (DisplayField.WeeklyLimit, weekText)),
            Line((DisplayField.Total, $"{used} {usedLabel}"), (DisplayField.FiveHourLimit, fiveText), (DisplayField.WeeklyLimit, weekText)),
            Line((DisplayField.Total, $"{used} {usedLabel}"), (DisplayField.CacheHitRate, cache)),
            Line((DisplayField.Total, $"{used} {usedLabel}"))
        }.Where(line => line.Length > 0).DefaultIfEmpty(WaitingCapsuleText).Distinct().ToArray();
    }

    public static string BuildCapsuleText(TokenSnapshot snapshot) => BuildCapsuleVariants(snapshot)[0];

    private static void ValidateHighlightedFields(DisplayField primaryField, DisplayField secondaryField)
    {
        if (!DisplayFieldRules.IsSingleSupported(primaryField)
            || !DisplayFieldRules.IsSingleSupported(secondaryField))
        {
            throw new ArgumentException("Select one supported metric for the collapsed view.");
        }
    }

    private static IReadOnlyList<OverlayMetric> CreateExpandedRows(
        DisplayField visibleFields,
        DisplayField primaryField,
        DisplayField secondaryField,
        Func<DisplayField, OverlayMetric> createMetric)
    {
        return DisplayFieldRules.Ordered
            .Where(field => (visibleFields & field) != 0 && (DisplayFieldRules.LimitMask & field) == 0)
            .Where(field => field != primaryField && field != secondaryField)
            .Select(createMetric)
            .ToArray();
    }

    private static IEnumerable<OverlayMetric> UsageRows(UsageLimits? limits, DisplayField fields, DateTimeOffset now)
    {
        if ((fields & DisplayFieldRules.LimitMask) == 0) yield break;
        OverlayMetric Row(string label, string value, double? bar = null, bool showBar = false) =>
            new(DisplayField.FiveHourLimit, label, label, value, value != "N/A", bar, showBar);
        yield return Row("Usage limits", limits is null || limits.MissingStatus == LimitFreshness.Unsupported ? "N/A" : UsageLimits.Id);
        foreach (var (label, window, mask) in new[]
        {
            ("5-hour", limits?.FiveHour, DisplayField.FiveHourLimit | DisplayField.FiveHourReset),
            ("Weekly", limits?.Weekly, DisplayField.WeeklyLimit | DisplayField.WeeklyReset)
        })
        {
            if ((fields & mask) == 0) continue;
            var status = window?.Freshness(limits!.Recorded, now) ?? limits?.MissingStatus ?? LimitFreshness.Unavailable;
            var known = window is not null;
            yield return Row(label, UsageLimits.Status(status));
            yield return Row(status is LimitFreshness.AwaitingRefresh or LimitFreshness.Stale ? "Last left / used" : "Remaining / used", known
                ? $"{UsageLimitWindow.Percent(window!.Remaining)} / {UsageLimitWindow.Percent(window.Used)}"
                : "N/A", known && status != LimitFreshness.AwaitingRefresh ? window!.Remaining : null, showBar: true);
            yield return Row("Resets in", window?.Countdown(now) ?? "N/A");
            yield return Row("Local reset", UsageLimits.LocalTime(window?.Reset));
        }
        yield return Row("Source", limits is not null ? UsageLimits.Source : "N/A");
        yield return Row("Last valid update", UsageLimits.LocalTime(limits?.LastValid));
        yield return Row("Valid snapshot age", limits?.LastValid is not null ? limits.Age(now) : "N/A");
    }

    private static OverlayMetric CreateWaitingMetric(DisplayField field)
    {
        var labels = GetLabels(field);
        return new OverlayMetric(field, labels.Compact, labels.Expanded, NoValue, false);
    }

    private static OverlayMetric CreateMetric(
        TokenSnapshot snapshot,
        DisplayField field,
        double contextPercent)
    {
        var labels = GetLabels(field);
        string? Count(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture);
        var contextKnown = snapshot.ContextUsedTokens > 0;
        var windowKnown = snapshot.ContextWindowTokens > 0;
        var value = field switch
        {
            DisplayField.Total => Count(snapshot.Has(field) ? snapshot.TotalTokens : null),
            DisplayField.Input => Count(snapshot.Has(field) ? snapshot.InputTokens : null),
            DisplayField.Output => Count(snapshot.Has(field) ? snapshot.OutputTokens : null),
            DisplayField.CacheHit => Count(snapshot.Has(field) ? snapshot.CachedInputTokens : null),
            DisplayField.CacheHitRate => snapshot.CacheHitPercent?.ToString("0.##", CultureInfo.InvariantCulture) is string hit ? hit + "%" : null,
            DisplayField.CacheMiss => Count(snapshot.Has(DisplayField.Input | DisplayField.CacheHit) ? snapshot.UncachedInputTokens : null),
            DisplayField.Context => Count(contextKnown ? snapshot.ContextUsedTokens : null),
            DisplayField.ContextWindow => Count(windowKnown ? snapshot.ContextWindowTokens : null),
            DisplayField.ContextRemaining => Count(contextKnown && windowKnown
                ? Math.Max(0, snapshot.ContextWindowTokens - snapshot.ContextUsedTokens) : null),
            DisplayField.ContextPercent => contextKnown && windowKnown ? $"{contextPercent:0}%" : null,
            DisplayField.LastTurn => Count(snapshot.LastTurnTokens),
            DisplayField.FiveHourLimit or DisplayField.WeeklyLimit or DisplayField.FiveHourReset or DisplayField.WeeklyReset => null,
            DisplayField.Reasoning => Count(snapshot.Has(field) ? snapshot.ReasoningOutputTokens : null),
            DisplayField.Thread => string.IsNullOrWhiteSpace(snapshot.ThreadId) ? null : ShortThreadId(snapshot.ThreadId),
            DisplayField.Tps => snapshot.Tps is not null ? FormatTpsValue(snapshot) : null,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported display field.")
        };
        return new OverlayMetric(field, labels.Compact, labels.Expanded, value ?? "N/A", value is not null);
    }

    private static (string Compact, string Expanded) GetLabels(DisplayField field)
    {
        return field switch
        {
            DisplayField.Total => ("Total", "Total tokens"),
            DisplayField.Input => ("In", "Input tokens"),
            DisplayField.Output => ("Out", "Output tokens"),
            DisplayField.CacheHit => ("Cached", "Cached input"),
            DisplayField.CacheHitRate => ("Hit rate", "Cache hit rate"),
            DisplayField.CacheMiss => ("Uncached", "Uncached input"),
            DisplayField.Context => ("Context", "Current context"),
            DisplayField.ContextPercent => ("Context", "Context utilization"),
            DisplayField.Reasoning => ("Reasoning", "Reasoning output"),
            DisplayField.ContextWindow => ("Window", "Context window"),
            DisplayField.ContextRemaining => ("Remaining", "Remaining context"),
            DisplayField.LastTurn => ("Last turn", "Last turn tokens"),
            DisplayField.FiveHourLimit or DisplayField.WeeklyLimit or DisplayField.FiveHourReset or DisplayField.WeeklyReset
                => (GetFieldMenuText(field), GetFieldMenuText(field)),
            DisplayField.Thread => ("Chat", "Chat"),
            DisplayField.Tps => ("⚡", "Generation speed"),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported display field.")
        };
    }

    /// <summary>TPS cell used by the expanded panel and the tray menu: e.g. "243 tok/s", "-- tok/s".</summary>
    private static string FormatTpsValue(TokenSnapshot snapshot) =>
        MetricFormat.FormatTps(snapshot.Tps, snapshot.TpsHeld) + " tok/s";

    private static string SanitizeSingleLine(string value)
    {
        return value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}

internal sealed class PresentationProbeRequest
{
    public List<PresentationProbeCase> Cases { get; set; } = new();
}

internal sealed class PresentationProbeCase
{
    public string Name { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public TokenSnapshot? Snapshot { get; set; }
    public int? PrimaryField { get; set; }
    public int? SecondaryField { get; set; }
    public int? VisibleFields { get; set; }
    public string? StatusText { get; set; }
}

internal sealed record PresentationProbeCaseResult(string Name, OverlayPresentation Presentation);

internal sealed record PresentationProbeResult(IReadOnlyList<PresentationProbeCaseResult> Cases);

internal static class PresentationProbe
{
    public static PresentationProbeResult Execute(PresentationProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<PresentationProbeCaseResult>();
        foreach (var probeCase in request.Cases)
        {
            var primaryField = RequireField(probeCase.PrimaryField, nameof(probeCase.PrimaryField));
            var secondaryField = RequireField(probeCase.SecondaryField, nameof(probeCase.SecondaryField));
            var visibleFields = (DisplayField)(probeCase.VisibleFields ?? 0);
            var presentation = probeCase.Operation switch
            {
                "Create" => OverlayPresentationBuilder.Create(
                    probeCase.Snapshot ?? throw new ArgumentException("Create requires Snapshot.", nameof(probeCase)),
                    primaryField,
                    secondaryField,
                    visibleFields),
                "Waiting" => OverlayPresentationBuilder.CreateWaiting(
                    probeCase.StatusText ?? string.Empty,
                    primaryField,
                    secondaryField,
                    visibleFields),
                _ => throw new ArgumentException($"Unsupported presentation probe operation: {probeCase.Operation}", nameof(probeCase))
            };
            results.Add(new PresentationProbeCaseResult(probeCase.Name, presentation));
        }

        return new PresentationProbeResult(results);
    }

    private static DisplayField RequireField(int? value, string parameterName)
    {
        if (!value.HasValue)
        {
            throw new ArgumentException("The presentation probe requires a field.", parameterName);
        }

        return (DisplayField)value.Value;
    }
}
