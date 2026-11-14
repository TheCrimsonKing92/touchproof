using System.Text.RegularExpressions;

// Parallel-safe: every fact owns its temp tree and keeps its own deletion-blocking handle open.
public sealed class SharedTestSupportRemoveTempDirectoryFailureMessageTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Windows deletion-blocking file handles.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_FileHeldPastRealBudget_ThrowsWithAttemptAndResidualDetail()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var heldFile = Path.Combine(root, "held.txt");
        File.WriteAllText(heldFile, "held");
        try
        {
            using var holder = new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Keep the real handle open throughout the helper's unchanged real budget.
            // The verdict checks its failure and reported diagnostics, never wall-clock pacing.
            var error = Assert.Throws<IOException>(() => SharedTestSupport.RemoveTempDirectory(root));

            Assert.Contains(root, error.Message, StringComparison.Ordinal);
            Assert.Equal(typeof(IOException), error.InnerException!.GetType());
            Assert.Contains(error.InnerException.Message, error.Message, StringComparison.Ordinal);
            var calls = Regex.Matches(error.Message,
                @"removal (\d+) \(attempt (\d+)\): duration=\d+ ms; exception=IOException");
            Assert.NotEmpty(calls);
            for (var i = 0; i < calls.Count; i++)
            {
                Assert.Equal((i + 1).ToString(), calls[i].Groups[1].Value);
                Assert.Equal((i + 1).ToString(), calls[i].Groups[2].Value);
            }
            var headline = Regex.Match(error.Message, @"after (\d+) attempts over (\d+) ms\.");
            Assert.True(headline.Success, error.Message);
            Assert.Equal(calls.Count, int.Parse(headline.Groups[1].Value));
            Assert.True(long.Parse(headline.Groups[2].Value) >= SharedTestSupport.RemoveTempDirectoryBudgetMilliseconds);
            Assert.Contains("Attribute pass: not run.", error.Message, StringComparison.Ordinal);
            Assert.Contains($"Residual root '{root}' attributes=", error.Message, StringComparison.Ordinal);
            Assert.Contains($"'{heldFile}' attributes={File.GetAttributes(heldFile)}", error.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact(Skip = "Requires Windows deletion-blocking file handles.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_GitStoreBesideHeldFile_ReportsAttributePassBeforeRemoval()
        => AssertGitStoreBesideHeldFileReportsProactivePass(oneLevelDown: false);

    [Fact(Skip = "Requires Windows deletion-blocking file handles.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_NestedGitStoreBesideHeldFile_ReportsProactiveAttributePass()
        => AssertGitStoreBesideHeldFileReportsProactivePass(oneLevelDown: true);

    private static void AssertGitStoreBesideHeldFileReportsProactivePass(bool oneLevelDown)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var repository = oneLevelDown ? Path.Combine(root, "repository") : root;
        var readOnlyFile = Path.Combine(repository, ".git", "objects", "ab", "cdef0123");
        CreateReadOnlyFile(readOnlyFile);
        var heldFile = Path.Combine(root, "held.txt");
        File.WriteAllText(heldFile, "held");
        var elapsedCalls = 0;
        var delays = 0;
        try
        {
            using var holder = new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var error = Assert.Throws<IOException>(() => SharedTestSupport.RemoveTempDirectory(
                root,
                () =>
                {
                    elapsedCalls++;
                    return TimeSpan.FromMilliseconds(SharedTestSupport.RemoveTempDirectoryBudgetMilliseconds + 1);
                },
                _ => delays++));

            Assert.Matches(@"Attribute pass: ran before first removal; started at \d+ ms; duration=\d+ ms\.", error.Message);
            Assert.Contains("removal 1 (attempt 1):", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("removal 2", error.Message, StringComparison.Ordinal);
            Assert.Equal(typeof(IOException), error.InnerException!.GetType());
            Assert.Equal(1, elapsedCalls);
            Assert.Equal(0, delays);
        }
        finally
        {
            ClearIfPresent(readOnlyFile);
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact(Skip = "Requires Windows read-only deletion semantics.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_ReadOnlyBesideHeldFile_ReportsAttributePassAfterAttempt()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        // Create and name the read-only entry before the held entry, so its failure is first.
        var readOnlyFile = Path.Combine(root, "000-readonly.txt");
        CreateReadOnlyFile(readOnlyFile);
        var heldFile = Path.Combine(root, "zzz-held.txt");
        File.WriteAllText(heldFile, "held");
        var elapsedCalls = 0;
        var delays = 0;
        try
        {
            using var holder = new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var error = Assert.Throws<IOException>(() => SharedTestSupport.RemoveTempDirectory(
                root,
                () =>
                {
                    elapsedCalls++;
                    return TimeSpan.FromMilliseconds(SharedTestSupport.RemoveTempDirectoryBudgetMilliseconds + 1);
                },
                _ => delays++));

            Assert.Matches(@"Attribute pass: ran after attempt 1; started at \d+ ms; duration=\d+ ms\.", error.Message);
            Assert.Matches(@"removal 1 \(attempt 1\): duration=\d+ ms; exception=UnauthorizedAccessException", error.Message);
            Assert.Matches(@"removal 2 \(attempt 1, retry after attribute pass\): duration=\d+ ms; exception=IOException", error.Message);
            Assert.DoesNotContain("removal 3", error.Message, StringComparison.Ordinal);
            Assert.Equal(typeof(IOException), error.InnerException!.GetType());
            Assert.Equal(1, elapsedCalls);
            Assert.Equal(0, delays);
            Assert.Contains($"'{heldFile}' attributes=", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            ClearIfPresent(readOnlyFile);
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static void CreateReadOnlyFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "read only");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
    }

    private static void ClearIfPresent(string path)
    {
        if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }
}
