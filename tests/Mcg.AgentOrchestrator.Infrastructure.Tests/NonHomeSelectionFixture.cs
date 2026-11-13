using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Each caller owns its files; the git marker selects candidate probing without spawning git.
internal sealed class NonHomeSelectionFixture : IDisposable
{
    internal const string Project = "tests/Widget/Specs/Widget.Tests.csproj";
    internal const string Source = "src/Widget/Product/Widget.cs";
    internal const string Test = "tests/Widget/Specs/WidgetTests.cs";
    internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
    internal string Home => Path.Combine(Root, "project-home");
    internal string Worktree => Path.Combine(Root, "candidate");

    internal NonHomeSelectionFixture(bool solution = false)
    {
        Write(".git", "gitdir: fixture");
        Write("src/Widget/Product/Widget.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(Source, "public class Widget { }");
        Write(Project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(Test, "public class WidgetTests { [Xunit.Fact] public void Works() { } }");
        if (solution) Write("Widget.sln", "Microsoft Visual Studio Solution File, Format Version 12.00");
        WriteManifest("""
            {"engine":{"enforceStructuralCoverage":false},"checks":[
              {"name":"widget tests","type":"dotnet-test","project":"tests/Widget/Specs/Widget.Tests.csproj"}]}
            """);
    }

    internal void WriteManifest(string json)
    {
        Directory.CreateDirectory(Home);
        File.WriteAllText(Path.Combine(Home, "acceptance-manifest.json"), json);
    }

    internal void WriteManifest(object value) => WriteManifest(JsonSerializer.Serialize(value));

    private void Write(string path, string content)
    {
        var fullPath = Path.Combine(Worktree, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
}
