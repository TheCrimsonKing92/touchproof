using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique temporary roots and scoped observations; no shared cache resets.
public sealed class SourceConfirmedTheoryRowRemovalsParsedRootRoutingTests : IDisposable
{
    private const string FileName = "tests/P/Rows.cs";
    private const string Declaration = "Rows.Runs";
    private const string AssemblyName = "Fixture, Version=1.0.0.0";
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
    private readonly string _marker = "\n// fixture " + Guid.NewGuid().ToString("N");

    [Fact]
    public void Resolve_UnchangedThenRewritten_ReusesAndRefreshesRemovalProof()
    {
        var mainRoot = Path.Combine(_root, "main");
        var candidateRoot = Path.Combine(_root, "candidate");
        var mainPath = Write(mainRoot, Source("a.cs", "b.cs"));
        var candidatePath = Write(candidateRoot, Source("a.cs"));
        string[] arguments = ["dotnet", "Fixture.dll"];
        var identities = new Dictionary<string, string> { ["Fixture"] = "recorded-main-identity" };
        var mainUid = XunitStringTheoryIdentity.MethodUid(arguments, mainRoot,
            AssemblyName, "Rows", "Runs", identities);
        var candidateUid = XunitStringTheoryIdentity.MethodUid(arguments, candidateRoot,
            AssemblyName, "Rows", "Runs");
        Assert.NotNull(mainUid);
        Assert.NotNull(candidateUid);
        var main = Snapshot(mainUid, "a.cs", "b.cs");
        var candidate = Snapshot(candidateUid, "a.cs");
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);
        IReadOnlyDictionary<string, IReadOnlyList<int>> Read() =>
            SourceConfirmedTheoryRowRemovals.Resolve(main, candidate, mainRoot, candidateRoot,
                [Declaration], arguments, arguments, identities);

        var first = Read();
        Assert.Equal(Declaration, Assert.Single(first).Key);
        Assert.Equal(new[] { 2, 3 }, first[Declaration]);
        Assert.Equal(1, parses.CountFor(mainPath));
        Assert.Equal(1, parses.CountFor(candidatePath));
        var second = Read();
        Assert.Equal(first.Keys, second.Keys);
        foreach (var key in first.Keys) Assert.Equal(first[key], second[key]);
        Assert.Equal(1, parses.CountFor(mainPath));
        Assert.Equal(1, parses.CountFor(candidatePath));

        var oldSize = new FileInfo(candidatePath).Length;
        Write(candidateRoot, Source("a.cs", "new-path-absent-from-main.cs"));
        Assert.NotEqual(oldSize, new FileInfo(candidatePath).Length);
        Assert.Empty(Read());
        Assert.Equal(1, parses.CountFor(mainPath));
        Assert.Equal(2, parses.CountFor(candidatePath));
    }

    private static TestDiscoverySnapshot Snapshot(string methodUid, params string[] paths)
    {
        var rows = paths.SelectMany(path => new[] { path, path.Replace('/', '\\').ToUpperInvariant() })
            .Select(argument => new TestDiscoveryRow(XunitStringTheoryIdentity.RowUid(methodUid, argument),
                $"Rows.Runs(path: \"{argument.Replace("\\", "\\\\")}\")", FileName,
                AssemblyName, "Rows", "Runs", true)).ToArray();
        return new(rows.Select(row => row.DisplayName).ToArray(), null, rows);
    }

    private static string Source(params string[] paths) => $$"""
        class Rows
        {
            public static System.Collections.Generic.IEnumerable<object[]> Data()
            {
                string[] paths = [{{string.Join(", ", paths.Select(path => "\"" + path + "\""))}}];
                foreach (var path in paths)
                {
                    yield return [path];
                    yield return [path.Replace('/', '\\').ToUpperInvariant()];
                }
            }
            [Xunit.Theory]
            [Xunit.MemberData(nameof(Data))]
            public void Runs(string path) { }
        }
        """;

    private string Write(string root, string text)
    {
        var path = Path.Combine(root, FileName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text + _marker);
        return path;
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
