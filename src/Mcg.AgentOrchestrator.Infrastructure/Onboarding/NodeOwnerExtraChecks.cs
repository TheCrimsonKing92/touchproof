namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Derives owner-declared command checks for the root Node unit.</summary>
internal static class NodeOwnerExtraChecks
{
    internal static IReadOnlyList<NodeVerificationChecks.Check> For(AppliedOwnerAnswers? applied)
    {
        if (applied is null)
            return [];

        // Extra keys need no snapshot question. Lift only answers rejected solely for not being live,
        // as LearnedAcceptanceManifest.CrosswalkDeclaredChecks does for declared commands.
        var answers = applied.AnsweredFacts.Select(fact => (Key: fact.FactKey, Value: fact.Fact.Value))
            .Concat(applied.StaleAnswers.Where(stale => stale.Reason == OwnerAnswerApplier.NotLiveReason)
                .Select(stale => (Key: stale.Answer.FactKey, Value: stale.Answer.Value)));
        var commands = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (key, value) in answers)
        {
            // NodeUnitCommandDeriver assigns the root Node unit the id ".".
            if (OwnerAnswerValidator.TryParseExtraKey(key, out var unit) && unit == ".")
                commands[key] = DeclaredCommandLine.Parse(OwnerAnswerValidator.Validate(key, value));
        }
        return commands.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new NodeVerificationChecks.Check(string.Join(" ", entry.Value), "command",
                entry.Value[0], null, entry.Value[1..])).ToArray();
    }
}
