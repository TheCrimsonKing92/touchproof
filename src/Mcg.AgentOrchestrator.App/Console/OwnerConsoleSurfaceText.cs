using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleBindings;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleSurfaceText
{
    internal enum TextKind { TabTitle, TabHelpKeys, TabFooter, PaneFooter }

    internal sealed record Entry(TextKind Kind, string Group, string Variant, string Text);

    // The fingerprint uses this order. Keep tabs in strip order and include variants even when their text matches.
    internal static IReadOnlyList<Entry> All { get; } = Array.AsReadOnly<Entry>(
    [
        .. Tab(OwnerConsoleAlertsSettingsTab.TabTitle, OwnerConsoleAlertsSettingsTab.HelpKeys,
            OwnerConsoleAlertsSettingsTab.BrowsingKeyHint, OwnerConsoleAlertsSettingsTab.EditingKeyHint),
        .. Tab(OwnerConsoleAgentsSettingsTab.TabTitle, OwnerConsoleAgentsSettingsTab.HelpKeys,
            OwnerConsoleAgentsSettingsTab.BrowsingKeyHint, OwnerConsoleAgentsSettingsTab.EditingKeyHint),
        .. Tab(OwnerConsolePolicySettingsTab.TabTitle, OwnerConsolePolicySettingsTab.HelpKeys,
            OwnerConsolePolicySettingsTab.BrowsingKeyHint, OwnerConsolePolicySettingsTab.EditingKeyHint),
        .. Tab(OwnerConsoleExecutorsSettingsTab.TabTitle, OwnerConsoleExecutorsSettingsTab.HelpKeys,
            OwnerConsoleExecutorsSettingsTab.BrowsingKeyHint),
        .. Tab(OwnerConsoleProjectsSettingsTab.TabTitle, OwnerConsoleProjectsSettingsTab.HelpKeys,
            OwnerConsoleProjectsSettingsTab.BrowsingKeyHint, OwnerConsoleProjectsSettingsTab.EditingKeyHint),
        .. ProviderConnections(),
        new(TextKind.PaneFooter, nameof(OwnerConsolePane.Decisions), "No answer available",
            PaneHint(OwnerConsolePane.Decisions, answerable: false)),
        new(TextKind.PaneFooter, nameof(OwnerConsolePane.Decisions), "Answer available",
            PaneHint(OwnerConsolePane.Decisions, answerable: true)),
        new(TextKind.PaneFooter, nameof(OwnerConsolePane.Board), "Default", PaneHint(OwnerConsolePane.Board)),
        new(TextKind.PaneFooter, nameof(OwnerConsolePane.Activity), "Inspecting",
            PaneHint(OwnerConsolePane.Activity, following: false, signIn: false)),
        new(TextKind.PaneFooter, nameof(OwnerConsolePane.Activity), "Inspecting with sign-in",
            PaneHint(OwnerConsolePane.Activity, following: false, signIn: true)),
        new(TextKind.PaneFooter, nameof(OwnerConsolePane.Activity), "Following",
            PaneHint(OwnerConsolePane.Activity, following: true, signIn: false)),
        new(TextKind.PaneFooter, nameof(OwnerConsolePane.Activity), "Following with sign-in",
            PaneHint(OwnerConsolePane.Activity, following: true, signIn: true))
    ]);

    private static IEnumerable<Entry> Tab(string title, string helpKeys, string browsing, string? editing = null)
    {
        yield return new(TextKind.TabTitle, title, "Title", title);
        yield return new(TextKind.TabHelpKeys, title, "Help keys", helpKeys);
        if (editing is not null)
            yield return new(TextKind.TabFooter, title, "Editing", editing);
        yield return new(TextKind.TabFooter, title, "Browsing", browsing);
    }

    private static IEnumerable<Entry> ProviderConnections()
    {
        const string title = OwnerConsoleProviderConnectionsTab.TabTitle;
        yield return new(TextKind.TabTitle, title, "Title", title);
        yield return new(TextKind.TabHelpKeys, title, "Help keys", OwnerConsoleProviderConnectionsTab.HelpKeys);
        foreach (var action in HintActions)
        {
            var variant = action ?? "No action";
            yield return new(TextKind.TabFooter, title, variant, FormatKeyHint(action, deviceCodeAvailable: false));
            yield return new(TextKind.TabFooter, title, $"{variant} with device code", FormatKeyHint(action, deviceCodeAvailable: true));
        }
    }
}
