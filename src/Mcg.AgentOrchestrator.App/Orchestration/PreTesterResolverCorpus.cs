using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.PreTesterReverseDependencyObservation;
using Provenance = Mcg.AgentOrchestrator.App.Orchestration.PreTesterChangedFileGuardClasses.Provenance;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Offline input for the parent replay harness. This path never acquires conductor ownership.
internal sealed record PreTesterResolverCorpus(string Sha256, WindowHeader Header, Manifest Manifest, Producer[] Producers,
    PreTesterResolverCorpus.ReplayCase[] Cases)
{
    internal const string CandidateRevision = "e0042d3f51c90944ec03ff5f0055850762505ea7";
    internal const string OracleRevision = "08f295076eae4d1c14660a846e3c917de32d7a9d";
    internal const string OracleResolverBlob = "cbe48e555e77eecaba1b372af7464f1f61536ed3";

    internal static PreTesterResolverCorpus Read(string corpusPath, string expectedSha256)
    {
        var bytes = File.ReadAllBytes(corpusPath);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        Require(digest.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase), "Corpus SHA-256 mismatch.");
        var seal = JsonSerializer.Deserialize<Seal>(File.ReadAllBytes(corpusPath + ".receipt.json"));
        Require(seal is { Version: Schema, Algorithm: "SHA-256", Status: "complete" or "insufficient-coverage" } &&
            digest.Equals(seal.Sha256, StringComparison.OrdinalIgnoreCase), "Corpus has no matching complete seal.");

        using var input = new StreamReader(new MemoryStream(bytes));
        var header = ReadLine<WindowHeader>(input.ReadLine());
        Require(header.Version == Schema && header.WindowId != Guid.Empty && header.WindowId == seal!.WindowId &&
            header.Deadline - header.Started == Duration && IsProducer(header.Producer),
            "Invalid corpus window or producer identity.");

        var cases = new List<ReplayCase>();
        var producers = new List<Producer> { header.Producer };
        var observed = new Dictionary<string, int>(StringComparer.Ordinal);
        var completedTicks = 0;
        var sessionEntries = 0;
        SessionCheckpoint? checkpoint = null;
        var end = header.Started;
        while (input.ReadLine() is { } line)
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty(nameof(SessionCheckpoint.SuspendedAt), out _))
            {
                Require(checkpoint is null, "Corpus has consecutive producer checkpoints.");
                checkpoint = ReadLine<SessionCheckpoint>(line);
                Require(checkpoint.SessionId == producers[^1].SessionId && checkpoint.Token != Guid.Empty &&
                    checkpoint.SuspendedAt >= end && checkpoint.SuspendedAt < header.Deadline &&
                    checkpoint.CompletedTicks == completedTicks && checkpoint.InvocationCount == cases.Count &&
                    WorkloadsMatch(checkpoint.Workloads, observed), "Invalid corpus producer checkpoint.");
                continue;
            }
            if (json.RootElement.TryGetProperty(nameof(SessionStart.PreviousSessionId), out _))
            {
                var successor = ReadLine<SessionStart>(line);
                Require(checkpoint is not null && checkpoint.SessionId == successor.PreviousSessionId &&
                    checkpoint.Token == successor.Token && successor.Started >= checkpoint.SuspendedAt &&
                    IsProducer(successor.Producer) &&
                    successor.Producer.AuthorityDirectory == header.Producer.AuthorityDirectory &&
                    producers.All(producer => producer.SessionId != successor.Producer.SessionId),
                    "Invalid corpus producer continuation.");
                producers.Add(successor.Producer);
                sessionEntries = 0;
                end = successor.Started;
                checkpoint = null;
                continue;
            }
            Require(checkpoint is null, "Corpus has an unacknowledged producer checkpoint.");
            if (json.RootElement.TryGetProperty(nameof(PreTesterReverseDependencyObservation.Manifest.CompleteTickCount), out _))
            {
                var manifest = ReadLine<Manifest>(line);
                Require(completedTicks > 0 && manifest.CompleteTickCount == completedTicks &&
                    manifest.InvocationCount == cases.Count && manifest.SealedAt >= header.Deadline && manifest.SealedAt >= end &&
                    producers[^1].SessionId == seal!.SessionId && WorkloadsMatch(manifest.Workloads, observed) && input.EndOfStream,
                    "Corpus manifest counts do not match the ordered records.");
                var coverage = manifest.Workloads.Count(workload =>
                    workload.Names.Distinct(StringComparer.Ordinal).Take(2).Count() == 2) >= 2;
                Require(manifest.HasCpuCoverage == coverage &&
                    coverage == (seal!.Status == "complete"), "Corpus workload coverage does not match its receipt.");
                return new(digest, header, manifest, producers.ToArray(), cases.ToArray());
            }

            var tick = ReadLine<CompletedTick>(line);
            Require(tick.Number == ++completedTicks && tick.SessionId == producers[^1].SessionId &&
                tick.Started >= end && tick.Started < header.Deadline &&
                tick.Ended >= tick.Started && tick.Ended <= header.Deadline &&
                tick.Rows is not null && tick.BoundaryEntries == tick.Rows.Length,
                "Incomplete or out-of-order corpus tick.");
            end = tick.Ended;
            foreach (var row in tick.Rows!)
            {
                Require(row is not null && row.Ordinal == ++sessionEntries && row.Boundary is not null &&
                    !string.IsNullOrWhiteSpace(row.Boundary.GoalId) && !string.IsNullOrWhiteSpace(row.Boundary.TesterTaskId) &&
                    !string.IsNullOrWhiteSpace(row.Boundary.GoalCandidateRevision) &&
                    !string.IsNullOrWhiteSpace(row.Project) && row.Names is { Length: > 0 } &&
                    row.Paths is { Length: > 0 } && row.Inventory is not null &&
                    (row.UnresolvedName is null ? row.Cost is not null : row.Cost is null &&
                        row.Names.Contains(row.UnresolvedName, StringComparer.Ordinal)),
                    "Invalid, missing, or out-of-order corpus invocation.");
                var key = WorkloadKey(row!.Project, row.Names);
                observed[key] = observed.GetValueOrDefault(key) + 1;
                cases.Add(new(tick.SessionId, row,
                    row.Inventory.Select(name => new AcceptanceTestClassDescriptor(name, null)).ToArray()));
            }
        }
        throw new InvalidDataException("Corpus manifest is missing.");
    }

    internal static void Replay(string corpusPath, string expectedSha256, string receiptPath,
        ReplayBuild candidate, ReplayBuild oracle, Func<ReplayBuild, ReplayCase, ReplayDecision> evaluate)
    {
        var corpus = Read(corpusPath, expectedSha256);
        using var candidateBytes = File.OpenRead(candidate.AppPath);
        using var oracleBytes = File.OpenRead(oracle.AppPath);
        VerifyBuild(candidate, CandidateRevision, candidateBytes);
        VerifyBuild(oracle, OracleRevision, oracleBytes);
        Require(!candidate.Revision.Equals(oracle.Revision, StringComparison.OrdinalIgnoreCase) &&
            !candidate.AppSha256.Equals(oracle.AppSha256, StringComparison.OrdinalIgnoreCase),
            "Candidate and oracle identities must be distinct.");

        // Reserve a separate destination before running either arm. CreateNew also protects the corpus itself.
        using var output = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var comparisons = corpus.Cases.Select(row =>
        {
            var expected = evaluate(oracle, row);
            var actual = evaluate(candidate, row);
            return new Comparison(row.SessionId, row.Invocation.Ordinal, expected, actual,
                expected.UnresolvedName == actual.UnresolvedName &&
                expected.Selections.SequenceEqual(actual.Selections) &&
                expected.Provenance.SequenceEqual(actual.Provenance));
        }).ToArray();
        JsonSerializer.Serialize(output, new ReplayReceipt(corpus.Sha256, corpus.Header.WindowId,
            corpus.Producers, candidate, oracle, OracleResolverBlob, comparisons));
        output.Flush(flushToDisk: true);
    }

    private static void VerifyBuild(ReplayBuild build, string startingRevision, Stream bytes)
    {
        Require(build.StartingRevision.Equals(startingRevision, StringComparison.OrdinalIgnoreCase) &&
            IsRevision(build.Revision) && IsSha256(build.AppSha256) &&
            (build.Revision.Equals(startingRevision, StringComparison.OrdinalIgnoreCase) == (build.Amendments.Length == 0)) &&
            File.ReadAllText(build.AppPath + ".git-head").Trim().Equals(build.Revision, StringComparison.OrdinalIgnoreCase) &&
            Convert.ToHexString(SHA256.HashData(bytes)).Equals(build.AppSha256, StringComparison.OrdinalIgnoreCase),
            "Replay build revision or executable identity does not match.");
    }

    private static bool WorkloadsMatch(Workload[]? rows, Dictionary<string, int> counts)
    {
        if (rows is null || rows.Length != counts.Count) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is not { Names: not null }) return false;
            var key = WorkloadKey(row.Project, row.Names);
            if (!seen.Add(key) || !counts.TryGetValue(key, out var count) || count != row.Count) return false;
        }
        return true;
    }

    private static bool IsProducer(Producer? producer) =>
        producer is not null && producer.SessionId != Guid.Empty && IsRevision(producer.Revision) &&
        IsSha256(producer.AppSha256) && producer.ProcessId > 0 && !string.IsNullOrWhiteSpace(producer.AuthorityDirectory);

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static T ReadLine<T>(string? line) where T : class =>
        (line is null ? null : JsonSerializer.Deserialize<T>(line)) ??
        throw new InvalidDataException($"Missing corpus {typeof(T).Name}.");

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    internal sealed record ReplayCase(Guid SessionId, Invocation Invocation, AcceptanceTestClassDescriptor[] Inventory);

    internal sealed record ReplayBuild(string StartingRevision, string Revision, string AppPath, string AppSha256,
        string[] Amendments);

    internal sealed record ReplayDecision(string? UnresolvedName, FindingEvidenceSelection[] Selections, Provenance[] Provenance);

    internal sealed record Comparison(Guid SessionId, int Ordinal, ReplayDecision Oracle, ReplayDecision Candidate, bool Equivalent);

    internal sealed record ReplayReceipt(string CorpusSha256, Guid WindowId, Producer[] Producers, ReplayBuild Candidate,
        ReplayBuild Oracle, string OracleBlob, Comparison[] Comparisons);
}
