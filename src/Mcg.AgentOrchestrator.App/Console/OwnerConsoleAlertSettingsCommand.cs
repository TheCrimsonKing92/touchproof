using System.Text.Json.Nodes;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Publishes alert settings only after the sound command's shared atomic writer succeeds.
internal sealed class OwnerConsoleAlertSettingsCommand(OwnerConsoleNotificationSettingsProvider settings,
    Func<string, JsonNode?, string, OwnerConsoleCapturedOutput, bool> write)
{
    internal bool SetThreshold(string key, int value, OwnerConsoleCapturedOutput output)
    {
        if (value <= 0 || key is not ("attentionThresholdMinutes" or "staleTickThresholdMinutes" or "calmCoalesceSeconds"))
        {
            output.WriteLine("Enter a positive whole number within the supported range.");
            return false;
        }
        if (!write(key, JsonValue.Create(value), "threshold save failed", output)) return false;
        var snapshot = settings.Current;
        settings.Publish(key switch
        {
            "attentionThresholdMinutes" => snapshot with { AttentionThreshold = TimeSpan.FromMinutes(value) },
            "staleTickThresholdMinutes" => snapshot with { StaleTickThreshold = TimeSpan.FromMinutes(value) },
            _ => snapshot with { CalmCoalesce = TimeSpan.FromSeconds(value) }
        });
        output.WriteLine($"saved {key}: {value}");
        return true;
    }

    internal bool SetBellTiers(OwnerConsoleBellTiers tiers, OwnerConsoleCapturedOutput output)
    {
        var muted = new JsonArray();
        if (!tiers.Calm) muted.Add("calm");
        if (!tiers.Attention) muted.Add("attention");
        if (!tiers.Alarm) muted.Add("alarm");
        if (!tiers.Decision) muted.Add("decision");
        if (!write("mutedTiers", muted.Count == 0 ? null : muted, "bell save failed", output)) return false;
        settings.Publish(settings.Current with { BellTiers = tiers });
        return true;
    }
}
