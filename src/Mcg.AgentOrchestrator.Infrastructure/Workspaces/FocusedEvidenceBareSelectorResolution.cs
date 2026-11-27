using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;
using FocusedEvidenceFilterToken = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;
using FocusedEvidenceTokenKind = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceTokenKind;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceBareSelectorResolution
{
    internal static string DescribeFilenameMatches(
        string worktreePath, string request, AcceptanceGateEngineSettings settings, string offendingToken)
    {
        var token = offendingToken.Trim();
        var selector = Regex.Match(token, @"^(?:FullyQualifiedName\s*~\s*)?(?<name>[A-Za-z_][A-Za-z0-9_.]*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!selector.Success) return string.Empty;

        // An unresolved dotted selector can name a qualified class or a class followed by a method.
        var name = selector.Groups["name"].Value;
        var components = name.Split('.');
        var stems = components.TakeLast(2).ToHashSet(StringComparer.Ordinal);
        try
        {
            var projects = new List<string>();
            foreach (var item in request.Split(';'))
            {
                var separator = item.IndexOf(':');
                var expression = item[(separator + 1)..];
                if (!expression.Split(['&', '|', '(', ')'], StringSplitOptions.TrimEntries)
                        .Contains(token, StringComparer.Ordinal)) continue;
                var project = InfrastructureTestsProject;
                if (separator >= 0 && !FocusedEvidenceRequestResolver.TryResolveFocusedEvidenceProject(
                        item[..separator], settings, out project)) continue;
                if (project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                    !projects.Contains(project, StringComparer.OrdinalIgnoreCase)) projects.Add(project);
            }
            // If multiple projects use the same token, resolution stops at the first project that rejects it.
            var target = projects.Count == 1 ? projects[0] : projects.FirstOrDefault(project =>
                !FocusedEvidenceRequestResolver.TryResolveFocusedEvidenceSelection(
                    worktreePath, project, offendingToken, name, out _, out _));
            if (target is null) return string.Empty;

            var classes = FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(worktreePath, target)
                .Where(path => stems.Contains(Path.GetFileNameWithoutExtension(path)))
                .SelectMany(path => ReadDeclaredClasses(path, worktreePath)).Distinct();
            var names = classes.GroupBy(item => item.SimpleName, StringComparer.Ordinal)
                .SelectMany(group => group.Count() == 1 ? new[] { group.Key } : group.Select(item => item.QualifiedName))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return names.Length == 0 ? string.Empty : $" Matching test source files declare: {string.Join(", ", names)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Source suggestions are optional. The recorded rejection remains authoritative.
        }
        return string.Empty;
    }

    internal static FocusedEvidenceParsedRootCache.DeclaredTypeName[] ReadDeclaredClasses(string path, string worktreePath)
    {
        try
        {
            return FocusedEvidenceDeclarationFactStore.Current.Read(path, worktreePath).Types
                .Where(type => type.IsClassOrNonStructRecord).Select(type => type.Name).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static bool TryResolve(
        Func<IEnumerable<string>> discoverSourceFiles,
        string project,
        string originalToken,
        string value,
        out FocusedEvidenceFilterToken selection,
        out FocusedEvidenceRejection rejection)
    {
        selection = new FocusedEvidenceFilterToken(
            originalToken, $"FullyQualifiedName~{value}", FocusedEvidenceTokenKind.Class, value, value);
        rejection = new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.UnresolvableSelection,
            originalToken,
            $"focused evidence selection '{originalToken}' could not be resolved");
        var owners = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var sourceFiles = discoverSourceFiles().ToArray();
            if (sourceFiles.Length == 0)
            {
                return true;
            }

            foreach (var path in sourceFiles)
            {
                var source = File.ReadAllText(path);
                if (!source.Contains(value, StringComparison.Ordinal))
                {
                    continue;
                }

                var declarations = CSharpSyntaxTree.ParseText(source, path: path)
                    .GetCompilationUnitRoot().DescendantNodes().OfType<TypeDeclarationSyntax>();
                foreach (var declaration in declarations)
                {
                    var simpleName = declaration.Identifier.ValueText;
                    if (simpleName.Contains(value, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (!declaration.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                            method.Identifier.ValueText.Equals(value, StringComparison.Ordinal) &&
                            method.AttributeLists.SelectMany(list => list.Attributes)
                                .Any(attribute => FocusedEvidenceMethodResolver.IsTestAttribute(attribute.Name.ToString()))))
                    {
                        continue;
                    }

                    owners[FocusedEvidenceParsedRootCache.GetDeclaredTypeName(declaration).QualifiedName] = simpleName;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the historical class fallback when source discovery cannot establish ownership.
            return true;
        }

        if (owners.Count == 1)
        {
            var owner = owners.Single().Value;
            var method = $"{owner}.{value}";
            selection = new FocusedEvidenceFilterToken(
                originalToken, $"FullyQualifiedName~{method}", FocusedEvidenceTokenKind.Method, method, owner);
            return true;
        }

        selection = null!;
        rejection = new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.UnresolvableSelection,
            originalToken,
            owners.Count == 0
                ? $"focused evidence selection '{originalToken}' does not resolve to a class or method in {GoalAcceptanceVerifier.ProjectLabel(project)}"
                : $"focused evidence selection '{originalToken}' is ambiguous in {GoalAcceptanceVerifier.ProjectLabel(project)}; candidates: {string.Join(", ", owners.Keys)}");
        return false;
    }
}
