namespace Mcg.AgentOrchestrator.Core;

// An attributed request to append a goal-level operator note on the conductor's tick.
public sealed record GoalNoteOperatorIntentPayload(string Text);
