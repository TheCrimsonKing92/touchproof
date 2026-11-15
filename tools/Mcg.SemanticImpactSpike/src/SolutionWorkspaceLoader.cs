using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace Mcg.SemanticImpactSpike;

public sealed record ProjectSettings(string Project, string Assembly, string Framework,
    string LanguageVersion, string Nullable, string Optimization, string[] Defines, int Documents);

public sealed class SolutionWorkspaceLoader : IDisposable
{
    private readonly MSBuildWorkspace workspace;
    public ConcurrentQueue<string> Diagnostics { get; } = new();
    public List<ProjectSettings> Settings { get; } = [];
    public double OpenSeconds { get; private set; }
    public double CompilationSeconds { get; private set; }
    public Solution? Solution { get; private set; }
    public List<(Project Project, Compilation Compilation)> Compilations { get; } = [];
    public bool Failed { get; private set; }

    public SolutionWorkspaceLoader(IDictionary<string, string> properties)
    {
        workspace = MSBuildWorkspace.Create(properties);
        workspace.LoadMetadataForReferencedProjects = false;
        workspace.WorkspaceFailed += (_, e) =>
        {
            Diagnostics.Enqueue(e.Diagnostic.Kind + ": " + e.Diagnostic.Message);
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) Failed = true;
        };
    }

    public async Task OpenAsync(string path)
    {
        var timer = Stopwatch.StartNew();
        Solution = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? (await workspace.OpenProjectAsync(path)).Solution
            : await workspace.OpenSolutionAsync(path);
        OpenSeconds = timer.Elapsed.TotalSeconds;
        timer.Restart();
        foreach (var project in Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null)
            {
                Failed = true;
                Diagnostics.Enqueue("Failure: no compilation for " + project.FilePath);
                continue;
            }
            // Force declaration and body binding: merely obtaining Compilation is lazy.
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            foreach (var error in errors) Diagnostics.Enqueue("Failure: " + project.Name + ": " + error);
            if (errors.Length != 0) Failed = true;
            Compilations.Add((project, compilation));
            var parse = (CSharpParseOptions)project.ParseOptions!;
            var framework = compilation.Assembly.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "System.Runtime.Versioning.TargetFrameworkAttribute")
                ?.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "unknown";
            Settings.Add(new(project.FilePath!, compilation.AssemblyName!, framework,
                parse.LanguageVersion.ToString(), compilation.Options.NullableContextOptions.ToString(),
                compilation.Options.OptimizationLevel.ToString(), parse.PreprocessorSymbolNames.ToArray(), project.DocumentIds.Count));
        }
        CompilationSeconds = timer.Elapsed.TotalSeconds;
    }

    public void Dispose() => workspace.Dispose();
}
