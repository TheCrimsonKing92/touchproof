namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Session-local fleet intent state, shared by the controller and its dialog.
internal sealed class OwnerFleetViewKeys
{
    private string? _offeredProvider;
    private string? _requestedProvider;

    internal bool Expanded { get; private set; }
    internal bool SignInRequested => _requestedProvider is not null;

    internal bool Handle(char key, bool ctrl = false)
    {
        switch (OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.FleetView, key, ctrl)?.Command)
        {
            case "expand":
                Expanded = !Expanded;
                return true;
            case OwnerConsoleBindings.SettingsCommand when _offeredProvider is not null:
                _requestedProvider = _offeredProvider;
                return true;
            default:
                return false;
        }
    }

    internal string? TakeSignInRequest()
    {
        var provider = _requestedProvider;
        _offeredProvider = null;
        _requestedProvider = null;
        return provider;
    }

    internal (string Text, OwnerConsoleStructuredText Fields) Render(OwnerFleetViewModel model, int width)
    {
        var fields = new Dictionary<int, OwnerConsoleFieldSnapshot>();
        var offer = OwnerFleetDiagram.SignInOffer(model);
        var text = OwnerFleetDiagram.Render(model, width, fields, Expanded, offer);
        _offeredProvider = offer?.Provider;
        var footer = OwnerConsoleDialogBindings.Label(OwnerConsoleDialogBindings.FleetExpand);
        if (offer is not null) footer += "  " + OwnerConsoleDialogBindings.Label(OwnerConsoleDialogBindings.FleetSignIn);
        return (text, new(null, fields, FooterHint: footer + "  " + OwnerConsoleBindings.FieldsHint));
    }
}
