namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns bell syntax and effective tier state; the controller serializes it with detection.
internal sealed class OwnerConsoleBellCommand(OwnerConsoleSoundCommand? sound)
{
    private OwnerConsoleBellTiers _sessionTiers = OwnerConsoleBellTiers.All;
    internal OwnerConsoleBellTiers Tiers => sound?.Settings.Current.BellTiers ?? _sessionTiers;

    internal void Run(string[] arguments, OwnerConsoleCapturedOutput output)
    {
        var parts = arguments.Select(part => part.ToLowerInvariant()).ToArray();
        OwnerConsoleBellTiers tiers;
        if (parts.Length == 1 && parts[0] is "on" or "off")
            tiers = parts[0] == "on" ? OwnerConsoleBellTiers.All : OwnerConsoleBellTiers.None;
        else if (parts.Length == 2 && parts[0] is "calm" or "attention" or "alarm" or "decision" &&
                 parts[1] is "on" or "off")
        {
            var tier = parts[0] switch
            {
                "calm" => OwnerConsoleNotificationTier.Calm,
                "attention" => OwnerConsoleNotificationTier.Attention,
                "decision" => OwnerConsoleNotificationTier.Decision,
                _ => OwnerConsoleNotificationTier.Alarm
            };
            tiers = Tiers.With(tier, parts[1] == "on");
        }
        else
        {
            output.WriteLine("usage: bell on|off | bell <calm|attention|alarm|decision> on|off");
            return;
        }

        if (sound is null) _sessionTiers = tiers;
        else if (!sound.SetBellTiers(tiers, output)) return;
        output.WriteLine("bell " + string.Join(' ', parts));
    }
}
