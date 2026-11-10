using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExperimentReplayEvidence
{
    internal static ExperimentReplaySource Write(string directory, string text, string extension)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ExperimentReplayStore.Hash(text) + extension);
        if (!File.Exists(path)) File.WriteAllText(path, text);
        if (File.ReadAllText(path) != text) throw new InvalidDataException("Evidence identity collision.");
        return ExperimentReplayStore.Source(path);
    }

    internal static ExperimentReplaySource Copy(string directory, string source)
    {
        var identity = ExperimentReplayStore.Source(source);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, identity.Sha256 + Path.GetExtension(source));
        if (!File.Exists(target)) File.Copy(source, target);
        if (ExperimentReplayStore.Source(target).Sha256 != identity.Sha256)
            throw new InvalidDataException("Evidence hash mismatch.");
        return identity with { Identity = target };
    }

    internal sealed record Test(string Identity, string Outcome);
    internal static IReadOnlyList<Test> Tests(string path)
    {
        var document = XDocument.Load(path);
        var definitions = document.Descendants().Where(e => e.Name.LocalName == "UnitTest")
            .Where(e => e.Attribute("id") is not null).ToDictionary(e => e.Attribute("id")!.Value,
                e => e.Descendants().FirstOrDefault(m => m.Name.LocalName == "TestMethod"));
        return document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult").Select(e =>
        {
            definitions.TryGetValue(e.Attribute("testId")?.Value ?? "", out var method);
            var identity = method?.Attribute("className")?.Value is { } name
                ? name.Split(',')[0] + "." + method.Attribute("name")?.Value
                : e.Attribute("testName")?.Value ?? "unknown";
            return new Test(identity, e.Attribute("outcome")?.Value ?? "unknown");
        }).ToArray();
    }
}
