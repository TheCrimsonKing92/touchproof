using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: in-memory check receipts; no files or shared state are created.
public sealed class MissingToolFailureClassificationTests
{
    private const string WindowsCargo = "'cargo' is not recognized as an internal or external command,";
    private const string RustupCargo =
        "error: rustup could not choose a version of cargo to run, because one wasn't specified explicitly, and no default is configured.";

    [Theory]
    [InlineData(9009, null, null, "windows-command-not-recognized", "unknown")]
    [InlineData(127, "", "", "posix-command-not-found", "unknown")]
    [InlineData(1, WindowsCargo, null, "windows-command-not-recognized", "cargo")]
    [InlineData(1, null, "\"dotnet\" IS NOT RECOGNIZED AS AN INTERNAL OR EXTERNAL COMMAND", "windows-command-not-recognized", "dotnet")]
    [InlineData(1, "is not recognized as an internal or external command", null, "windows-command-not-recognized", "unknown")]
    [InlineData(1, "bash: cargo: command not found", null, "posix-command-not-found", "cargo")]
    [InlineData(1, null, "zsh: command not found: rustc", "posix-command-not-found", "rustc")]
    [InlineData(1, null, "BASH: Cargo: COMMAND NOT FOUND", "posix-command-not-found", "Cargo")]
    [InlineData(1, "command not found", null, "posix-command-not-found", "unknown")]
    [InlineData(1, "error: linker `link.exe` not found", null, "linker-not-found", "link.exe")]
    [InlineData(1, null, "link.exe not found", "linker-not-found", "link.exe")]
    [InlineData(1, "error: linker `cc` not found", null, "linker-not-found", "cc")]
    [InlineData(1, null, "LLD-LINK.EXE NOT FOUND", "linker-not-found", "LLD-LINK.EXE")]
    [InlineData(1, RustupCargo, null, "toolchain-default-unconfigured", "cargo")]
    [InlineData(null, null, RustupCargo, "toolchain-default-unconfigured", "cargo")]
    [InlineData(1, null, "RUSTUP COULD NOT CHOOSE A VERSION OF `Cargo` TO RUN; NO DEFAULT IS CONFIGURED", "toolchain-default-unconfigured", "Cargo")]
    [InlineData(1, "could not choose a version of `cargo` to run", "no default is configured", "toolchain-default-unconfigured", "cargo")]
    [InlineData(9009, null, "error: linker `cc` not found", "linker-not-found", "cc")]
    [InlineData(127, WindowsCargo, null, "windows-command-not-recognized", "cargo")]
    [InlineData(9009, "command not found\nerror: linker `cc` not found", null, "linker-not-found", "cc")]
    [InlineData(1, WindowsCargo, "bash: rustc: command not found", "windows-command-not-recognized", "cargo")]
    public void ListedSignature_AttachesEnvironmentalCauseAndTool(
        int? exitCode, string? output, string? stderr, string signature, string tool)
    {
        var check = new AcceptanceCheckResult("declared-build: cargo", false, exitCode, output,
            ProcessStderr: stderr);

        var result = AcceptanceFailureCauseAdjudicator.Attach(check);

        Assert.Equal("missing-tool", result.FailureClassification);
        Assert.True(AcceptanceFailureClassifications.IsEnvironmentalApparatus(result.FailureClassification));
        var cause = Assert.IsType<AcceptanceFailureCauseEvidence>(result.FailureCauseEvidence);
        Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, cause.Cause);
        Assert.Equal(check.Name, cause.CheckName);
        Assert.Equal("missing-tool", cause.SourceClassification);
        var fields = cause.Evidence.Split("; ");
        Assert.Contains($"signature={signature}", fields);
        Assert.Contains($"tool={tool}", fields);
    }

    [Theory]
    [InlineData(1, "Program.cs(3,1): error CS0103: The name 'x' does not exist in the current context")]
    [InlineData(1, "could not choose a version of cargo to run")]
    [InlineData(1, "no default is configured")]
    [InlineData(1, "error: file `input.rs` not found")]
    [InlineData(1, "input.rs not found")]
    [InlineData(1, "error LNK1104: cannot open file 'link.exe'")]
    [InlineData(1, "")]
    [InlineData(null, null)]
    public void UnlistedFailure_RemainsUnclassified(int? exitCode, string? output)
    {
        var result = AcceptanceFailureCauseAdjudicator.Attach(
            new AcceptanceCheckResult("declared-build", false, exitCode, output));

        Assert.Null(result.FailureClassification);
        Assert.Null(result.FailureCauseEvidence);
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, true, "Tests.Commands.Fail")]
    [InlineData(false, true, "")]
    [InlineData(true, true, "Tests.Commands.Fail")]
    public void RecordedTestResults_PreventClassification(
        bool hasPaths, bool hasIdentities, string? identity)
    {
        // Any recorded entry excludes the check, even an unusable path or blank identity.
        var check = new AcceptanceCheckResult("test-check", false, 127, "bash: cargo: command not found",
            TestResultPaths: hasPaths ? [""] : null,
            FailingTestIdentities: hasIdentities ? [identity!] : null);

        var result = AcceptanceFailureCauseAdjudicator.Attach(check);

        Assert.Null(result.FailureClassification);
        Assert.Null(result.FailureCauseEvidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void EmptyTestCollectionsAndAbsentClassification_AreEligible(string? classification)
    {
        var check = new AcceptanceCheckResult("declared-build", false, 9009, null,
            TestResultPaths: [], FailingTestIdentities: [], FailureClassification: classification);

        var result = AcceptanceFailureCauseAdjudicator.Attach(check);

        Assert.Equal("missing-tool", result.FailureClassification);
        Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, result.FailureCauseEvidence?.Cause);
    }

    [Theory]
    [InlineData(9009, WindowsCargo)]
    [InlineData(127, "bash: cargo: command not found")]
    [InlineData(1, RustupCargo)]
    public void PassedCheck_DoesNotAcquireClassification(int exitCode, string output)
    {
        var result = AcceptanceFailureCauseAdjudicator.Attach(
            new AcceptanceCheckResult("passed-check", true, exitCode, output));

        Assert.Null(result.FailureClassification);
        Assert.Null(result.FailureCauseEvidence);
    }

    [Theory]
    [InlineData(AcceptanceFailureClassifications.StructuralCoverageFailed)]
    [InlineData("existing-custom-classification")]
    public void ExistingClassification_IsPreserved(string classification)
    {
        var result = AcceptanceFailureCauseAdjudicator.Attach(
            new AcceptanceCheckResult("classified-check", false, 9009, WindowsCargo,
                FailureClassification: classification));

        Assert.Equal(classification, result.FailureClassification);
        Assert.Null(result.FailureCauseEvidence);
    }

    [Fact]
    public void ExistingEnvironmentalEvidence_IsPreserved()
    {
        var check = new AcceptanceCheckResult("classified-check", false, 9009, WindowsCargo,
            FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference,
            FailureCauseEvidence: new AcceptanceFailureCauseEvidence(
                AcceptanceFailureCause.EnvironmentalApparatus, "existing receipt", "classified-check",
                AcceptanceFailureClassifications.GateEnvironmentInterference));

        var result = AcceptanceFailureCauseAdjudicator.Attach(check);

        Assert.Equal(check.FailureClassification, result.FailureClassification);
        Assert.Equal(check.FailureCauseEvidence, result.FailureCauseEvidence);
    }
}
