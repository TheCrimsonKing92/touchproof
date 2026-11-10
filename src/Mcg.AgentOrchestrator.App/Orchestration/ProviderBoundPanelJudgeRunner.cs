using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Seat identity is stable; only the resolved binding determines execution provider.
internal class ProviderBoundPanelJudgeRunner(ModelFunctionCatalog catalog, string purpose, string judge,
    Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>>? process = null)
    : ConductorPanelJudgeRunner(catalog, purpose, process)
{
    public override string Judge => judge;
    protected override string Command(string directory) =>
        Binding.WorkerProfileName == "codex-cli" ? CodexCommand(directory) : ClaudeCommand(directory);
    protected override (string Answer, string? Usage, bool ProviderFault) Decode(string stdout, string stderr) =>
        Binding.WorkerProfileName == "codex-cli" ? DecodeCodex(stdout, stderr) : DecodeClaude(stdout, stderr);

    private string CodexCommand(string directory) =>
        $"codex exec --skip-git-repo-check --model {Quote(Binding.Alias!)}" +
        (string.IsNullOrWhiteSpace(Binding.Effort) ? "" : $" -c {Quote("model_reasoning_effort=\"" + Binding.Effort + "\"")}") +
        $" --sandbox read-only --cd {Quote(directory)} -";
    private static (string Answer, string? Usage, bool ProviderFault) DecodeCodex(string stdout, string stderr)
    {
        var match = Regex.Match(stderr, @"(?im)^tokens used\s*\r?\n([\d,]+)\s*$");
        var usage = match.Success && long.TryParse(match.Groups[1].Value.Replace(",", ""), out var tokens)
            ? JsonSerializer.Serialize(new { total_tokens = tokens }) : null;
        return (stdout, usage, false);
    }

    private string ClaudeCommand(string directory) =>
        $"claude -p --model {Quote(Binding.Alias!)} --restricted --tools '' --no-session-persistence" +
        $" --output-format json --json-schema {Quote(PanelV0Contract.JsonSchema)}";
    private static (string Answer, string? Usage, bool ProviderFault) DecodeClaude(string stdout, string stderr)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() != "result")
                return (stdout, null, false);
            var usage = root.TryGetProperty("usage", out var used) ? used.GetRawText() : null;
            var fault = root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
            var answer = root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object
                ? structured.GetRawText() : root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
                    ? result.GetString()! : stdout;
            return (answer, usage, fault);
        }
        catch (JsonException) { return (stdout, null, false); }
    }
}
