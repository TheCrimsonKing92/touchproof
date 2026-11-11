using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExperimentReplaySchedule
{
    internal static IReadOnlyList<ExperimentReplaySlot> Create(ExperimentReplayRegistration registration)
    {
        if (registration.Cases.Count != 20) throw new InvalidDataException("Fixed pilot requires 20 registered cases.");
        var result = new List<ExperimentReplaySlot>();
        var random = new Random(registration.Seed);
        var cases = registration.Cases.ToArray();
        random.Shuffle(cases);
        foreach (var item in cases.Take(3))
            for (var i = 0; i < 2; i++) Pair(item.Id, "AA", i, true);
        for (var i = 0; i < 10; i++) Pair(cases[i % 3].Id, "AA", i, false);
        foreach (var item in cases)
        {
            for (var i = 0; i < 2; i++) Pair(item.Id, "AB", i, true);
            for (var i = 0; i < 3; i++) Pair(item.Id, "AB", i, false);
        }
        return result;

        void Pair(string caseId, string stage, int pair, bool warmup)
        {
            var order = random.Next(2) == 0 ? new[] { "A", "B" } : ["B", "A"];
            foreach (var arm in order) result.Add(new(result.Count, caseId, stage, pair, arm, warmup));
        }
    }
}
