using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleProviderConnectionRows;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed partial class OwnerConsoleProviderConnectionsTab
{
    public int SelectedLine => _selected + (_rows.Length == 0 && _notInUse.Length > 0 ? 1 : 0);
    public IReadOnlyList<string> Lines => RenderLines().ToArray();
    public string StatusText => _listing ? "Reading provider connections…" :
        _loadError ?? _target.Notice ?? (SelectedActiveRow is { } row ? RowStatus(row) :
            SelectedUnusedProfile is { } profile ? CheckStatus(profile) ?? _signIn.StatusText(profile) : null) ?? "";

    public string KeyHint
    {
        get
        {
            if (UnusedHeaderSelected) return FormatUnusedHeaderHint(_unusedExpanded);
            if (SelectedUnusedProfile is { } unused)
                return FormatKeyHint(PrimaryAction(unused, unused), _signIn.DeviceCodeAvailable(unused), unused: true);
            if (SelectedActiveRow is not { } row) return FormatKeyHint(null, deviceCodeAvailable: false);
            return FormatKeyHint(PrimaryAction(Representative(row), SignInProfile(row)),
                _signIn.DeviceCodeAvailable(Representative(row)));
        }
    }

    private IEnumerable<string> RenderLines()
    {
        if (_rows.Length == 0)
            yield return _notInUse.Length > 0 ? "No provider is used by an active agent." :
                _listing ? "Reading configured profiles and readiness evidence…" :
                _loadError ?? (_listed ? "No configured worker profiles." : "Provider connections have not been loaded.");

        for (var index = 0; index < _rows.Length; index++)
        {
            var row = _rows[index];
            var profile = Representative(row);
            var signIn = SignInProfile(row);
            var action = PrimaryAction(profile, signIn);
            yield return Summary(row, _clock, action);
            if (index != _selected) continue;
            foreach (var line in Details(row, action == SignInKey, action == ReturnKey))
                yield return line;
            foreach (var line in OperationDetails(profile, signIn)) yield return line;
        }
        if (_notInUse.Length == 0) yield break;
        yield return $"Not in use ({_notInUse.Length}) {(_unusedExpanded ? "[-]" : "[+]")}";
        if (!_unusedExpanded) yield break;
        for (var index = 0; index < _notInUse.Length; index++)
        {
            var profile = FindProfile(_notInUse[index])!;
            var action = PrimaryAction(profile, profile);
            var evidence = OwnerConsoleProviderRowModel.SelectSignInEvidence(profile.Connection.Report.Observations);
            yield return UnusedSummary(profile.Connection, evidence, _clock, action);
            if (_rows.Length + 1 + index != _selected) continue;
            foreach (var line in UnusedDetails(profile.Connection, evidence, action == SignInKey, action == ReturnKey))
                yield return line;
            foreach (var line in OperationDetails(profile, profile)) yield return line;
        }
    }

    private IEnumerable<string> OperationDetails(OwnerConsoleProviderConnectionState profile, OwnerConsoleProviderConnectionState signIn)
    {
        if (_signIn.StatusText(signIn) is { } interaction) yield return $"  Sign-in: {interaction}";
        if (CheckStatus(profile) is { } check) yield return $"  {check}";
    }

    private string PrimaryAction(OwnerConsoleProviderConnectionState profile, OwnerConsoleProviderConnectionState signIn) =>
        _signIn.CanReportReturn(signIn) ? ReturnKey : _signIn.CanSignIn(profile) ? SignInKey : CheckKey;

    private string? RowStatus(OwnerConsoleProviderRowModel row) =>
        CheckStatus(Representative(row)) ?? _signIn.StatusText(SignInProfile(row));

    private static string? CheckStatus(OwnerConsoleProviderConnectionState profile) =>
        profile.Checking ? "Checking connection…" : profile.OperationText;
}
