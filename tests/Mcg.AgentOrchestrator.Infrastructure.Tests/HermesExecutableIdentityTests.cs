using System.Text.Json;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class HermesExecutableIdentityTests
{
    [Xunit.Fact]
    public async Task NativeOutputIsAcceptedFromOsBoundCleanAnnotatedCheckout()
    {
        using var fixture = new HermesIdentityTestFixture();

        var receipt = await fixture.Verifier.VerifyAsync(
            fixture.ExecutablePath,
            fixture.NativeVersionOutput,
            string.Empty,
            0,
            versionJobExitConfirmed: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(fixture.Root), receipt.InstallRoot);
        Assert.Equal(fixture.ExecutablePath, receipt.ImagePath);
        Assert.Equal(fixture.Pin.Commit, receipt.HeadCommit);
        Assert.Equal(fixture.Pin.TagObject, receipt.TagObject);
        Assert.Equal(fixture.Pin.Commit, receipt.PeeledCommit);
        Assert.True(receipt.WorkingTreeClean);
    }

    [Xunit.Fact]
    public async Task WrongCommitRefuses()
    {
        using var fixture = new HermesIdentityTestFixture();
        var verifier = new GitHermesExecutableIdentityVerifier(fixture.Pin with { Commit = new string('0', 40) });

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => verifier.VerifyAsync(
            fixture.ExecutablePath, fixture.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.WrongCommit, error.Reason);
    }

    [Xunit.Fact]
    public async Task LightweightTagRefusesTagObjectOnlyIdentity()
    {
        using var fixture = new HermesIdentityTestFixture(annotatedTag: false);

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => fixture.Verifier.VerifyAsync(
            fixture.ExecutablePath, fixture.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.TagNotAnnotated, error.Reason);
    }

    [Xunit.Fact]
    public async Task ExecutableAndReportedInstallationMismatchRefusesMisleadingText()
    {
        using var launched = new HermesIdentityTestFixture();
        using var reported = new HermesIdentityTestFixture();

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => launched.Verifier.VerifyAsync(
            launched.ExecutablePath, reported.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.InstallDirectoryTextMismatch, error.Reason);
    }

    [Xunit.Fact]
    public async Task ModifiedTrackedSourceRefuses()
    {
        using var fixture = new HermesIdentityTestFixture();
        File.AppendAllText(fixture.TrackedSourcePath, "modified");

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => fixture.Verifier.VerifyAsync(
            fixture.ExecutablePath, fixture.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.WorkingTreeModified, error.Reason);
    }

    [Xunit.Fact]
    public async Task ImageOutsideGitCheckoutRefusesUnverifiableSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-hermes-no-git", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var image = Path.Combine(root, "hermes.exe");
        File.WriteAllText(image, "fixture");
        try
        {
            var error = await Assert.ThrowsAsync<HermesIdentityException>(() =>
                new GitHermesExecutableIdentityVerifier().VerifyAsync(
                    image, NativeOutput(root), string.Empty, 0, true, TestContext.Current.CancellationToken));

            Assert.Equal(HermesIdentityRefusal.NotGitCheckout, error.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task MissingExecutableRefusesBeforeReadingVersionText()
    {
        using var fixture = new HermesIdentityTestFixture();

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => fixture.Verifier.VerifyAsync(
            Path.Combine(fixture.Root, "missing.exe"), fixture.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.ImagePathMissingOnDisk, error.Reason);
    }

    [Xunit.Fact]
    public async Task WrongTagObjectRefusesEvenWhenHeadAndPeelMatch()
    {
        using var fixture = new HermesIdentityTestFixture();
        var verifier = new GitHermesExecutableIdentityVerifier(fixture.Pin with { TagObject = new string('0', 40) });

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => verifier.VerifyAsync(
            fixture.ExecutablePath, fixture.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.TagObjectMismatch, error.Reason);
    }

    [Xunit.Fact]
    public void StaleReceiptCannotBeReused()
    {
        using var fixture = new HermesIdentityTestFixture();
        var now = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        var receipt = fixture.Receipt(now - TimeSpan.FromMinutes(2));

        var error = Assert.Throws<HermesIdentityException>(() =>
            GitHermesExecutableIdentityVerifier.ValidateReceipt(receipt, now, fixture.Pin));

        Assert.Equal(HermesIdentityRefusal.ReceiptStale, error.Reason);
    }

    [Xunit.Fact]
    public void ValidateReceiptContextVariantsPreserveEveryAcceptanceAndRefusal()
    {
        using var fixture = new HermesIdentityTestFixture();
        var now = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        var valid = fixture.Receipt(now); // The original 15-argument constructor leaves context null.
        Assert.Null(valid.LaunchContext);
        (string Name, HermesExecutableIdentityReceipt? Receipt, HermesIdentityRefusal? Reason)[] cases =
        [
            ("fresh", valid, null),
            ("maximum age", valid with { VerifiedAtUtc = now - TimeSpan.FromMinutes(1) }, null),
            ("missing", null, HermesIdentityRefusal.ReceiptMissing),
            ("stale", valid with { VerifiedAtUtc = now - TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1) }, HermesIdentityRefusal.ReceiptStale),
            ("future", valid with { VerifiedAtUtc = now + TimeSpan.FromTicks(1) }, HermesIdentityRefusal.ReceiptStale),
            ("release", valid with { Release = "v1900.1.1" }, HermesIdentityRefusal.ReceiptPinMismatch),
            ("head", valid with { HeadCommit = new string('0', 40) }, HermesIdentityRefusal.ReceiptPinMismatch),
            ("tag", valid with { TagObject = new string('0', 40) }, HermesIdentityRefusal.ReceiptPinMismatch),
            ("peel", valid with { PeeledCommit = new string('0', 40) }, HermesIdentityRefusal.ReceiptPinMismatch),
            ("dirty", valid with { WorkingTreeClean = false }, HermesIdentityRefusal.ReceiptEvidenceInvalid),
            ("exit", valid with { VersionExitCode = 1 }, HermesIdentityRefusal.ReceiptEvidenceInvalid),
            ("job", valid with { VersionJobExitConfirmed = false }, HermesIdentityRefusal.ReceiptEvidenceInvalid),
            ("install method", valid with { InstallMethod = "pip" }, HermesIdentityRefusal.ReceiptEvidenceInvalid),
            ("install root mismatch", valid with { ReportedInstallDirectory = Path.Combine(fixture.Root, "other") }, HermesIdentityRefusal.ReceiptEvidenceInvalid),
            ("empty image", valid with { ImagePath = " " }, HermesIdentityRefusal.ReceiptEvidenceInvalid),
            ("empty root", valid with { InstallRoot = " " }, HermesIdentityRefusal.ReceiptEvidenceInvalid),
            ("empty reported root", valid with { ReportedInstallDirectory = " " }, HermesIdentityRefusal.ReceiptEvidenceInvalid),
            ("malformed path", valid with { InstallRoot = "\0" }, HermesIdentityRefusal.ReceiptEvidenceInvalid)
        ];
        HermesLaunchContext?[] contexts =
        [
            null,
            new(HermesIntegrityLevel.FromRid(0x1000), true),
            new(HermesIntegrityLevel.FromRid(0x1234), false),
            new(HermesIntegrityLevel.Unavailable("fixture query refused"), false)
        ];

        foreach (var testCase in cases)
        {
            var baseline = Outcome(testCase.Receipt);
            Assert.True(testCase.Reason == baseline.Reason, $"Unexpected baseline for {testCase.Name}: {baseline}");
            foreach (var context in contexts)
            {
                var variant = testCase.Receipt is null ? null : testCase.Receipt with { LaunchContext = context };
                Assert.Equal(baseline, Outcome(variant));
            }
        }

        (HermesIdentityRefusal? Reason, string? Message) Outcome(HermesExecutableIdentityReceipt? receipt)
        {
            var exception = Record.Exception(() =>
                GitHermesExecutableIdentityVerifier.ValidateReceipt(receipt, now, fixture.Pin));
            if (exception is null) return (null, null);
            var refusal = Assert.IsType<HermesIdentityException>(exception);
            return (refusal.Reason, refusal.Message);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(0x0000, "Untrusted")]
    [Xunit.InlineData(0x1000, "Low")]
    [Xunit.InlineData(0x2000, "Medium")]
    [Xunit.InlineData(0x2100, "MediumPlus")]
    [Xunit.InlineData(0x3000, "High")]
    [Xunit.InlineData(0x4000, "System")]
    [Xunit.InlineData(0x5000, "Protected")]
    [Xunit.InlineData(0x1234, "Other")]
    [Xunit.InlineData(-1, "Other")]
    public void IntegrityRidPreservesRawValueAndDistinguishesUnknown(int rid, string expectedKind)
    {
        var integrity = HermesIntegrityLevel.FromRid(unchecked((uint)rid));

        Assert.Equal(expectedKind, integrity.Kind.ToString());
        Assert.Equal(unchecked((uint)rid), integrity.Rid);
        Assert.Null(integrity.UnavailableReason);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" ")]
    [Xunit.InlineData("OpenProcessToken access refused")]
    public void UnavailableIntegrityAlwaysCarriesReasonWithoutInventingRid(string? reason)
    {
        var integrity = HermesIntegrityLevel.Unavailable(reason);

        Assert.Equal(HermesIntegrityLevelKind.Unavailable, integrity.Kind);
        Assert.Null(integrity.Rid);
        Assert.False(string.IsNullOrWhiteSpace(integrity.UnavailableReason));
        if (!string.IsNullOrWhiteSpace(reason)) Assert.Equal(reason, integrity.UnavailableReason);
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("invalid")]
    [Xunit.InlineData("closed")]
    public void IntegrityReaderUnusableHandleReturnsUnavailable(string handleCase)
    {
        using var handle = new Microsoft.Win32.SafeHandles.SafeProcessHandle(
            handleCase == "closed" ? new IntPtr(1) : IntPtr.Zero, ownsHandle: false);
        if (handleCase == "closed") handle.Dispose();

        var integrity = HermesLaunchContextReader.ReadIntegrityLevel(handleCase == "missing" ? null : handle);

        Assert.Equal(HermesIntegrityLevelKind.Unavailable, integrity.Kind);
        Assert.Null(integrity.Rid);
        Assert.False(string.IsNullOrWhiteSpace(integrity.UnavailableReason));
    }

    [Xunit.Fact]
    public void LegacyReceiptWithoutLaunchContextStillDeserializes()
    {
        using var fixture = new HermesIdentityTestFixture();
        var receipt = fixture.Receipt(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var legacyJson = JsonSerializer.Serialize(receipt, options);
        using var document = JsonDocument.Parse(legacyJson);
        Assert.False(document.RootElement.TryGetProperty("launchContext", out _));

        var restored = JsonSerializer.Deserialize<HermesExecutableIdentityReceipt>(legacyJson, options);

        Assert.NotNull(restored);
        Assert.Null(restored.LaunchContext);
        Assert.Equal(receipt, restored);
    }

    [Xunit.Fact]
    public void DefaultPinMatchesCheckedTrialPolicy()
    {
        var repositoryRoot = RepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "config", "trials", "hermes-acp-v2026.8.27.json")));
        var pin = document.RootElement.GetProperty("pin");

        Assert.Equal(pin.GetProperty("release").GetString(), HermesPinnedIdentity.Default.Release);
        Assert.Equal(pin.GetProperty("tagObject").GetString(), HermesPinnedIdentity.Default.TagObject);
        Assert.Equal(pin.GetProperty("commit").GetString(), HermesPinnedIdentity.Default.Commit);
    }

    [Xunit.Fact]
    public void FixtureGitUsesSharedBudgetAndDrainTimeoutNamesCommand()
    {
        Assert.Equal(GitCli.DefaultTimeoutMilliseconds, HermesIdentityTestFixture.GitBudgetMilliseconds);
        Assert.True(HermesIdentityTestFixture.GitBudgetMilliseconds >= 60_000);

        string? root = null;
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                new HermesIdentityTestFixture(runGit: (workingDirectory, _) =>
                {
                    root = workingDirectory;
                    return new GitCli.GitResult(
                        ExitCode: 0,
                        Output: string.Empty,
                        Error: "output drain timed out",
                        DrainTimedOut: true);
                }));

            Assert.Contains("git init", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (root is not null)
                _ = TempRootJanitor.DeleteTreeWithRetry(root);
        }
    }

    [Xunit.Fact]
    public void FixtureCleanupRetriesLockedRootAndReturnsJanitorReceipt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fixture = new HermesIdentityTestFixture();
        var lockedPath = Path.Combine(fixture.Root, "cleanup-lock.txt");
        File.WriteAllText(lockedPath, "locked");

        using (File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = Record.Exception(fixture.Dispose);

            Assert.Null(error);
            var receipt = Assert.IsType<TempRootJanitorDeleteResult>(fixture.CleanupReceipt);
            Assert.Equal(Path.GetFullPath(fixture.Root), Path.GetFullPath(receipt.Path));
            Assert.True(receipt.DeleteAttempts >= 2, $"Cleanup attempted {receipt.DeleteAttempts} time(s).");
            Assert.True(
                !Directory.Exists(fixture.Root) || receipt.Status == TempRootJanitorDeleteStatus.Failed,
                $"Cleanup retained '{receipt.Path}' without a failed janitor receipt.");
        }

        fixture.Dispose();
        Assert.False(Directory.Exists(fixture.Root), $"Cleanup retained '{fixture.Root}' after the lock closed.");
    }

    private static string NativeOutput(string installRoot) =>
        $"Hermes Agent v0.20.6 (2026.8.27){Environment.NewLine}" +
        $"Install directory: {installRoot}{Environment.NewLine}" +
        "Install method: git";

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot)
            ? verifiedRoot
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
}

