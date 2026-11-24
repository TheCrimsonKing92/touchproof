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
                var coverage = HasCpuCoverage(manifest.Workloads);
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

    internal static bool Replay(string corpusPath, string expectedSha256, string receiptPath,
        ReplayBuild candidate, ReplayBuild oracle) =>
        Replay(corpusPath, expectedSha256, receiptPath, candidate, oracle, VerifyOracleSource);

    internal static bool Replay(string corpusPath, string expectedSha256, string receiptPath,
        ReplayBuild candidate, ReplayBuild oracle, Func<ReplayBuild, Stream, PreTesterResolverReplayArm, string> verifyOracleSource)
    {
        var corpus = Read(corpusPath, expectedSha256);
        using var candidateBytes = File.OpenRead(candidate.AppPath);
        using var oracleBytes = File.OpenRead(oracle.AppPath);
        VerifyBuild(candidate, CandidateRevision, candidateBytes);
        VerifyBuild(oracle, OracleRevision, oracleBytes);
        using var oracleArm = new PreTesterResolverReplayArm(oracle, oracleBytes);
        Require(HasEmbeddedRevision(oracleArm, oracle.Revision), "Replay executable revision does not match the staged revision.");
        var oracleBlob = verifyOracleSource(oracle, oracleBytes, oracleArm);
        Require(!candidate.Revision.Equals(oracle.Revision, StringComparison.OrdinalIgnoreCase) &&
            !candidate.AppSha256.Equals(oracle.AppSha256, StringComparison.OrdinalIgnoreCase),
            "Candidate and oracle identities must be distinct.");

        using var candidateArm = new PreTesterResolverReplayArm(candidate, candidateBytes);
        Require(HasEmbeddedRevision(candidateArm, candidate.Revision),
            "Replay executable revision does not match the staged revision.");
        Require(candidateArm.ModuleVersionId != oracleArm.ModuleVersionId,
            "Candidate and oracle must use distinct compiled modules.");
        // Reserve a separate destination before invoking either resolver. CreateNew also protects the corpus itself.
        using var output = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var comparisons = corpus.Cases.Select(row =>
        {
            var expected = oracleArm.Evaluate(row);
            var actual = candidateArm.Evaluate(row);
            return new Comparison(row.SessionId, row.Invocation.Ordinal, expected, actual,
                expected.UnresolvedName == row.Invocation.UnresolvedName &&
                expected.UnresolvedName == actual.UnresolvedName &&
                expected.Selections.SequenceEqual(actual.Selections) &&
                expected.Provenance.SequenceEqual(actual.Provenance) &&
                expected.Diagnostics.SequenceEqual(actual.Diagnostics, StringComparer.Ordinal));
        }).ToArray();
        JsonSerializer.Serialize(output, new ReplayReceipt(corpus.Sha256, corpus.Header.WindowId,
            corpus.Producers, candidate, oracle, oracleBlob, comparisons));
        output.Flush(flushToDisk: true);
        return comparisons.All(comparison => comparison.Equivalent);
    }

    private static void VerifyBuild(ReplayBuild build, string startingRevision, Stream bytes)
    {
        var amended = !string.Equals(build.Revision, startingRevision, StringComparison.OrdinalIgnoreCase);
        Require(string.Equals(build.StartingRevision, startingRevision, StringComparison.OrdinalIgnoreCase) &&
            IsRevision(build.Revision) && IsSha256(build.AppSha256) &&
            build.Amendments is not null && amended == (build.Amendments.Length != 0) &&
            build.Amendments.All(amendment => !string.IsNullOrWhiteSpace(amendment)) &&
            File.ReadAllText(build.AppPath + ".git-head").Trim().Equals(build.Revision, StringComparison.OrdinalIgnoreCase) &&
            Convert.ToHexString(SHA256.HashData(bytes)).Equals(build.AppSha256, StringComparison.OrdinalIgnoreCase),
            "Replay build revision or executable identity does not match.");
        if (amended)
        {
            Require(build.RepositoryPath is not null && Path.IsPathFullyQualified(build.RepositoryPath) &&
                Directory.Exists(build.RepositoryPath) &&
                GitCli.Run(build.RepositoryPath, "merge-base", "--is-ancestor", startingRevision, build.Revision).ExitCode == 0,
                "Replay build ancestry could not be verified in the supplied repository.");
        }
        bytes.Position = 0;
    }

    private static bool HasEmbeddedRevision(PreTesterResolverReplayArm arm, string revision) =>
        arm.InformationalVersion is { } version && version.IndexOf('+') is var metadata && metadata >= 0 &&
        version[(metadata + 1)..].Split('.')[^1].Equals(revision, StringComparison.OrdinalIgnoreCase);

    private static string VerifyOracleSource(ReplayBuild oracle, Stream appBytes, PreTesterResolverReplayArm arm)
    {
        const string resolverPath = "src/Mcg.AgentOrchestrator.App/Orchestration/PreTesterReverseDependencySelections.cs";
        Require(oracle.RepositoryPath is not null && Path.IsPathFullyQualified(oracle.RepositoryPath) &&
            Directory.Exists(oracle.RepositoryPath), "Oracle replay requires the repository containing its staged revision.");
        var source = GitCli.RunForRepository(oracle.RepositoryPath, "rev-parse", "--verify", $"{oracle.Revision}:{resolverPath}");
        var blob = source.Output.Trim();
        // The observer port extracts the original loop for replay. Its approved source has a different blob.
        var checksum = blob switch
        {
            OracleResolverBlob => "d7e222d4e61e3ac7a488bdb0cc99780a57f41fa134b2b1d6b7047ff93d43f162",
            "23f9044420b64de6750682f80404e8d51c618779" => "928cbd382aeb8386b723f73bc995186a3c989ed90d2801bc5d09e05c4d1a2b0f",
            _ => null
        };
        Require(source.Succeeded && checksum is not null, "Oracle revision does not contain an approved original resolver.");
        Require(arm.HasEmbeddedResolverSource(appBytes, resolverPath, checksum),
            "Oracle executable does not bind both replay operations to the approved resolver source.");
        return blob;
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
        string[] Amendments, string? RepositoryPath = null, Dictionary<string, string>? DependencySha256 = null);

    internal sealed record ReplayDecision(string? UnresolvedName, FindingEvidenceSelection[] Selections,
        Provenance[] Provenance, string[] Diagnostics);

    internal sealed record Comparison(Guid SessionId, int Ordinal, ReplayDecision Oracle, ReplayDecision Candidate, bool Equivalent);

    internal sealed record ReplayReceipt(string CorpusSha256, Guid WindowId, Producer[] Producers, ReplayBuild Candidate,
        ReplayBuild Oracle, string OracleBlob, Comparison[] Comparisons);
}
