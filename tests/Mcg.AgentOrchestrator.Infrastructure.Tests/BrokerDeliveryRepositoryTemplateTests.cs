using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using static BrokerDeliveryRepositoryTemplate;

// Parallel-safe: each test owns its cache and case roots. Git launch observation is thread-local;
// concurrency tests use separate repositories and explicit event gates, never global observers.
public sealed class BrokerDeliveryRepositoryTemplateTests
{
    [Fact]
    public void AllLayouts_MatchFreshRepositoriesAndWarmCopiesLaunchNoGit()
    {
        using var owner = new BrokerDeliveryRepositoryTemplate();
        using var cases = new CaseRoots();
        // One owner across ALL keys catches omitted flags even when detection precedence masks them.
        foreach (var dotnet in new[] { false, true })
        foreach (var go in new[] { false, true })
        foreach (var node in new[] { false, true })
        foreach (var marker in new string?[] { null, "pyproject.toml", "requirements.txt" })
        {
            var variant = new Variant(dotnet, go, node, marker);
            var first = cases.Next();
            var warm = cases.Next();
            var fresh = cases.Next();
            var coldLaunches = CaptureLaunches(() => owner.CopyTo(first, variant));
            Assert.Equal(4, coldLaunches.Count);
            Assert.Equal(new[] { "init", "add", "-c", "rev-parse" }, coldLaunches.Select(args => args[0]));
            Assert.Contains("commit", coldLaunches[2]);
            string? head = null;
            Assert.Empty(CaptureLaunches(() => head = owner.CopyTo(warm, variant)));
            CreateReference(fresh, variant);

            foreach (var copy in new[] { first, warm })
            {
                Assert.Equal(Git(fresh, "rev-parse", "HEAD^{tree}"), Git(copy, "rev-parse", "HEAD^{tree}"));
                Assert.Equal(head, Git(copy, "rev-parse", "HEAD"));
                Assert.Equal("main", Git(copy, "branch", "--show-current"));
                Assert.Equal("1", Git(copy, "rev-list", "--count", "HEAD"));
                Assert.Equal("fixture", Git(copy, "log", "-1", "--format=%s"));
                Assert.Equal("Broker Tests <broker@example.invalid>", Git(copy, "log", "-1", "--format=%an <%ae>"));
                Assert.Equal("", Git(copy, "status", "--porcelain"));
                Assert.Equal(ExpectedToolchain(variant), TargetToolchainDetector.Detect(copy));
                Assert.False(File.Exists(Path.Combine(copy, ".git", "objects", "info", "alternates")));
                Assert.False(Directory.Exists(Path.Combine(copy, ".orchestrator-context")));
            }
            foreach (var root in new[] { first, warm, fresh })
                File.AppendAllText(Path.Combine(root, "fixture.txt"), "\nchanged broker fixture");
            var diff = Git(fresh, "diff", "--binary", "HEAD");
            Assert.Contains("changed broker fixture", diff, StringComparison.Ordinal);
            Assert.Equal(diff, Git(first, "diff", "--binary", "HEAD"));
            Assert.Equal(diff, Git(warm, "diff", "--binary", "HEAD"));
            Assert.Equal("", Git(warm, "diff", "--cached"));
        }
        Assert.Equal(24, Directory.GetDirectories(owner.Root).Length);
    }

    [Fact]
    public void MutationsAndCaseCleanup_LeaveOtherCopiesAndTemplatePristine()
    {
        using var owner = new BrokerDeliveryRepositoryTemplate();
        using var cases = new CaseRoots();
        var variant = new Variant(true, false, false, null);
        var changed = cases.Next();
        var other = cases.Next();
        var later = cases.Next();
        var head = owner.CopyTo(changed, variant);
        owner.CopyTo(other, variant);
        File.AppendAllText(Path.Combine(changed, "fixture.txt"), "\nindex change");
        Git(changed, "add", "fixture.txt");
        Assert.NotEmpty(Git(changed, "diff", "--cached"));
        AssertPristine(other, head);
        Git(changed, "-c", "user.name=Copy", "-c", "user.email=copy@example.invalid", "commit", "-m", "copy");
        Git(changed, "branch", "case-only");
        Git(changed, "config", "--local", "broker.case", "changed");
        File.AppendAllText(Path.Combine(changed, "fixture.txt"), "\nworking-tree change");
        Assert.NotEqual(head, Git(changed, "rev-parse", "HEAD"));
        Assert.NotEmpty(Git(changed, "diff"));
        SharedTestSupport.RemoveTempDirectory(changed);
        Assert.False(Directory.Exists(changed));
        AssertPristine(other, head);
        owner.CopyTo(later, variant);
        AssertPristine(later, head);
        AssertPristine(Assert.Single(Directory.GetDirectories(owner.Root)), head);
    }