internal sealed class HermesIdentityTestFixture : IDisposable
{
    private const string Release = "v2026.8.27";
    private readonly Func<string, string[], GitCli.GitResult> runGit;

    public HermesIdentityTestFixture(
        bool annotatedTag = true,
        Func<string, string[], GitCli.GitResult>? runGit = null)
    {
        this.runGit = runGit ?? RunGitWithSharedBudget;
        Root = Path.Combine(Path.GetTempPath(), "mcg-hermes-identity", Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(Root, ".venv", "Scripts");
        Directory.CreateDirectory(scripts);
        ExecutablePath = Path.Combine(scripts, "hermes.exe");
        TrackedSourcePath = Path.Combine(Root, "hermes-source.py");
        File.WriteAllText(ExecutablePath, "fixture executable");
        File.WriteAllText(TrackedSourcePath, "fixture source");
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Hermes identity tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "fixture candidate");
        RunGit("tag", annotatedTag ? "-a" : "", Release, annotatedTag ? "-m" : "", annotatedTag ? "fixture release" : "");

        var commit = Git("rev-parse", "HEAD");
        var tagObject = Git("rev-parse", $"refs/tags/{Release}");
        Pin = new HermesPinnedIdentity(Release, tagObject, commit);
        Verifier = new GitHermesExecutableIdentityVerifier(Pin);
        NativeVersionOutput =
            $"Hermes Agent v0.20.6 (2026.8.27){Environment.NewLine}" +
            $"Install directory: {Root}{Environment.NewLine}" +
            $"Install method: git{Environment.NewLine}" +
            "Python: 3.12.14";
    }

