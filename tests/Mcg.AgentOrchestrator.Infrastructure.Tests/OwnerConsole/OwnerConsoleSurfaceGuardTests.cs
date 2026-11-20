using Mcg.AgentOrchestrator.App.OwnerConsole;
using TextKind = Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleSurfaceText.TextKind;

// Parallel-safe: reads the verified repository document and varies the surface in memory.
public sealed class OwnerConsoleSurfaceGuardTests
{
    private const string AppendEntry = "Append an entry to docs/owner-console-changelog.md and update its surface fingerprint.";

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

        var mismatch = OwnerConsoleSurfaceFingerprint.FindMismatch(changed, OwnerConsoleSurfaceText.All, entries);
        Assert.NotNull(mismatch);
        Assert.Contains(AppendEntry, mismatch);
    }

    [Theory]
    [InlineData((int)TextKind.TabTitle)]
    [InlineData((int)TextKind.TabHelpKeys)]
    [InlineData((int)TextKind.TabFooter)]
    [InlineData((int)TextKind.PaneFooter)]
    public void ChangingAnySurfaceTextReportsDrift(int kind)
    {
        var rows = OwnerConsoleBindings.All;
        var entries = ReadChangelog();
        var original = OwnerConsoleSurfaceText.All;
        AssertMatches(rows, entries);
        var fingerprint = OwnerConsoleSurfaceFingerprint.Compute(rows, original);
        var indices = Enumerable.Range(0, original.Count).Where(index => original[index].Kind == (TextKind)kind).ToArray();
        Assert.NotEmpty(indices);

        foreach (var index in indices)
        {
            var changed = original.ToArray();
            var entry = changed[index];
            changed[index] = entry with { Text = $"{entry.Text} [changed {entry.Group} {entry.Variant}]" };

            Assert.NotEqual(fingerprint, OwnerConsoleSurfaceFingerprint.Compute(rows, changed));
            var mismatch = OwnerConsoleSurfaceFingerprint.FindMismatch(rows, changed, entries);
            Assert.NotNull(mismatch);
            Assert.Contains(AppendEntry, mismatch);
        }
    }

    private static void AssertMatches(IReadOnlyList<OwnerConsoleBinding> rows,
        IReadOnlyList<OwnerConsoleChangelogEntry>? entries)
    {
        var mismatch = OwnerConsoleSurfaceFingerprint.FindMismatch(rows, OwnerConsoleSurfaceText.All, entries);
        if (mismatch is not null) Assert.Fail(mismatch);
    }

    private static IReadOnlyList<OwnerConsoleChangelogEntry>? ReadChangelog()
    {
        var path = Path.Combine(VerifiedRepositoryRoot.Find(), "docs", OwnerConsoleChangelog.FileName);
        return File.Exists(path) ? OwnerConsoleChangelog.Parse(File.ReadAllText(path)) : null;
    }
}
