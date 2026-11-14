namespace Mcg.AgentOrchestrator.Core;

/// <summary>A quoted guidance item, retained with its heading and source rather than reinterpreted.</summary>
public sealed record ProjectConvention
{
    public string DocumentPath { get; }
    public string Heading { get; }
    public string Rule { get; }
    public ProjectFact<string> Evidence { get; }

    public ProjectConvention(string documentPath, string heading, string rule, ProjectFact<string> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        ArgumentNullException.ThrowIfNull(evidence);
        DocumentPath = documentPath;
        Heading = heading;
        Rule = rule;
        Evidence = evidence;
    }
}
