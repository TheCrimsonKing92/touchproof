namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Session-local fleet intent state, shared by the controller and its dialog.
internal sealed class OwnerFleetViewKeys
{
    internal bool Expanded { get; private set; }

    internal bool Handle(char key, bool ctrl = false)
    {
        if (OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.FleetView, key, ctrl)?.Command != "expand") return false;
        Expanded = !Expanded;
        return true;
    }

    internal (string Text, OwnerConsoleStructuredText Fields) Render(OwnerFleetViewModel model, int width)
    {
        var fields = new Dictionary<int, OwnerConsoleFieldSnapshot>();
        var text = OwnerFleetDiagram.Render(model, width, fields, Expanded);
        return (text, new(null, fields, FooterHint:
            OwnerConsoleDialogBindings.Label(OwnerConsoleDialogBindings.FleetExpand) + "  " + OwnerConsoleBindings.FieldsHint));
    }
}
