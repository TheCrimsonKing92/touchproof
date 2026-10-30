using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class OwnerAnsweredEnvironmentTools
{
    public const string Prefix = "environment/tools/";
    public static bool IsKey(string key) => key.StartsWith(Prefix, StringComparison.Ordinal) && key.Length > Prefix.Length;

    public static string Validate(string key, string value)
    {
        var name = key[Prefix.Length..];
        if (name.EndsWith("/pattern", StringComparison.Ordinal))
        {
            if (name.Length == "/pattern".Length) throw new ArgumentException("A tool name is required.");
            try { _ = new Regex(value, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            { throw new ArgumentException("Invalid or unsupported output pattern.", ex); }
            return value;
        }
        if (name.EndsWith("/variables", StringComparison.Ordinal))
        {
            if (value.Split(',').Any(variable => string.IsNullOrWhiteSpace(variable) || variable.Contains('=')))
                throw new ArgumentException("Tool variables require comma-separated environment variable names.");
            return value;
        }
        if (name.Contains('/')) throw new ArgumentException("Use environment/tools/<name>[/pattern|/variables].");
        return value == "none" ? value : string.Join(" ", DeclaredCommandLine.Parse(value));
    }

    public static EnvironmentContract Overlay(EnvironmentContract contract, AppliedOwnerAnswers? answers)
    {
        var tools = contract.Tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        var facts = (answers?.AnsweredFacts ?? []).Where(fact => IsKey(fact.FactKey))
            .GroupBy(fact => fact.FactKey).ToDictionary(group => group.Key, group => group.Last().Fact.Value);
        foreach (var (key, value) in facts.Where(pair => !pair.Key[Prefix.Length..].Contains('/')))
        {
            var name = key[Prefix.Length..];
            if (value == "none") { tools.Remove(name); continue; }
            var tokens = DeclaredCommandLine.Parse(value);
            tools[name] = new(name, tokens[0], tokens[1..], Source: "owner-answer:" + key);
        }
        foreach (var name in tools.Keys.ToArray())
        {
            var tool = tools[name];
            if (facts.TryGetValue(Prefix + name + "/pattern", out var pattern)) tool = tool with { OutputPattern = pattern };
            if (facts.TryGetValue(Prefix + name + "/variables", out var variables)) tool = tool with { Variables = variables.Split(',', StringSplitOptions.TrimEntries) };
            tools[name] = tool;
        }
        return new(1, tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray());
    }
}
