using System.Data;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleKeySurface
{
    OwnerConsoleGateDialog GateView { get; }
    OwnerConsoleSettingsView? SettingsView => null;
    OwnerConsoleEpicDialog EpicView { get; }
    TextField CommandLine { get; }
    bool EditingCommand { get; set; }
    string? CompletionHint { set; }
    View? CommandReturnFocus { get; set; }
    int HintWidth { get; }
    OwnerConsolePane FocusedPane { get; }
    TableView BoardPane { get; }
    ListView DecisionsPane { get; }
    ListView ActivityPane { get; }
    DataTable BoardTable { get; }
    int SelectedBoardIndex { get; }
    OwnerConsoleFieldSnapshot? SelectedBoardFields { get; }
    string? SelectedFieldKey { get; set; }
    bool CopySelectionValid { set; }
    string? SelectedGoalId { get; }
    string? SelectedProject { get; }
    OwnerConsoleViewModel? ArrangedBoard { get; }
    bool Rendering { set; }
    IReadOnlyList<string> ActivityLines { get; }
    OwnerConsoleActivityPane ActivityModel { get; }
    OwnerConsoleFieldSnapshot? DecisionFields { get; }
    bool ActionRunning { get; }
    View PaneView(OwnerConsolePane pane);
    void FocusPane(OwnerConsolePane pane);
    void RenderBoard();
    void SelectBoard(int index, bool refresh = false, int direction = 0, bool allowHeader = false);
    int BoardBodyRows();
    bool RefuseSelectedBoardRow();
    void FollowLatest();
    void ShowNotice(string message, OwnerConsoleNoticeSeverity severity, OwnerConsoleNoticeSource source);
    void StartAction();
    void RenderHints();
    void RenderOverflow();
    Task ActAsync(Func<CancellationToken, Task> action);
    Task RunBindingAsync(OwnerConsoleBinding binding, string? argument = null, bool fromKey = false);
}
