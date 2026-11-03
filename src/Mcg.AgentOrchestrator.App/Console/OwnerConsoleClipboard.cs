using Terminal.Gui.App;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleClipboard(Func<string, bool> write)
{
    internal static OwnerConsoleClipboard For(IApplication app) => new(value =>
        app.Clipboard is { IsSupported: true } clipboard && clipboard.TrySetClipboardData(value));

    internal (bool Copied, string Message) Copy(OwnerConsoleField? field)
    {
        if (field is null) return (false, "No selected field.");
        if (field.Value is null) return (false, field.Label + " is unavailable.");
        try
        {
            return write(field.Value) ? (true, "Copied " + field.Label + ".") :
                (false, "Copy failed: clipboard unavailable or write refused.");
        }
        catch (Exception ex) { return (false, "Copy failed: " + ex.Message); }
    }
}
