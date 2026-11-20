using System.Reflection;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleDispatchVerbs
{
    internal const string Conductor = "conductor";
    internal const string Digest = "digest";
    internal const string Metrics = "metrics";
    internal const string Fleet = "fleet";
    internal const string Landings = "landings";
    internal const string Sound = "sound";
    internal const string Settings = "settings";
    internal const string Bell = "bell";
    internal const string Goal = "goal";

    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(typeof(OwnerConsoleDispatchVerbs)
        .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToArray());
}
