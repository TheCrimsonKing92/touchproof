namespace Mcg.SemanticImpactSpike;

public sealed record CheckResult(string Name, bool Passed, string[] Returned, string[] Expected);

public static class SpikeChecks
{
    public static CheckResult[] ReceiptPaths()
    {
        var home = Path.Combine(Path.GetTempPath(), "receipt-owner");
        var root = Path.Combine(home, "repo");
        var sanitizer = new ReceiptPaths(root, home);
        var json = sanitizer.Serialize(new
        {
            Command = new[] { "--repo", root.ToUpperInvariant(), "ArtifactsPath=" + home.Replace('\\', '/') + "/artifacts" },
            Properties = new { Root = root },
            Projects = new[] { new { Project = Path.Combine(root, "src", "Sample.csproj") } },
            Error = "Failed: " + home.Replace("\\", "\\\\") + "/cache",
            Seconds = 12.5, Passed = true
        });
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var value = document.RootElement;
        var actual = new[] { value.GetProperty("Command")[1].GetString()!, value.GetProperty("Properties").GetProperty("Root").GetString()! };
        var passed = actual.All(s => s == "<repo>") && !json.Contains("receipt-owner", StringComparison.OrdinalIgnoreCase)
            && value.GetProperty("Command")[2].GetString() == "ArtifactsPath=<user-home>/artifacts"
            && value.GetProperty("Error").GetString() == "Failed: <user-home>/cache"
            && value.GetProperty("Projects")[0].GetProperty("Project").GetString()!.StartsWith("<repo>", StringComparison.Ordinal)
            && value.GetProperty("Seconds").GetDouble() == 12.5 && value.GetProperty("Passed").GetBoolean();
        return [new("receipt-path-redaction", passed, actual, ["<repo>", "<repo>"])];
    }

    public static async Task<CheckResult[]> FixtureAsync(string root, IDictionary<string, string> properties)
    {
        using var loader = new SolutionWorkspaceLoader(properties);
        await loader.OpenAsync(Path.Combine(root, "tools/Mcg.SemanticImpactSpike/tests/Fixtures/ImpactFixture/ImpactFixture.sln"));
        if (loader.Failed) throw new InvalidOperationException(string.Join("\n", loader.Diagnostics));
        var index = new ReverseReferenceIndex();
        index.Build(loader, root);
        var results = new List<CheckResult>();
        void Check(string name, string target, params string[] expected)
        {
            var key = "ImpactFixture|" + target;
            if (!index.Symbols.ContainsKey(key)) throw new InvalidOperationException("Fixture target unresolved: " + key);
            var actual = index.Affected([key]);
            results.Add(new(name, actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected), actual, expected));
        }
        Check("interface-first", "M:SpikeFixture.First.Run", "SpikeFixtureInterfaceTests");
        Check("interface-second-explicit", "M:SpikeFixture.Second.SpikeFixture#IService#Run", "SpikeFixtureInterfaceTests");
        Check("interface-unrelated", "M:SpikeFixture.Unrelated.Run");
        Check("static-bound", "M:SpikeFixture.StaticTarget.Run", "SpikeFixtureStaticTests");
        Check("static-unrelated", "M:SpikeFixture.StaticOther.Run");
        Check("delegate-method-group", "M:SpikeFixture.Callback.Target", "SpikeFixtureDelegateTests");
        Check("partial-member", "M:SpikeFixture.PartialTarget.Run", "SpikeFixturePartialTests");
        Check("inherited-fact", "M:SpikeFixture.InheritedTarget.Run", "SpikeFixtureInheritedTests");
        return results.ToArray();
    }

    public static QueryResult[] KnownAnswers(ReverseReferenceIndex index, QueryData data) =>
        data.Queries.Select(q => new ImpactQueryEngine(index).Execute(q)).ToArray();
}
