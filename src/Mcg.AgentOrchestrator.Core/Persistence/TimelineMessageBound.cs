using System.Diagnostics.CodeAnalysis;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>Bounds persisted timeline text in UTF-16 code units, including its marker.</summary>
public static class TimelineMessageBound
{
    public const int CharacterLimit = 16_000;

    [return: NotNullIfNotNull(nameof(message))]
    public static string? Bound(string? message, string goalId, string? taskId)
    {
        if (message is null || message.Length <= CharacterLimit)
            return message;

        var goalReference = goalId ?? "unavailable";
        var taskReference = taskId ?? "unavailable";
        string Marker(int count) => FormattableString.Invariant(
            $"\n[omitted {count} characters; goal id: {goalReference}; task id: {taskReference}; see task dispatch log]\n");

        var omitted = message.Length;
        var marker = Marker(omitted);
        // Legacy identifiers are arbitrary strings. If they cannot fit, preserve the text bound
        // and label the unavailable reference rather than silently abbreviating an identity.
        while (marker.Length > CharacterLimit - 4)
        {
            if (goalReference.Length >= taskReference.Length)
                goalReference = "unavailable (id exceeds message budget)";
            else
                taskReference = "unavailable (id exceeds message budget)";
            marker = Marker(omitted);
        }

        while (true)
        {
            var payload = CharacterLimit - marker.Length;
            var headLength = (payload + 1) / 2;
            var tailStart = message.Length - payload / 2;
            if (char.IsHighSurrogate(message[headLength - 1]) && char.IsLowSurrogate(message[headLength]))
                headLength--;
            if (char.IsLowSurrogate(message[tailStart]) && char.IsHighSurrogate(message[tailStart - 1]))
                tailStart++;

            var actualOmitted = tailStart - headLength;
            if (actualOmitted == omitted)
                return string.Concat(message.AsSpan(0, headLength), marker.AsSpan(), message.AsSpan(tailStart));

            // Starting from the full length makes the count decrease monotonically; a shorter
            // count can only free payload space. Recompute after surrogate-boundary adjustments.
            omitted = actualOmitted;
            marker = Marker(omitted);
        }
    }
}
