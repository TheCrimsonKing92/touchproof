// Parallel-safe: every fact owns a unique temp tree and changes only its own file attributes.
public sealed class SharedTestSupportRemoveTempDirectoryAttributeRetryTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Windows read-only deletion semantics.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_ReadOnlyFileAndSpentBudget_RetriesAfterClearingAndRemoves()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var file = Path.Combine(root, "readonly.txt");
        CreateReadOnlyFile(file);
        var elapsedCalls = 0;
        var delays = 0;
        try
        {
            var error = Record.Exception(() => SharedTestSupport.RemoveTempDirectory(
                root,
                () =>
                {
                    elapsedCalls++;
                    return TimeSpan.FromMilliseconds(SharedTestSupport.RemoveTempDirectoryBudgetMilliseconds + 1);
                },
                _ => delays++));

            Assert.Null(error);
            Assert.False(Directory.Exists(root));
            Assert.Equal(0, elapsedCalls);
            Assert.Equal(0, delays);
        }
        finally
        {
            ClearIfPresent(file);
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact(Skip = "Requires Windows read-only deletion semantics.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_GitObjectStoreAtRoot_RemovesOnFirstAttempt()
        => AssertGitStoreRemovedOnFirstAttempt(oneLevelDown: false);

    [Fact(Skip = "Requires Windows read-only deletion semantics.", SkipUnless = nameof(IsWindows))]
    public void RemoveTempDirectory_GitObjectStoreOneLevelDown_RemovesOnFirstAttempt()
        => AssertGitStoreRemovedOnFirstAttempt(oneLevelDown: true);

    private static void AssertGitStoreRemovedOnFirstAttempt(bool oneLevelDown)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var repository = oneLevelDown ? Path.Combine(root, "project") : root;
        var objects = Path.Combine(repository, ".git", "objects");
        var looseObject = Path.Combine(objects, "ab", "cdef0123");
        var pack = Path.Combine(objects, "pack", "pack-1.pack");
        CreateReadOnlyFile(looseObject);
        CreateReadOnlyFile(pack);
        var elapsedCalls = 0;
        var delays = 0;
        try
        {
            SharedTestSupport.RemoveTempDirectory(
                root,
                () => { elapsedCalls++; return TimeSpan.Zero; },
                _ => delays++);

            Assert.False(Directory.Exists(root));
            Assert.Equal(0, elapsedCalls);
            Assert.Equal(0, delays);
        }
        finally
        {
            ClearIfPresent(looseObject);
            ClearIfPresent(pack);
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
