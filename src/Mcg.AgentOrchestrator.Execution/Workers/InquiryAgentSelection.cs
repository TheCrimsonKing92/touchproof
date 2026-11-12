using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Selects the answering agent; the completed dispatch remains the subject of the inquiry.
public sealed record InquiryAgentSelection(ProviderKind ProviderKind, string? ModelName, string? ReasoningEffort, string WorkerName);
