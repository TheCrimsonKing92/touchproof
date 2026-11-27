namespace Mcg.AgentOrchestrator.Core;

internal sealed record SourceMemberDeclaration(
    string Name,
    int StartLine,
    int EndLine,
    IReadOnlySet<string>? ReferencedIdentifiers = null);
