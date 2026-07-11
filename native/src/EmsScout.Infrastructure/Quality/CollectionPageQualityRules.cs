using System.Globalization;
using System.Text.RegularExpressions;

namespace EmsScout.Infrastructure.Quality;

internal static partial class CollectionPageQualityRules
{
    public static CollectionPageQualityResult Evaluate(
        IReadOnlyList<CollectionPageQualityCard> cards,
        CollectionPageQualityMeta? meta = null)
    {
        if (cards.Count == 0)
        {
            return CollectionPageQualityResult.Empty;
        }

        var count = cards.Count;
        var rawCount = PositiveOrDefault(meta?.RawCount, count);
        var uniqueCount = PositiveOrDefault(meta?.UniqueCount, count);
        var duplicateCollapse = rawCount >= 3 && uniqueCount <= Math.Max(1, rawCount / 2);
        var placeholderNames = cards.Count(card =>
            string.IsNullOrEmpty(card.Name) || card.Name == "0-0001-KT");
        var withResolvedState = cards.Count(card => IsResolvedCommunication(card.Comm));
        var activeCards = cards.Where(card => IsActive(card.Comm)).ToList();
        var activeWithSwitch = activeCards.Count(card => card.Switch is "ON" or "OFF");
        var activeWithMode = activeCards.Count(card => !string.IsNullOrEmpty(card.Mode) && card.Mode != "-");
        var activeWithFan = activeCards.Count(card => !string.IsNullOrEmpty(card.Fan) && card.Fan is not ("-" or "0"));
        var activeWithIndoor = activeCards.Count(card => IsRealIndoor(card.Indoor));
        var activeWithSetTemp = activeCards.Count(card => IsValidSetTemp(card.SetTemp));
        var invalidIndoor = cards.Count(card =>
            HasNumericValue(card.Indoor) && !IsValidIndoor(card.Indoor));
        var invalidSetTemp = cards.Count(card =>
        {
            var value = ParseJavaScriptFloat(card.SetTemp);
            return value is not null && value != 0 && !IsValidSetTemp(card.SetTemp);
        });
        var activeFieldOk = activeCards.Count == 0 ||
            (activeWithSwitch == activeCards.Count &&
             activeWithMode == activeCards.Count &&
             activeWithFan == activeCards.Count &&
             activeWithIndoor == activeCards.Count &&
             activeWithSetTemp == activeCards.Count);
        var uniformTemplate = IsUniformTemplate(cards);
        var allOffline = cards.All(card => card.Comm == "离线");
        var ok = placeholderNames == 0 &&
                 !duplicateCollapse &&
                 withResolvedState == count &&
                 !uniformTemplate &&
                 invalidIndoor == 0 &&
                 invalidSetTemp == 0 &&
                 activeFieldOk;
        var names = cards.Select(card => (card.Name ?? string.Empty).Trim()).ToList();
        var stableOfflineTemplateEligible = uniformTemplate &&
            allOffline &&
            !duplicateCollapse &&
            names.All(name => name.Length > 0 && name != "0-0001-KT") &&
            names.Distinct(StringComparer.Ordinal).Count() == count &&
            cards.All(card => !string.IsNullOrWhiteSpace(card.Indicator)) &&
            cards.All(card => card.Comm == "离线" && card.Switch == "-");

        return new CollectionPageQualityResult(
            ok,
            uniformTemplate,
            allOffline,
            duplicateCollapse,
            placeholderNames,
            withResolvedState,
            activeFieldOk,
            invalidIndoor,
            invalidSetTemp,
            stableOfflineTemplateEligible);
    }

    public static bool HasInvalidCardFields(CollectionPageQualityCard card)
    {
        var indoor = ParseJavaScriptFloat(card.Indoor);
        var setTemp = ParseJavaScriptFloat(card.SetTemp);
        if (indoor is < 0 or > 60 || setTemp is not null && setTemp != 0 && setTemp is < 5 or > 40)
        {
            return true;
        }

        return IsActive(card.Comm) &&
               (card.Switch is not ("ON" or "OFF") ||
                string.IsNullOrEmpty(card.Mode) || card.Mode == "-" ||
                string.IsNullOrEmpty(card.Fan) || card.Fan is "-" or "0" ||
                indoor is null or <= 0 or > 60 ||
                setTemp is null or < 5 or > 40);
    }

    public static bool IsKnownDefaultTemplateValues(
        string? indoor,
        string? setTemp,
        string? fan,
        string? mode) =>
        indoor == "0" && setTemp == "0" && fan == "0" ||
        indoor == "0" && setTemp == "0" && fan == "中" && mode == "制冷" ||
        indoor == "26" && setTemp == "25" && fan == "中" && mode == "制冷";

    private static bool IsUniformTemplate(IReadOnlyList<CollectionPageQualityCard> cards)
    {
        if (cards.Count < 2 ||
            cards.Select(card => card.Indoor).Distinct(StringComparer.Ordinal).Count() > 1 ||
            cards.Select(card => card.SetTemp).Distinct(StringComparer.Ordinal).Count() > 1 ||
            cards.Select(card => card.Fan).Distinct(StringComparer.Ordinal).Count() > 1 ||
            cards.Select(card => card.Mode).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return false;
        }

        var first = cards[0];
        return IsKnownDefaultTemplateValues(first.Indoor, first.SetTemp, first.Fan, first.Mode);
    }

    private static int PositiveOrDefault(int? value, int fallback) => value is > 0 ? value.Value : fallback;

    private static bool IsResolvedCommunication(string? value) => value is "开机" or "关机" or "离线";

    private static bool IsActive(string? value) => value is "开机" or "关机";

    private static bool HasNumericValue(string? value) => ParseJavaScriptFloat(value) is not null;

    private static bool IsValidIndoor(string? value)
    {
        var number = ParseJavaScriptFloat(value);
        return number is >= 0 and <= 60;
    }

    private static bool IsRealIndoor(string? value)
    {
        var number = ParseJavaScriptFloat(value);
        return number is > 0 and <= 60;
    }

    private static bool IsValidSetTemp(string? value)
    {
        var number = ParseJavaScriptFloat(value);
        return number is >= 5 and <= 40;
    }

    private static double? ParseJavaScriptFloat(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = JavaScriptFloatPrefix().Match(value);
        return match.Success &&
               double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               double.IsFinite(parsed)
            ? parsed
            : null;
    }

    [GeneratedRegex(@"^\s*[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptFloatPrefix();
}

internal sealed record CollectionPageQualityCard(
    string? Name,
    string? Switch,
    string? Mode,
    string? Indoor,
    string? SetTemp,
    string? Fan,
    string? Indicator,
    string? Comm);

internal sealed record CollectionPageQualityMeta(int? RawCount, int? UniqueCount);

internal sealed record CollectionPageQualityResult(
    bool Ok,
    bool UniformTemplate,
    bool AllOffline,
    bool DuplicateCollapse,
    int PlaceholderNames,
    int WithResolvedState,
    bool ActiveFieldOk,
    int InvalidIndoor,
    int InvalidSetTemp,
    bool StableOfflineTemplateEligible)
{
    public static CollectionPageQualityResult Empty { get; } =
        new(false, false, false, false, 0, 0, true, 0, 0, false);
}
