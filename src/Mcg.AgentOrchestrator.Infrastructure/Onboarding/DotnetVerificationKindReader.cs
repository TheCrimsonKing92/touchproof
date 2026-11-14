using System.Xml;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Associates verification activities with declarations, preserving uncertainty about service health.</summary>
internal static class DotnetVerificationKindReader
{
    public static IReadOnlyList<ProjectVerificationKind> Read(string root, IReadOnlyList<ProjectUnit> units,
        IReadOnlyList<UnitTestSetup> setups, List<ProjectOwnerQuestion> questions)
    {
        var result = new List<ProjectVerificationKind>();
        foreach (var unit in units.OrderBy(unit => unit.Id, StringComparer.Ordinal))
        {
            var document = ReadProject(root, Path.Combine(root, unit.Id));
            if (document?.Root is not { } element)
                continue;
            var runner = setups.FirstOrDefault(setup => setup.UnitId == unit.Id)?.Runner;
            if (unit.IsTest.Value == true && runner is { Confidence: FactConfidence.High })
                result.Add(new ProjectVerificationKind(unit.Id, UnitVerificationKind.UnitTests, runner));

            var desktop = element.Descendants().Where(candidate =>
                (candidate.Parent?.Name.LocalName == "PropertyGroup" &&
                 candidate.Name.LocalName is "UseWPF" or "UseWindowsForms" or "UseMaui" &&
                 candidate.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) ||
                (candidate.Name.LocalName == "PackageReference" && candidate.Attribute("Include") is { } include &&
                 (include.Value.Equals("Avalonia", StringComparison.OrdinalIgnoreCase) ||
                  include.Value.Equals("Avalonia.Desktop", StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(candidate => Reliable(candidate, Value(candidate))).FirstOrDefault();
            if (desktop is not null)
            {
                var value = desktop.Attribute("Include")?.Value ?? desktop.Name.LocalName;
                Add(UnitVerificationKind.DesktopUi, "desktopUi", value, Source(unit.Id, desktop),
                    Reliable(desktop, Value(desktop)) ? FactConfidence.High : FactConfidence.Low);
            }
            if (element.Attribute("Sdk") is { } sdk && sdk.Value.Split(';').Any(value =>
                value.Trim().Split('/')[0].Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
                value.Trim().Split('/')[0].Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)))
                // An SDK suggests service verification, but does not declare a health endpoint.
                Add(UnitVerificationKind.ServiceHealth, "serviceHealth", sdk.Value, Source(unit.Id, element), FactConfidence.Low);

            void Add(UnitVerificationKind kind, string key, string value, FactSource source, FactConfidence confidence) =>
                result.Add(new ProjectVerificationKind(unit.Id, kind, ProjectFactHelpers.Fact(value, confidence, source,
                    $"verifications/{unit.Id}/{key}", $"Confirm {key} verification suggested by '{value}'.", questions)));
        }
        return result;
    }

    private static string Value(XElement element) => element.Attribute("Include")?.Value ?? element.Value.Trim();

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
            return null;
        }
    }
}
