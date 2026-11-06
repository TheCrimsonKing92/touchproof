using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsLaunchSiteCensus
{
    // Deliberately retained managed paths. The lock remains only for these compatibility launches.
    private sealed record ExceptionSite(string Reason, bool InheritsHandles);
    private static readonly IReadOnlyDictionary<string, ExceptionSite> Allowed = new Dictionary<string, ExceptionSite>
    {
        ["Mcg.AgentOrchestrator.Execution/Processes/ProcessTreeGuiSuppression.cs|Start|managed"] = new("Non-Windows fallback; unchanged behavior.", false),
        ["Mcg.AgentOrchestrator.Execution/Processes/ProcessTreeGuiSuppression.cs|Start|legacy"] = new("Test-host Process compatibility requires managed redirected streams; inherits handles under the legacy lock.", true),
        ["Mcg.AgentOrchestrator.Execution/Processes/ProcessTreeGuiSuppression.cs|StartWithLaunchLock|managed"] = new("Legacy ProcessStartInfo test-host compatibility; inherits handles under the legacy lock.", true),
        ["Mcg.AgentOrchestrator.Execution/Processes/ProcessTreeGuiSuppression.cs|StartWithLaunchLock|instance"] = new("Legacy Process test-host compatibility; inherits handles under the legacy lock.", true),
        ["Mcg.AgentOrchestrator.Execution/Processes/HandleListLaunchedProcess.cs|Start|legacy"] = new("Non-Windows fallback; never selected on Windows.", false),
        ["Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.cs|StartRegisteredOrThrow|legacy"] = new("Non-Windows registered launch delegate; Windows uses the owned handle-list launcher.", false),
        ["Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.cs|StartRegisteredOwnedOrThrow|legacy"] = new("Non-Windows owned launch delegate; Windows uses the owned handle-list launcher.", false),
        ["Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceCheckProcessInvoker.cs|StartAcceptanceProcess|legacy"] = new("Environment-gated negative control intentionally replays start-then-attach.", true)
    };

    [Xunit.Fact]
    public void ProductLaunches_UseSharedNativeBoundaryOrDocumentedCompatibility()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var found = new List<string>();
        var native = new List<string>();
        foreach (var path in snapshot.EnumerateFiles(Path.Combine(root, "src"))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !SourceGuardFileFilter.BuildOutputAndScratch.Excludes(path)))
        {
            var text = snapshot.GetText(path);
            var relative = Path.GetRelativePath(Path.Combine(root, "src"), path).Replace('\\', '/');
            found.AddRange(Detect(text).Select(site => relative + "|" + site));
            if (CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Any(method => method.Identifier.ValueText == "CreateProcessW")) native.Add(relative);
        }
        Assert.Equal(Allowed.Keys.Order(), found.Order()); // Rejects both unknown launches and stale exceptions.
        Assert.All(Allowed.Values, site => Assert.False(string.IsNullOrWhiteSpace(site.Reason)));
        Assert.Equal(["Mcg.AgentOrchestrator.Execution/Processes/HandleListProcessLauncher.cs"], native);
        var suppression = snapshot.GetText(Path.Combine(root, "src/Mcg.AgentOrchestrator.Execution/Processes/ProcessTreeGuiSuppression.cs"));
        if (Allowed.Values.Any(site => site.InheritsHandles)) Assert.Contains("WindowsLaunchLock", suppression);
        else Assert.DoesNotContain("WindowsLaunchLock", suppression);
    }

    [Xunit.Fact]
    public void Detector_FindsStaticInstanceAndPolicyForms()
    {
        var source = "using System.Diagnostics; class Control { void Run(Process process, ProcessStartInfo info) { " +
            "Process.Start(info); process.Start(); ProcessTreeGuiSuppression.Start(info); " +
            "ProcessTreeGuiSuppression.StartWithLaunchLock(info); } }";
        Assert.Equal(["Run|managed", "Run|instance", "Run|legacy", "Run|legacy"], Detect(source));
    }

    private static IEnumerable<string> Detect(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var accesses = tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(node => node.Name.Identifier.ValueText is "Start" or "StartWithLaunchLock").ToArray();
        var unqualified = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(node => node.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == "StartWithLaunchLock");
        var compilation = CSharpCompilation.Create("LaunchCensus", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(typeof(Process).Assembly.Location)]);
        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        foreach (var access in accesses)
        {
            var expression = access.Expression.ToString();
            var kind = expression == "ProcessTreeGuiSuppression" ? "legacy" :
                model.GetTypeInfo(access.Expression).Type?.ToDisplayString() == "System.Diagnostics.Process"
                    ? (expression is "Process" or "System.Diagnostics.Process" ? "managed" : "instance") : null;
            if (kind is not null)
                yield return access.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText + "|" + kind;
        }
        foreach (var call in unqualified)
            yield return call.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText + "|legacy";
    }
}
