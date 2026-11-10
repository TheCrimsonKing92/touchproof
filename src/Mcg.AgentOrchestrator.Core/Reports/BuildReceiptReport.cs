namespace Mcg.AgentOrchestrator.Core;

public sealed class BuildReceiptReport
{
    public sealed record OriginTotal(string Origin, int Builds, double CpuMs, double WallMs, int CacheHits, int CpuUnavailable);
    public sealed record RepeatedTree(string Tree, int Compiles, string[] Origins, double RepeatCpuMs);
    public IReadOnlyList<OriginTotal> Origins { get; }
    public IReadOnlyList<RepeatedTree> Repeats { get; }
    public int Lines { get; }
    public int Skipped { get; }

    public BuildReceiptReport(IEnumerable<string> lines, DateTimeOffset? since = null)
    {
        var rows = new List<BuildReceipt>();
        foreach (var line in lines)
        {
            Lines++;
            if (!BuildReceipt.TryParse(line, out var row)) { Skipped++; continue; }
            if (since is null || row!.RecordedAt >= since) rows.Add(row!);
        }
        Origins = rows.GroupBy(row => row.Origin, StringComparer.Ordinal)
            .Select(group => new OriginTotal(group.Key, group.Count(), group.Sum(row => row.CpuMs ?? 0),
                group.Sum(row => row.WallMs), group.Count(row => row.Cache == "hit"), group.Count(row => row.CpuMs is null)))
            .OrderByDescending(row => row.CpuMs).ThenBy(row => row.Origin, StringComparer.Ordinal).ToArray();
        Repeats = rows.Where(row => row.Cache != "hit" && row.TreeDigest.StartsWith("wbc1:git-tree:", StringComparison.Ordinal))
            .GroupBy(row => row.TreeDigest, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => new RepeatedTree(group.Key, group.Count(),
                group.Select(row => row.Origin).Distinct().Order(StringComparer.Ordinal).ToArray(),
                group.OrderBy(row => row.RecordedAt).Skip(1).Sum(row => row.CpuMs ?? 0)))
            .OrderByDescending(row => row.RepeatCpuMs).ThenBy(row => row.Tree, StringComparer.Ordinal).ToArray();
    }
}
