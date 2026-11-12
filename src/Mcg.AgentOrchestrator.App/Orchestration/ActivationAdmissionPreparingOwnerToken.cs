namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ActivationAdmissionPreparingOwnerToken
{
    internal static string Format(IEnumerable<ActivationAdmissionPreparingOwner> owners) =>
        string.Join(",", owners.Select(owner => owner.Token).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

    internal static bool TryParse(string value, out IReadOnlyList<ActivationAdmissionPreparingOwner> owners)
    {
        var result = new List<ActivationAdmissionPreparingOwner>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        owners = [];
        if (value.Length == 0) return true;
        foreach (var token in value.Split(','))
        {
            var parts = token.Split(':');
            if (parts.Length != 2 || parts[0] is not ("train" or "cohort") ||
                parts[1].Length == 0 || !parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                !seen.Add(token)) return false;
            result.Add(new(parts[0], parts[1]));
        }
        owners = result;
        return true;
    }
}
