using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A tab owns its content and actions; the frame owns switching and closing.
internal interface IOwnerConsoleSettingsTab
{
    string Title { get; }
    IReadOnlyList<string> Lines { get; }
    string KeyHint { get; }
    string StatusText { get; }
    bool Editing { get; }
    int SelectedLine { get; }
    void HandleKey(Key key);
    void Refresh();
}
