using System.Security.Cryptography;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test writes its retained TRX files to its own temporary directory.
public sealed class NestedTestClassResultCoverageTests : IDisposable
{
    private const string NestedClass = "Ns.Outer+Inner";
    private const string TestMethod = "Guard";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("nested-test-trx-");

    [Theory]
    [InlineData(NestedClass, "Inner")]
    [InlineData("Ns.XInner+Inner", "Inner")]
    [InlineData("Ns.Plain", "Plain")]
    public void PassedSelectionsMatchClassAtBoundary(string className, string selection)
    {
        var green = WriteArm(className, red: false);

        Assert.True(FindingEvidenceTrxCoverage.HasPassedSelections(green, SelectClass(selection)));
    }

    [Theory]
    [InlineData(NestedClass, "Inner")]
    [InlineData(NestedClass, NestedClass)]
    [InlineData("Ns.Outer+Middle+Inner", "Middle+Inner")]
    [InlineData("Ns.Plain", "Plain")]
    public void RedCoverageMatchesRequiredClass(string className, string required)
    {
        var red = WriteArm(className, red: true);

        Assert.True(FindingEvidenceTrxCoverage.HasContentBoundRedTrxCoverage(red, [required]));
    }

    [Theory]
    [InlineData(NestedClass, "Inner", false)]
    [InlineData(NestedClass, "Inner", true)]
    [InlineData(NestedClass, NestedClass, false)]
    [InlineData("Ns.Outer+Middle+Inner", "Middle+Inner", false)]
    [InlineData("Ns.Plain", "Plain", false)]
    public void GreenCoverageMatchesRequiredClass(string className, string required, bool allowShortNamePrefix)
    {
        var green = WriteArm(className, red: false);

        Assert.Null(ContentBoundGreenTrxEvidenceCheck.FirstFailure(green.TestResultPaths,
            green.ReceiptArtifacts, 1, [required], allowShortNamePrefix));
    }

    [Theory]
    [InlineData(NestedClass, "XInner")]
    [InlineData("Ns.Outer+XInner", "Inner")]
    [InlineData(NestedClass, "Outer")]
    public void PartialClassNamesMissSelectionAndRequiredCoverage(string className, string partialName)
    {
        var green = WriteArm(className, red: false);
        var red = WriteArm(className, red: true);

        Assert.True(FindingEvidenceTrxCoverage.HasPassedIdentities(green, [$"{className}.{TestMethod}"]));
        Assert.True(FindingEvidenceTrxCoverage.HasContentBoundRedTrxCoverage(red, [className]));
        Assert.False(FindingEvidenceTrxCoverage.HasPassedSelections(green, SelectClass(partialName)));
        Assert.False(FindingEvidenceTrxCoverage.HasContentBoundRedTrxCoverage(red, [partialName]));
        var failure = ContentBoundGreenTrxEvidenceCheck.FirstFailure(green.TestResultPaths,
            green.ReceiptArtifacts, 1, [partialName]);
        Assert.NotNull(failure);
        Assert.Equal("required-class-missing", failure.Check);
        Assert.Equal(partialName, Assert.Single(failure.MissingClasses));
    }

    private static FindingEvidenceRequest SelectClass(string className) =>
        new([new("Mcg.AgentOrchestrator.Infrastructure.Tests", className)]);

    private FindingEvidenceArmReceipt WriteArm(string className, bool red)
    {
        var path = Path.Combine(_directory.FullName, red ? "red.trx" : "green.trx");
        XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(trx + "TestRun",
            new XElement(trx + "TestDefinitions",
                new XElement(trx + "UnitTest", new XAttribute("id", "1"), new XAttribute("name", TestMethod),
                    new XElement(trx + "TestMethod", new XAttribute("className", className),
                        new XAttribute("name", TestMethod)))),
            new XElement(trx + "Results",
                new XElement(trx + "UnitTestResult", new XAttribute("testId", "1"),
                    new XAttribute("testName", $"{className}.{TestMethod}"),
                    new XAttribute("outcome", red ? "Failed" : "Passed"))),
            new XElement(trx + "ResultSummary", new XElement(trx + "Counters",
                new XAttribute("total", 1), new XAttribute("executed", 1),
                new XAttribute("passed", red ? 0 : 1), new XAttribute("failed", red ? 1 : 0))))).Save(path);

        var bytes = File.ReadAllBytes(path);
        return new(red ? FindingEvidenceArm.SourceReverted : FindingEvidenceArm.Candidate, "candidate",
            red ? FindingEvidenceArmDisposition.Red : FindingEvidenceArmDisposition.Green, true, !red, "executed",
            ExecutedTestCount: 1, TestResultPaths: [path],
            ReceiptArtifacts: [new("trx", path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length)]);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
