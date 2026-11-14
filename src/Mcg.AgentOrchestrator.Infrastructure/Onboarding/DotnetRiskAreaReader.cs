using System.Xml;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reads unit-local risk evidence without evaluating MSBuild or inspecting untracked files.</summary>
internal static class DotnetRiskAreaReader
{
    private static readonly string[] DatabasePackages =
    [
        "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "Microsoft.Data.SqlClient",
        "System.Data.SqlClient", "System.Data.SQLite", "Npgsql", "MySql.Data", "MySqlConnector",
        "Oracle.ManagedDataAccess", "Dapper", "NHibernate", "linq2db", "MongoDB.Driver", "LiteDB",
        "sqlite-net-pcl", "Microsoft.Azure.Cosmos"
    ];

    public static IReadOnlyList<ProjectRiskArea> Read(string root, IReadOnlyList<ProjectUnit> units,
        List<ProjectOwnerQuestion> questions, DiscoveryTrackedPaths tracked)
    {
        var result = new List<ProjectRiskArea>();
        foreach (var unit in units.OrderBy(unit => unit.Id, StringComparer.Ordinal))
        {
            var project = Path.Combine(root, unit.Id);
            var document = ReadProject(root, project);
            if (document?.Root is not { } element)
                continue;

            var package = element.Descendants().Where(candidate => candidate.Name.LocalName == "PackageReference")
                .Where(candidate => candidate.Attribute("Include") is { } include && DatabasePackages.Any(name =>
                    include.Value.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    include.Value.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(candidate => Reliable(candidate, candidate.Attribute("Include")!.Value)).FirstOrDefault();
            if (package is not null)
            {
                var value = package.Attribute("Include")!.Value;
                Add(ProjectRiskAreaKind.Persistence, "persistence", value, Source(unit.Id, package),
                    Reliable(package, value) ? FactConfidence.High : FactConfidence.Low);
            }

            var directory = Path.GetDirectoryName(project)!;
            var settings = Directory.EnumerateFiles(directory, "appsettings*.json")
                .Where(path => IsSettings(Path.GetFileName(path)) && tracked.IncludesFile(path) &&
                    ProjectFactHelpers.IsSafePath(root, path))
                .OrderBy(path => Path.GetFileName(path).Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path, StringComparer.Ordinal).FirstOrDefault();
            if (settings is not null)
            {
                var relative = Path.GetRelativePath(root, settings).Replace('\\', '/');
                Add(ProjectRiskAreaKind.Settings, "settings", relative, new FactSource(relative, 1), FactConfidence.High);
            }

            var publicApi = Path.Combine(directory, "PublicAPI.Shipped.txt");
            if (File.Exists(publicApi) && tracked.IncludesFile(publicApi) && ProjectFactHelpers.IsSafePath(root, publicApi))
            {
                var relative = Path.GetRelativePath(root, publicApi).Replace('\\', '/');
                Add(ProjectRiskAreaKind.PublicApi, "publicApi", relative, new FactSource(relative, 1), FactConfidence.High);
            }
            else if (element.Attribute("Sdk") is { } sdk && IsSdk(sdk.Value, "Microsoft.NET.Sdk.Web"))
                Add(ProjectRiskAreaKind.PublicApi, "publicApi", sdk.Value, Source(unit.Id, element), FactConfidence.Low);

            void Add(ProjectRiskAreaKind kind, string key, string value, FactSource source, FactConfidence confidence) =>
                result.Add(new ProjectRiskArea(unit.Id, kind, ProjectFactHelpers.Fact(value, confidence, source,
                    $"riskAreas/{unit.Id}/{key}", $"Confirm the {key} risk area suggested by '{value}'.", questions)));
        }
        return result;
    }

    private static bool IsSettings(string name) => name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
        (name.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) &&
         name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && name.Length > "appsettings..json".Length);

    private static bool IsSdk(string value, string name) => value.Split(';').Any(sdk =>
        sdk.Trim().Split('/')[0].Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool Reliable(XElement element, string value) =>
        !value.Contains("$(", StringComparison.Ordinal) && !value.Contains("@(", StringComparison.Ordinal) &&
        !value.Contains("%(", StringComparison.Ordinal) && !element.AncestorsAndSelf().Any(ancestor =>
            ancestor.Attribute("Condition") is not null || ancestor.Name.LocalName is "Choose" or "When" or "Otherwise" or "Target");

    private static FactSource Source(string path, XElement element) =>
        new(path, Math.Max(1, ((IXmlLineInfo)element).LineNumber));

    private static XDocument? ReadProject(string root, string path)
    {
        try
        {
            if (!File.Exists(path) || !ProjectFactHelpers.IsSafePath(root, path))
                return null;
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            return document.Root?.Name.LocalName == "Project" ? document : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            // The adapter already records the unreadable unit location; do not duplicate that question.
            return null;
        }
    }
}
