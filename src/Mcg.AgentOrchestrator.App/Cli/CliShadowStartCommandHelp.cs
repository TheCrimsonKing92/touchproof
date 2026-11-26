namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliShadowStartCommandHelp
{
    internal const string Usage = "Usage: shadow-start --source <state-directory> --scratch <new-directory> [--scratch-retention <retain|disposable>] [--candidate <executable>] [--candidate-arg=<value> ...] [--startup-ceiling-seconds <n>] [--budget-seconds <n>]";
    internal const string Description = "Check candidate conductor startup against SQLite backups in a scratch workspace.";
    internal static IReadOnlyList<string> Flags { get; } =
        ["--source", "--scratch", "--scratch-retention", "--candidate", "--candidate-arg", "--startup-ceiling-seconds", "--budget-seconds", "--help", "-h"];
    internal static IReadOnlyList<string> DetailLines { get; } =
    [
        "Use absolute source and scratch paths. Scratch must be new and outside the source directory.",
        "The startup ceiling defaults to 60 seconds and the total child budget defaults to 120 seconds.",
        "The candidate defaults to this application. Repeat --candidate-arg=<value> for candidate arguments.",
        "Scratch retention defaults to retain. Select disposable to reclaim store copies after verified release.",
        "Disposable runs preserve measurement and cleanup receipts. A passing measurement with deferred cleanup exits 2."
    ];
}
