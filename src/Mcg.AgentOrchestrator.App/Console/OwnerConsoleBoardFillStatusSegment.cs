using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleBoardFillStatusSegment
{
    internal static string? Text(ConductorBoardFillMode? mode) => mode switch
    {
        ConductorBoardFillMode.Off => "board fill off",
        ConductorBoardFillMode.Shadow => "board fill shadow",
        _ => null
    };
}
