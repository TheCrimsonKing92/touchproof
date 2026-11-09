using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = WorkerBuildProjectScopeTests.Fixture;

// Parallel-safe: each case owns a unique repository and telemetry capture is AsyncLocal.
public sealed class WorkerBuildRepositoryFactsTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Theory]
    [Xunit.InlineData("clean")]
    [Xunit.InlineData("staged-only")]
    [Xunit.InlineData("unstaged-only")]
    [Xunit.InlineData("cancellation")]
    [Xunit.InlineData("untracked-nested")]
    [Xunit.InlineData("ignored")]
    [Xunit.InlineData("removed")]
    [Xunit.InlineData("rename")]
    [Xunit.InlineData("spaces-and-unicode")]
    [Xunit.InlineData("show-stash")]
    public void ChangedPaths_EqualLegacyFourCommandUnion(string state)
    {
        using var fixture = new Fixture();
        switch (state)
        {
            case "clean": break;
            case "staged-only":
                fixture.Change("Core");
                fixture.Git("add", "Core");
                break;
            case "unstaged-only": fixture.Change("Core"); break;
            case "cancellation":
                fixture.Change("Core");
                fixture.Git("add", "Core");
                fixture.Write("Core/Core.cs", fixture.Git("show", "HEAD:Core/Core.cs"));
                break;
            case "untracked-nested": fixture.Write("new/subfolder/source.cs", "new"); break;
            case "ignored": fixture.Write("Core/obj/ignored.cs", "ignored"); break;
            case "removed": File.Delete(Path.Combine(fixture.Root, "Core/Core.cs")); break;
            case "rename": fixture.Git("mv", "Core/Core.cs", "Core/Renamed.cs"); break;
            case "spaces-and-unicode":
                fixture.Write("Core/with space.cs", "initial");
                fixture.Write("Core/caf\u00e9.cs", "initial");
                fixture.Commit();
                fixture.Write("Core/with space.cs", "changed");
                fixture.Write("Core/caf\u00e9.cs", "changed");
                fixture.Git("add", "Core");
                fixture.Write("Core/new \u65e5\u672c.cs", "untracked");
                break;
            case "show-stash":
                fixture.Git("config", "--local", "status.showStash", "true");
                fixture.Change("Core");
                fixture.Git("-c", "user.name=Scope Fixture", "-c", "user.email=scope@example.invalid",
                    "stash", "push", "--message", "fixture-local stash");
                fixture.Change("App");
                Assert.Contains("# stash 1", fixture.Git("status", "--porcelain=v2", "-z",
                    "--no-renames", "--untracked-files=all"), StringComparison.Ordinal);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(state));
        }

        var baseline = fixture.Metadata().GoalBaseline;
        var expected = LegacyPaths(fixture, baseline).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var actual = WorkerBuildRepositoryFacts.ReadChangedPaths(fixture.Root, baseline)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        Assert.Equal(expected, actual);
        if (state is "clean" or "ignored") Assert.Empty(actual);
        else Assert.NotEmpty(actual);
        if (state == "cancellation") Assert.Equal(["Core/Core.cs"], actual);
        if (state == "rename") Assert.Equal(["Core/Core.cs", "Core/Renamed.cs"], actual);
    }

    [Xunit.Theory]
    [Xunit.InlineData("initial")]
    [Xunit.InlineData("distinct-dispatch-base")]
    [Xunit.InlineData("mixed-dirty")]
    [Xunit.InlineData("additional-paths")]
    public void Resolve_MatchesLegacyResultForGoalWorktreeWithMetadata(string state)
    {
        using var fixture = new Fixture();
        if (state == "distinct-dispatch-base") AdvanceDispatch(fixture);
        if (state == "mixed-dirty")
        {
            fixture.Change("Core");
            fixture.Git("add", "Core");
            fixture.Change("App");
            fixture.Write("Execution/New.cs", "public class New { }");
        }
        string[] additional = state == "additional-paths" ? ["Core/Core.cs"] : [];
        var expected = LegacyResolve(fixture, ["Tests/Tests.csproj"], additional);
        var actual = WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"], additional);
        Assert.Equal(expected.RequestedProjects, actual.RequestedProjects);
        Assert.Equal(expected.RequiredProjects, actual.RequiredProjects);
        Assert.Equal(expected.Projects, actual.Projects);
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.Equal(expected.MetadataDigest, actual.MetadataDigest);
        Assert.Equal(expected.StableMetadataDigest, actual.StableMetadataDigest);
        Assert.NotEqual("none", actual.MetadataDigest);
        Assert.NotEmpty(actual.Projects);
    }

    [Xunit.Fact]
    public void Resolve_UnresolvedSourcesFailWithLegacyMessageOrder()
    {
        using var fixture = new Fixture();
        fixture.Write("a.cs", "baseline");
        fixture.Write("b.cs", "baseline");
        fixture.Commit();
        var baseline = fixture.Git("rev-parse", "HEAD").Trim();
        fixture.SetMetadata(fixture.Metadata() with { GoalBaseline = baseline, DispatchBase = baseline });
        fixture.Write("a.cs", "committed");
        fixture.Write("b.cs", "committed");
        fixture.Commit();
        fixture.Write("a.cs", "baseline");
        fixture.Git("add", "a.cs");
        fixture.Write("b.cs", "baseline");

        var expected = Assert.Throws<IOException>(() => LegacyResolve(fixture, ["Tests/Tests.csproj"], []));
        var actual = Assert.Throws<IOException>(() => WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]));
        Assert.Equal("unresolved compiled source: b.cs; unresolved compiled source: a.cs", expected.Message);
        Assert.Equal(expected.Message, actual.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void Identity_EqualsLegacySingleReads(bool shadowBranch, bool includeTopLevel)
    {
        using var fixture = new Fixture();
        if (shadowBranch) fixture.Git("tag", "goal/" + fixture.Prefix);
        var expectedBranch = fixture.Git("symbolic-ref", "--short", "HEAD").Trim();
        var expectedHead = fixture.Git("rev-parse", "HEAD").Trim();
        var expectedCommon = fixture.Git("rev-parse", "--path-format=absolute", "--git-common-dir").Trim();
        var expectedTopLevel = includeTopLevel ? fixture.Git("rev-parse", "--show-toplevel").Trim() : null;
        var actual = WorkerBuildRepositoryFacts.ReadIdentity(fixture.Root, includeTopLevel);
        Assert.Equal(expectedBranch, actual.RequireBranch());
        Assert.Equal(expectedHead, actual.Head);
        Assert.Equal(expectedCommon, actual.CommonDirectory);
        Assert.Equal(expectedTopLevel, actual.TopLevel);
    }

    [Xunit.Theory]
    [Xunit.InlineData("resolve")]
    [Xunit.InlineData("read-dispatch-identity")]
    [Xunit.InlineData("prepare")]
    public void DetachedHead_FailsWithLegacyExceptionTypeNamingDetachedState(string entryPoint)
    {
        using var fixture = new Fixture();
        fixture.Git("checkout", "--detach", "HEAD");
        var legacy = GitCli.RunForRepository(fixture.Root, "symbolic-ref", "--short", "HEAD");
        Assert.False(legacy.Succeeded);
        Assert.False(legacy.DrainTimedOut);
        var expected = Assert.Throws<IOException>(() => LegacyGit(fixture.Root, "symbolic-ref", "--short", "HEAD"));
        var actual = Assert.Throws<IOException>(() =>
        {
            switch (entryPoint)
            {
                case "resolve": WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]); break;
                case "read-dispatch-identity": WorkerBuildProjectScope.ReadDispatchIdentity(fixture.Root); break;
                case "prepare": WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "next", "landing"); break;
                default: throw new ArgumentOutOfRangeException(nameof(entryPoint));
            }
        });
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Contains("detached", actual.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Theory(Skip = "Requires Windows launch telemetry.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData("initial")]
    [Xunit.InlineData("distinct-dispatch-base")]
    [Xunit.InlineData("distinct-head")]
    public void Resolve_LaunchesThreeGitProcessesPlusDistinctAncestryChecks(string state)
    {
        using var fixture = new Fixture();
        if (state != "initial") AdvanceDispatch(fixture);
        if (state == "distinct-head")
        {
            fixture.Change("App");
            fixture.Commit();
        }
        var metadata = fixture.Metadata();
        var head = fixture.Git("rev-parse", "HEAD").Trim();
        var pairs = new[] { metadata.GoalBaseline + ":" + head, metadata.DispatchBase + ":" + head,
            metadata.GoalBaseline + ":" + metadata.DispatchBase };
        var distinct = pairs.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(state == "initial" ? 1 : state == "distinct-dispatch-base" ? 2 : 3, distinct);

        using var capture = LaunchLockTelemetry.BeginCapture();
        var result = WorkerBuildProjectScope.Resolve(fixture.Root, ["Tests/Tests.csproj"]);
        var launches = capture.Snapshot().ByEntry[(int)LaunchLockEntryPoint.GitCli];
        Assert.NotEmpty(result.Projects);
        Assert.Equal(3L + distinct, launches);
    }

    private static void AdvanceDispatch(Fixture fixture)
    {
        fixture.Change("Core");
        fixture.Commit();
        WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "next", "landing");
    }

    private static IEnumerable<string> LegacyPaths(Fixture fixture, string baseline) =>
        fixture.Git("diff", "--name-only", "-z", "--no-renames", baseline, "--").Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Concat(fixture.Git("diff", "--name-only", "-z", "--no-renames", "--").Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Concat(fixture.Git("diff", "--cached", "--name-only", "-z", "--no-renames", "--").Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Concat(fixture.Git("ls-files", "--others", "--exclude-standard", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries));

    // Independent pre-change command oracle for a goal worktree with metadata.
    private static WorkerBuildProjectScope.Result LegacyResolve(Fixture fixture, string[] requestedProjects, string[] additional)
    {
        var root = Canonical(fixture.Root);
        Assert.Equal(root, Canonical(fixture.Git("rev-parse", "--show-toplevel").Trim()),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var branch = fixture.Git("symbolic-ref", "--short", "HEAD").Trim();
        var head = fixture.Git("rev-parse", "HEAD").Trim();
        var bytes = File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(root));
        var metadataDigest = Hash(bytes);
        var metadata = JsonSerializer.Deserialize<WorkerBuildProjectScope.Metadata>(bytes)!;
        var common = Canonical(fixture.Git("rev-parse", "--path-format=absolute", "--git-common-dir").Trim());
        Assert.Equal(root, Canonical(metadata.WorktreeRoot));
        Assert.Equal(common, Canonical(metadata.GitCommonDirectory));
        Assert.Equal(branch, metadata.Branch);
        foreach (var revision in new[] { metadata.GoalBaseline, metadata.DispatchBase })
        {
            Assert.True(revision.Length is 40 or 64 && revision.All(Uri.IsHexDigit));
            fixture.Git("merge-base", "--is-ancestor", revision, head);
        }
        fixture.Git("merge-base", "--is-ancestor", metadata.GoalBaseline, metadata.DispatchBase);
        var stableMetadataDigest = WorkerBuildScopeStableDigest.Compute(metadata);
        var baseline = metadata.GoalBaseline;
        var required = WorkerBuildEvidenceRequirement.ResolveRequiredProjects(root, LegacyPaths(fixture, baseline).Concat(additional));
        if (required.Errors.Count != 0) throw new IOException(string.Join("; ", required.Errors));
        var requested = requestedProjects.Select(project =>
        {
            var path = WorkerBuildProjectScope.ValidatePath(root, project, mustExist: true);
            Assert.Contains(Path.GetExtension(path), new[] { ".csproj", ".fsproj", ".vbproj" });
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var projects = requested.Concat(required.Projects).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var identity = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { root, branch, head, baseline, metadataDigest, requested, required = required.Projects, projects })));
        return new(requested, required.Projects, projects, identity, metadataDigest, stableMetadataDigest);
    }

    private static string LegacyGit(string root, params string[] arguments)
    {
        var result = GitCli.RunForRepository(root, arguments);
        if (!result.Succeeded || result.DrainTimedOut)
            throw new IOException($"Worker build scope Git {arguments[0]} failed (exit={result.ExitCode}): {result.Error.Trim()}");
        return result.Output;
    }

    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
