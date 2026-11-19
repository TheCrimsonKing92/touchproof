using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: reads the verified repository document and varies the registry in memory.
public sealed class OwnerConsoleSurfaceGuardTests
{
    [Fact]
    public void CommandSurfaceMatchesTheNewestChangelogFingerprint() =>
        AssertMatches(OwnerConsoleBindings.All, ReadChangelog());

    [Fact]
    public void ExtraValidBindingReportsADriftFailureAgainstTheSameChangelog()
    {
        var entries = ReadChangelog();
        var original = OwnerConsoleBindings.All;
        AssertMatches(original, entries);
        OwnerConsoleBinding extra = new("surface-probe", "surface-probe", null, OwnerConsoleScope.Global,
            OwnerConsoleEffect.View, OwnerConsoleCommandOwner.Controller, false,
            "surface-probe", null, null, "Open the surface probe.");
        OwnerConsoleBinding[] changed = [.. original, extra];
        Assert.Empty(OwnerConsoleBindings.Validate(changed));

        var mismatch = OwnerConsoleSurfaceFingerprint.FindMismatch(changed, entries);
        Assert.NotNull(mismatch);
        Assert.Contains("Append an entry to docs/owner-console-changelog.md and update its surface fingerprint.", mismatch);
    }

    private static void AssertMatches(IReadOnlyList<OwnerConsoleBinding> rows,
        IReadOnlyList<OwnerConsoleChangelogEntry>? entries)
    {
        var mismatch = OwnerConsoleSurfaceFingerprint.FindMismatch(rows, entries);
        if (mismatch is not null) Assert.Fail(mismatch);
    }

    private static IReadOnlyList<OwnerConsoleChangelogEntry>? ReadChangelog()
    {
        var path = Path.Combine(VerifiedRepositoryRoot.Find(), "docs", OwnerConsoleChangelog.FileName);
        return File.Exists(path) ? OwnerConsoleChangelog.Parse(File.ReadAllText(path)) : null;
    }
}
