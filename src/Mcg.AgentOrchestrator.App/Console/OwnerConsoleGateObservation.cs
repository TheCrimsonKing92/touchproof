using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record Observation(OwnerConductEvent Event, Dictionary<string, string> Fields, int Order)
{
    internal string Get(string key) => Fields.GetValueOrDefault(key, "");
    internal double Number(string key) => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) && value >= 0 ? value : 0;
}
