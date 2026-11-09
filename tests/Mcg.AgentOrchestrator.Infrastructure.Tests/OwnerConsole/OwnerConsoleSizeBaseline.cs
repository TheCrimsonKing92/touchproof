using System.Collections.Frozen;

// Parallel-safe: immutable baseline data; no shared mutable test state.
internal static class OwnerConsoleSizeBaseline
{
    // File.ReadAllLines counts at the candidate head. Lower/remove rows as files shrink.
    // Raising a ceiling requires a one-line justification comment immediately above its row.
    internal static IReadOnlyDictionary<string, int> Files { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["OwnerConsoleScreenController.cs"] = 283,
        ["OwnerGoalChangeSummaryQuery.cs"] = 152,
        // Relocated section builders preserve existing text and ordering; individual methods stay below 60 lines.
        ["OwnerGoalSummarySectionReader.cs"] = 277
    }.AsReadOnly();

    // Syntax-node inclusive line spans at HEAD, including constructors and local functions.
    // Keys include enclosing types/methods; overloads share a key and are measured individually.
    // Remove a row once every overload is at most 60 lines. Additions require justification above the row.
    internal static IReadOnlySet<string> Methods { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "OwnerActivityDetailPresentation.cs::OwnerActivityDetailPresentation.Build",
        "OwnerActivityDiagnosticParser.cs::OwnerActivityDiagnosticParser.Parse",
        "OwnerActivityNarrator.cs::OwnerActivityNarrator.Narrate",
        "OwnerConsoleBindingRunner.cs::OwnerConsoleBindingRunner.RunAsync",
        "OwnerConsoleFullScreenHost.cs::OwnerConsoleFullScreenHost.RunCoreAsync",
        "OwnerConsoleGateLayout.cs::OwnerConsoleGateLayout.Lines",
        "OwnerConsoleJsonlTail.cs::OwnerConsoleJsonlTail.Select",
        "OwnerConsoleKeyRouter.cs::OwnerConsoleKeyRouter.RouteAsync",
        "OwnerConsoleLineFitter.cs::OwnerConsoleLineFitter.Fit",
        "OwnerConsolePaneKeys.cs::OwnerConsolePaneKeys.HandleAsync",
        "OwnerConsoleScreenController.cs::OwnerConsoleScreenController.RunCommandAsync",
        "OwnerConsoleStageDescriber.cs::OwnerConsoleStageDescriber.Describe",
        "OwnerConsoleViewModelBuilder.cs::OwnerConsoleViewModelBuilder.BuildBoardAsync",
        "OwnerExperimentDecisionFlow.cs::OwnerExperimentDecisionFlow.RunAsync",
        "OwnerFleetDiagram.cs::OwnerFleetDiagram.Render",
        "OwnerQuestionReadModel.cs::OwnerQuestionReadModel.ReadAsync",
        "OwnerQuestionResolutionReader.cs::OwnerQuestionResolutionReader.ListForGoalAsync"
    }.ToFrozenSet(StringComparer.Ordinal);
}
