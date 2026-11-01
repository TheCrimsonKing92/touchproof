using System.Globalization;
using Token = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Predictions belong to a compatible cohort: unrelated class timings cannot supply its median.
internal sealed class FocusedEvidenceGroupCostModel
{
    internal const double HeavyThresholdSeconds = 300;
    private readonly Dictionary<string, double> _known = new(StringComparer.Ordinal);
    private readonly double? _median;

    internal FocusedEvidenceGroupCostModel(string project, IEnumerable<Token> cohort, IClassDurationHistory? history = null)
    {
        foreach (var name in Classes(cohort))
        {
            double? seconds;
            try { seconds = (history ?? NoClassDurationHistory.Instance).PredictSeconds(GoalAcceptanceVerifier.ProjectLabel(project) + ":" + name); }
            catch (Exception) { seconds = null; }
            if (seconds is { } value && double.IsFinite(value) && value >= 0) _known[name] = value;
        }
        _median = _known.Count == 0 ? null : AcceptanceBalancedLanePacker.Median(_known.Values);
    }

    internal double? PredictSeconds(IEnumerable<Token> tokens)
    {
        if (_median is not { } median) return null;
        var names = Classes(tokens).ToArray();
        if (names.Length == 0) return null;
        var total = names.Sum(name => _known.GetValueOrDefault(name, median));
        return double.IsFinite(total) ? total : null;
    }

    internal static double? Sum(IEnumerable<double?> values)
    {
        var items = values.ToArray();
        if (items.Any(value => value is null)) return null;
        var total = items.Sum(value => value!.Value);
        return double.IsFinite(total) ? total : null;
    }

    internal static bool IsHeavy(double? seconds) => seconds is { } value && double.IsFinite(value) && value > HeavyThresholdSeconds;
    internal static string Format(double? seconds) => seconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";
    private static IEnumerable<string> Classes(IEnumerable<Token> tokens) => tokens
        .Where(token => !string.IsNullOrWhiteSpace(token.ContainingClass))
        .Select(token => token.ContainingClass).Distinct(StringComparer.Ordinal);
}