    [Fact]
    public async Task ConcurrentFirstUse_PublishesOnlyOneCompleteRepository()
    {
        using var cases = new CaseRoots();
        using var initializing = new ManualResetEventSlim();
        using var secondAcquiring = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var initializations = 0;
        using var owner = new BrokerDeliveryRepositoryTemplate((root, variant) =>
        {
            Interlocked.Increment(ref initializations);
            initializing.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "Initialization release event was not signaled.");
            return CreateFresh(root, variant);
        });
        var variant = new Variant(true, false, true, null);
        var first = cases.Next();
        var second = cases.Next();
        var a = Task.Run(() => owner.CopyTo(first, variant));
        Task<string>? b = null;
        try
        {
            Assert.True(initializing.Wait(TimeSpan.FromSeconds(30)), "Initializer did not reach its event gate.");
            b = Task.Run(() =>
            {
                var observation = new BrokerDeliveryFixtureObservation(_ => { }, "second", openGitWindow: () =>
                {
                    secondAcquiring.Set();
                    return null;
                });
                return owner.CopyTo(second, variant, observation);
            });
            Assert.True(secondAcquiring.Wait(TimeSpan.FromSeconds(30)), "Second caller did not enter template acquisition.");
        }
        finally
        {
            release.Set();
            await Task.WhenAll(b is null ? [a] : new[] { a, b }).WaitAsync(TimeSpan.FromSeconds(60));
        }
        Assert.Equal(1, initializations);
        Assert.Equal(await a, await b!);
        AssertPristine(first, await a);
        AssertPristine(second, await a);
        Assert.Equal(Git(first, "rev-parse", "HEAD^{tree}"), Git(second, "rev-parse", "HEAD^{tree}"));
    }

    [Fact]
    public void FailedInitialization_CleansStagingPropagatesAndAllowsRetry()
    {
        using var cases = new CaseRoots();
        var attempts = 0;
        string? failedRoot = null;
        var sentinel = new IOException("initialization sentinel");
        using var owner = new BrokerDeliveryRepositoryTemplate((root, variant) =>
        {
            if (++attempts == 1)
            {
                failedRoot = root;
                Git(root, "init", "--initial-branch=main");
                throw sentinel;
            }
            return CreateFresh(root, variant);
        });
        var lines = new List<string>();
        var observation = new BrokerDeliveryFixtureObservation(lines.Add, "failed initialization");
        var destination = cases.Next();
        var variant = new Variant(false, false, false, "requirements.txt");
        Assert.Same(sentinel, Record.Exception(() => owner.CopyTo(destination, variant, observation)));
        observation.Complete();
        Assert.NotNull(failedRoot);
        Assert.False(Directory.Exists(failedRoot));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetDirectories(owner.Root));
        using var summary = JsonDocument.Parse(Assert.Single(lines)[BrokerDeliveryFixtureObservation.Prefix.Length..]);
        var preparation = summary.RootElement.GetProperty("repositoryPreparation");
        Assert.Equal("failed", preparation.GetProperty("initialization").GetProperty("status").GetString());
        Assert.Equal("not-entered", preparation.GetProperty("copy").GetProperty("status").GetString());
        var head = owner.CopyTo(destination, variant);
        Assert.Equal(2, attempts);
        AssertPristine(destination, head);
        Assert.Single(Directory.GetDirectories(owner.Root));
    }

    [Fact]
    public void Disposal_RemovesOwnedTemplatesAndKeepsCaseRepositoriesUsable()
    {
        using var cases = new CaseRoots();
        using var owner = new BrokerDeliveryRepositoryTemplate();
        var copy = cases.Next();
        var variant = new Variant(false, true, false, null);
        var head = owner.CopyTo(copy, variant);
        Assert.True(Directory.Exists(owner.Root));
        owner.Dispose();
        Assert.False(Directory.Exists(owner.Root));
        AssertPristine(copy, head);
        File.AppendAllText(Path.Combine(copy, "fixture.txt"), "\nafter owner disposal");
        Git(copy, "add", "fixture.txt");
        Assert.NotEmpty(Git(copy, "diff", "--cached"));
        Assert.Throws<ObjectDisposedException>(() => owner.CopyTo(cases.Next(), variant));
    }

    [Fact]
    public void CopyFailure_DoesNotPoisonTemplateOrDeleteUnownedFiles()
    {
        using var cases = new CaseRoots();
        using var owner = new BrokerDeliveryRepositoryTemplate();
        var obstacle = Path.Combine(cases.Root, "obstacle");
        File.WriteAllText(obstacle, "keep");
        var variant = new Variant(false, false, false, null);
        Assert.ThrowsAny<IOException>(() => owner.CopyTo(Path.Combine(obstacle, "repo"), variant));
        Assert.Equal("keep", File.ReadAllText(obstacle));
        var copy = cases.Next();
        string? head = null;
        Assert.Empty(CaptureLaunches(() => head = owner.CopyTo(copy, variant)));
        AssertPristine(copy, head!);
        Assert.Throws<IOException>(() => owner.CopyTo(copy, variant));
        AssertPristine(copy, head!);
    }

    [Fact]
    public void InvalidMarker_IsRejectedBeforeVariantAllocation()
    {
        using var cases = new CaseRoots();
        using var owner = new BrokerDeliveryRepositoryTemplate();
        var copy = cases.Next();
        Assert.Throws<ArgumentException>(() => owner.CopyTo(copy, new Variant(false, false, false, "../escape")));
        Assert.Empty(Directory.GetFileSystemEntries(owner.Root));
        Assert.False(Directory.Exists(copy));
    }

    private static void AssertPristine(string root, string head)
    {
        Assert.Equal(head, Git(root, "rev-parse", "HEAD"));
        Assert.Equal("1", Git(root, "rev-list", "--count", "HEAD"));
        Assert.Equal("main", Git(root, "for-each-ref", "--format=%(refname:short)", "refs/heads"));
        Assert.Equal("", Git(root, "status", "--porcelain"));
        Assert.Equal("", Git(root, "diff", "--cached"));
        Assert.Equal("", Git(root, ["config", "--local", "--get-regexp", "^broker\\."], allowMissing: true));
        Assert.Equal("broker fixture", File.ReadAllText(Path.Combine(root, "fixture.txt")));
    }

    private static List<string[]> CaptureLaunches(Action action)
    {
        var launches = new List<string[]>();
        var previous = GitCli.StartInfoObserver;
        GitCli.StartInfoObserver = info =>
        {
            launches.Add(info.ArgumentList.Skip(GitCli.HardeningArguments.Count).ToArray());
            previous?.Invoke(info);
        };
        try { action(); }
        finally { GitCli.StartInfoObserver = previous; }
        return launches;
    }

    private static Toolchain ExpectedToolchain(Variant variant) => variant.Go ? Toolchain.Go :
        variant.Node ? Toolchain.Node : variant.Dotnet ? Toolchain.Dotnet :
        variant.PythonMarker is not null ? Toolchain.Python : Toolchain.Unknown;

    // Independent fresh recipe preserves the pre-template fixture's observable file contract.
    private static void CreateReference(string root, Variant variant)
    {
        Directory.CreateDirectory(root);
        var files = new Dictionary<string, string> { ["fixture.txt"] = "broker fixture" };
        if (variant.Dotnet)
            files[variant.Node ? "Fixture.csproj" : "src/Fixture/Fixture.csproj"] =
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
        if (variant.Go) files["go.mod"] = "module example.invalid/broker\n\ngo 1.21\n";
        if (variant.Node) files["package.json"] =
            """{"name":"broker-fixture","private":true,"scripts":{"test":"node --test","build":"node build.js","lint":"eslint ."}}""";
        if (variant.PythonMarker == "pyproject.toml") files["pyproject.toml"] =
            "[project]\nname = \"broker-fixture\"\nversion = \"0.0.0\"\n[tool.pytest.ini_options]\ntestpaths = [\"tests\"]\n";
        if (variant.PythonMarker == "requirements.txt") files["requirements.txt"] = "pytest\n";
        if (!variant.Dotnet && !variant.Go && !variant.Node) files["README.md"] =
            "# Broker fixture\nDiscover the project-standard build and test commands from the project CI configuration.\n";
        foreach (var (path, contents) in files)
        {
            var fullPath = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, contents);
        }
        Git(root, "init", "--initial-branch=main");
        Git(root, "add", ".");
        Git(root, "-c", "user.name=Broker Tests", "-c", "user.email=broker@example.invalid", "commit", "--quiet", "-m", "fixture");
    }

    private static string Git(string root, params string[] args) => Git(root, args, false);

    private static string Git(string root, string[] args, bool allowMissing)
    {
        var result = GitCli.Run(root, args);
        Assert.True((result.Succeeded || allowMissing && result.ExitCode == 1) && !result.DrainTimedOut,
            $"git regression failed: exit={result.ExitCode}; stderr={result.Error}");
        return result.Output.Trim();
    }

    private sealed class CaseRoots : IDisposable
    {
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal string Next() => Path.Combine(Root, Guid.NewGuid().ToString("N"));
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