    public string Root { get; }
    public string ExecutablePath { get; }
    public string TrackedSourcePath { get; }
    public HermesPinnedIdentity Pin { get; }
    public GitHermesExecutableIdentityVerifier Verifier { get; }
    public string NativeVersionOutput { get; }
    internal static int GitBudgetMilliseconds => GitCli.DefaultTimeoutMilliseconds;
    internal TempRootJanitorDeleteResult? CleanupReceipt { get; private set; }

    public HermesExecutableIdentityReceipt Receipt(DateTimeOffset verifiedAtUtc) => new(
        ExecutablePath, Root, Root, "git", Pin.Release, Pin.Commit, Pin.TagObject, Pin.Commit,
        "Hermes Agent v0.20.6 (2026.8.27)", true, verifiedAtUtc, NativeVersionOutput, string.Empty, 0, true);

    public void Dispose()
    {
        CleanupReceipt = TempRootJanitor.DeleteTreeWithRetry(Root);
    }

    private string Git(params string[] arguments)
    {
        var filteredArguments = arguments.Where(argument => !string.IsNullOrEmpty(argument)).ToArray();
        var result = runGit(Root, filteredArguments);
        if (!result.Succeeded || result.DrainTimedOut)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}");
        return result.Output.Trim();
    }

    private void RunGit(params string[] arguments) => _ = Git(arguments);

    private static GitCli.GitResult RunGitWithSharedBudget(string workingDirectory, string[] arguments) =>
        GitCli.Run(workingDirectory, GitBudgetMilliseconds, arguments);
}
