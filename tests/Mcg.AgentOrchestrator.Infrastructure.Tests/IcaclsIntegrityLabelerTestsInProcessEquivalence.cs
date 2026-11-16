using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.ProcessSpawning)]
public sealed class IcaclsIntegrityLabelerTestsInProcessEquivalence
{
    [Theory]
    [InlineData("L", "file")]
    [InlineData("M", "file")]
    [InlineData("(OI)(CI)L", "file")]
    [InlineData("(OI)(CI)M", "file")]
    [InlineData("L", "directory")]
    [InlineData("M", "directory")]
    [InlineData("(OI)(CI)L", "directory")]
    [InlineData("(OI)(CI)M", "directory")]
    [InlineData("L", "tree")]
    [InlineData("M", "tree")]
    [InlineData("(OI)(CI)L", "tree")]
    [InlineData("(OI)(CI)M", "tree")]
    public async Task Set_AllLevelsAndShapes_MatchIcaclsForEveryEntry(string level, string shape)
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        var root = Directory.CreateTempSubdirectory("equivalent-il-").FullName;
        try
        {
            // Both twins inherit the same known Medium security state. Only one
            // descendant below receives an explicit label before the compared writes.
            await OracleAsync(root, "(OI)(CI)M", false);
            var native = CreateFixture(root, "native", shape);
            var oracle = CreateFixture(root, "oracle", shape);
            if (shape == "tree")
            {
                await OracleAsync(Path.Combine(native, "nested", "explicit.txt"), "M", false);
                await OracleAsync(Path.Combine(oracle, "nested", "explicit.txt"), "M", false);
            }
            var entries = RelativeEntries(native, shape);
            Assert.Equal(entries, RelativeEntries(oracle, shape));
            var labeler = new IcaclsIntegrityLabeler(_ =>
                throw new InvalidOperationException("Only the oracle may launch icacls."));
            var before = entries.ToDictionary(relative => relative,
                relative => labeler.Query(At(oracle, relative)));
            foreach (var relative in entries)
                Assert.Equal(before[relative], labeler.Query(At(native, relative)));

            var result = labeler.SetIntegrityWithDiagnostics(native, level, shape == "tree");
            Assert.True(result.Succeeded, result.Diagnostic);
            await OracleAsync(oracle, level, shape == "tree");

            foreach (var relative in entries)
            {
                var expected = labeler.Query(At(oracle, relative));
                var actual = labeler.Query(At(native, relative));
                Assert.True(expected.Exists, relative);
                Assert.Null(expected.NativeQueryError);
                var inheritable = level.StartsWith("(OI)(CI)", StringComparison.Ordinal);
                if (inheritable && (shape == "file"
                    || relative == Path.Combine("nested", "explicit.txt")))
                {
                    // Inheritable requests do not overwrite explicit file labels.
                    Assert.True(before[relative].Medium, relative);
                    Assert.Equal(before[relative], expected);
                }
                else if (shape == "directory" && relative != "." && !inheritable)
                {
                    // Removing the parent's inheritance leaves these entries unlabeled.
                    Assert.True(expected.Unlabeled, relative);
                }
                else
                {
                    Assert.Equal(level.EndsWith('L'), expected.Low);
                    Assert.Equal(level.EndsWith('M'), expected.Medium);
                    Assert.Equal(inheritable && Directory.Exists(At(oracle, relative)),
                        expected.Inheritable);
                    Assert.False(expected.Unlabeled);
                }
                // Record equality covers Exists, Low, Medium, Inheritable,
                // Unlabeled and NativeQueryError, including the root entry.
                Assert.Equal(expected, actual);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateFixture(string root, string name, string shape)
    {
        var path = Path.Combine(root, name);
        if (shape == "file")
        {
            File.WriteAllText(path, "fixture");
            return path;
        }
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "root.txt"), "fixture");
        var nested = Directory.CreateDirectory(Path.Combine(path, "nested")).FullName;
        if (shape == "tree")
        {
            File.WriteAllText(Path.Combine(nested, "explicit.txt"), "fixture");
            var deep = Directory.CreateDirectory(Path.Combine(nested, "deep")).FullName;
            File.WriteAllText(Path.Combine(deep, "leaf.txt"), "fixture");
            Directory.CreateDirectory(Path.Combine(deep, "empty"));
            var hidden = Path.Combine(path, "hidden.txt");
            File.WriteAllText(hidden, "fixture");
            File.SetAttributes(hidden, FileAttributes.Hidden);
        }
        return path;
    }

    private static string[] RelativeEntries(string root, string shape) => shape == "file" ? ["."]
        : new[] { "." }.Concat(Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        }).Select(path => Path.GetRelativePath(root, path))).Order(StringComparer.Ordinal).ToArray();

    private static string At(string root, string relative) => relative == "." ? root : Path.Combine(root, relative);

    private static async Task OracleAsync(string path, string level, bool recursive)
    {
        var arguments = new List<string>
        {
            Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            IcaclsIntegrityLabeler.NativeFilePath(path, recursive), "/setintegritylevel", level
        };
        if (recursive) arguments.Add("/T");
        var result = await AcceptanceProcessTestContext.RunManifestCommandAsync(arguments.ToArray(),
            Path.GetDirectoryName(path)!, TimeSpan.FromMinutes(2), new Dictionary<string, string?>(),
            resolveBareCommand: false, CancellationToken.None);
        Assert.False(result.TimedOut);
        Assert.True(result.ExitCode == 0, result.Output);
    }
}
