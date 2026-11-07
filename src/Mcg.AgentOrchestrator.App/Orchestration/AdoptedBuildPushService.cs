using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Publishes one scanned commit to one existing branch. Never derives the candidate from local HEAD.
internal sealed class AdoptedBuildPushService(
    string repository, string genericPatterns, string ownerPatterns,
    Action<string> append, Action<string> attention,
    Func<string, string[], GitCli.GitResult>? git = null)
{
    private readonly object _gate = new();

    internal bool Push(string commit, string remote = "origin", string branch = "main")
    {
        lock (_gate) return PushCore(commit, remote, branch);
    }

    private bool PushCore(string candidate, string remote, string branch)
    {
        var commit = "unresolved";
        var destinationName = "configured-remote";
        var branchName = "configured-branch";
        var phase = "validation-failed";
        GitCli.GitResult Run(params string[] args)
        {
            var result = (git ?? LandingExecutor.GitRunner)(repository, args);
            if (!result.ProcessStarted || result.DrainTimedOut)
                throw new InvalidOperationException("Incomplete git result.");
            return result;
        }
        bool Hold(string reason, IReadOnlyList<AdoptedBuildLeakScanner.Location>? matches = null)
        {
            append($"ADOPTED_BUILD_PUSH_HELD commit={commit} remote={destinationName} reason={reason}");
            var locations = matches is null ? "" : string.Join("\n", matches.Select(hit => $"{hit.File}:{hit.Line}"));
            attention($"Adopted build publication held. commit={commit} remote={destinationName} reason={reason}\n" +
                locations + $"\nNext: push-adopted {commit} --remote {destinationName} --branch {branchName}");
            return false;
        }

        try
        {
            // Names only: URLs, option-looking values, refspecs and revision expressions are not CLI inputs.
            if (!Regex.IsMatch(remote, @"^[A-Za-z0-9][A-Za-z0-9._-]*$") ||
                branch.StartsWith('-') || branch.Any(char.IsWhiteSpace) ||
                Run("check-ref-format", $"refs/heads/{branch}").ExitCode != 0 ||
                !Regex.IsMatch(candidate, @"^[A-Za-z0-9][A-Za-z0-9._/-]*$"))
                return Hold("invalid-target");
            destinationName = remote;
            branchName = branch;
            var resolved = Run("rev-parse", "--verify", "--end-of-options", candidate + "^{commit}");
            if (resolved.ExitCode != 0 || !HomeBuildAdoptionDetector.IsCommit(resolved.Output.Trim()))
                return Hold("invalid-commit");
            commit = resolved.Output.Trim();
            if (!File.Exists(ownerPatterns)) return Hold("owner-patterns-missing");
            phase = "patterns-invalid";
            var scanner = new AdoptedBuildLeakScanner(genericPatterns, ownerPatterns);
            if (!scanner.OwnerPatternsPresent) return Hold("owner-patterns-missing");

            phase = "destination-invalid";
            var urls = Run("remote", "get-url", "--push", "--all", remote);
            var destinations = urls.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (urls.ExitCode != 0 || destinations.Length != 1 || destinations[0].StartsWith('-'))
                return Hold(phase);
            var destination = destinations[0];
            phase = "fetch-failed";
            if (Run("fetch", "--no-tags", "--no-write-fetch-head", "--", destination, $"refs/heads/{branch}").ExitCode != 0)
                return Hold(phase);
            // Query this destination directly; concurrent fetches must not replace our comparison tip.
            var fetched = Run("ls-remote", "--refs", "--", destination, $"refs/heads/{branch}");
            var fields = fetched.Output.Trim().Split('\t');
            if (fetched.ExitCode != 0 || fields.Length != 2 || fields[1] != $"refs/heads/{branch}" ||
                !HomeBuildAdoptionDetector.IsCommit(fields[0]))
                return Hold(phase);
            var tip = fields[0];
            var ancestor = Run("merge-base", "--is-ancestor", tip, commit);
            if (ancestor.ExitCode != 0) return Hold(ancestor.ExitCode == 1 ? "non-fast-forward" : "ancestry-failed");

            phase = "diff-failed";
            var diff = Run("-c", "core.quotePath=false", "diff", "--unified=0", "--no-color",
                "--no-ext-diff", "--no-textconv", "--no-renames", tip, commit, "--");
            if (diff.ExitCode != 0) return Hold(phase);
            phase = "scan-invalid";
            var scan = scanner.Scan(diff.Output);
            if (scan.Matches.Count != 0) return Hold("scan-match", scan.Matches);

            phase = "push-failed";
            if (Run("-c", "push.followTags=false", "-c", "push.default=nothing", "-c", "push.recurseSubmodules=no",
                "push", "--", destination, $"{commit}:refs/heads/{branch}").ExitCode != 0) return Hold(phase);
            phase = "push-unverified";
            var observed = Run("ls-remote", "--refs", "--", destination, $"refs/heads/{branch}");
            if (observed.ExitCode != 0 || observed.Output.Trim() != $"{commit}\trefs/heads/{branch}")
                return Hold(phase);
            append($"ADOPTED_BUILD_PUSHED commit={commit} remote={remote} added_lines={scan.AddedLines}");
            return true;
        }
        catch (RegexMatchTimeoutException) { return Hold("scan-timeout"); }
        catch (Exception) { return Hold(phase); } // Never echo git stderr, pattern contents or raw exception text.
    }

    internal static AdoptedBuildPushService CreateDefault(string repository, Action<string> append)
    {
        return new(repository, Path.Combine(repository, AdoptedBuildLeakScanner.GenericPatternPath),
            AdoptedBuildLeakScanner.OwnerPatternPath(OrchestratorDataRoot.Resolve().RootDirectory), append,
            body => CollaborationItemStore.ForDirectory(Path.Combine(repository, ".orchestrator"))
                .RaiseAsync(CollaborationItemType.Decision, null, "Adopted build push held", body,
                    "adopted-build-push-held:" + body.Split('\n')[0]).GetAwaiter().GetResult());
    }
}
