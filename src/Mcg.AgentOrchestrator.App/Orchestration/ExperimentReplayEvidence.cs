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
        var read = new TrxTestResultSource().Read(path);
        if (read.Status != TestResultReadStatus.Readable)
            throw new InvalidDataException($"Replay TRX is {read.Status}: {read.Detail}");
        var tests = read.Records.Select(row => new Test(row.Id, row.Status.ToString())).ToList();
        // Summary-only fatal evidence must not disappear when the host wrote partial rows.
        var document = XDocument.Load(path);
        if (document.Descendants().Where(e => e.Name.LocalName == "Counters").Any(counter =>
            AcceptanceTrxOutcomeTaxonomy.HasFatalCounter(name => name == "failed" ? 0 :
                int.TryParse(counter.Attribute(name)?.Value, out var count) ? count : null)))
            tests.Add(new("TRX fatal summary", "Error"));
        return tests;
    }

    internal static string? ExecutionError(IReadOnlyList<Test> tests, int exitCode)
    {
        if (tests.Any(t => t.Outcome == "Error")) return "Fatal or unknown TRX outcome; execution is incomplete.";
        if (!tests.Any(t => t.Outcome is "Passed" or "Failed")) return "Zero executed tests in selected project.";
        // Native MTP uses 2 for test failures. Other nonzero exits describe host/apparatus failures.
        var failed = tests.Any(t => t.Outcome == "Failed");
        return exitCode == (failed ? 2 : 0) ? null : $"Test host exit {exitCode} disagrees with completed TRX outcomes.";
    }
}
