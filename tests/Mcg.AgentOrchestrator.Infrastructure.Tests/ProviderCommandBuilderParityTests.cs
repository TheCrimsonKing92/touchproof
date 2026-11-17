using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProviderCommandBuilderParityTests
{
    // Full tests-directory Contains/DoesNotContain command-shape audit:
    // built-in/default dispatch assertions are in WorkerProfileTests, WorkerDispatchTestsDispatchPreparation,
    // WorkerDispatchTestsModelSelection, WorkerDispatchTestsModelSelectionEnvMutation, and
    // WorkerDispatchTestsSandboxLowIntegrity. InquiryDispatcherTests covers the separate inquiry path.
    // AdvanceLoopTests, ProgressiveReviewGlanceTests, ProgressiveReviewSteeringTests,
    // RealWorkerProcessGuardTests, StatePersistenceAndPerformanceTests, WorkerDispatchJobAccountingTests,
    // and Core RepositoryChangeClassifierTests use test-local commands/profiles rather than built-in dispatch.
    public static IEnumerable<object[]> BuiltInParityCases()
    {
        foreach (var providerKind in new[]
                 {
                     ProviderKind.OpenAICodexCli,
                     ProviderKind.OpenAICodexLuna,
                     ProviderKind.OpenAICodexOssCli,
                     ProviderKind.AnthropicClaudeCli,
                     ProviderKind.OllamaQwenCodeCli
                 })
        {
            foreach (var isWriteCapable in new[] { false, true })
            {
                foreach (var osSandbox in new[] { false, true })
                {
                    yield return [providerKind, isWriteCapable, osSandbox, "populated"];
                    yield return [providerKind, isWriteCapable, osSandbox, "blank"];
                    yield return [providerKind, isWriteCapable, osSandbox, "null"];
                }
            }
        }
    }

    [Xunit.Theory(DisplayName = "ProviderCommandBuilder_matches_legacy_template_for_each_builtin_provider")]
    [Xunit.MemberData(nameof(BuiltInParityCases))]
    public void ProviderCommandBuilderMatchesLegacyTemplateForEachBuiltInProvider(
        ProviderKind providerKind,
        bool isWriteCapable,
        bool osSandbox,
        string valueState)
    {
        var provider = WorkerProviderCatalog.Default().Resolve(providerKind);
        var profile = WorkerProfileCatalog.Default().GetRequired(provider.ProfileName);
        string? modelAlias = providerKind == ProviderKind.OllamaQwenCodeCli
            ? "qwen3:8b"
            : providerKind == ProviderKind.AnthropicClaudeCli
                ? "claude-sonnet-4-6"
                : AgentCatalog.OpenAiSubscriptionModelAlias;
        string? reasoningEffort = "high";
        string? permissionMode = isWriteCapable ? "bypassPermissions" : "plan";
        string? sandboxMode = osSandbox
            ? "danger-full-access"
            : isWriteCapable ? "workspace-write" : "read-only";
        string? workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "provider command parity",
            "worker's repo");
        if (valueState == "blank")
        {
            modelAlias = string.Empty;
            reasoningEffort = string.Empty;
            permissionMode = string.Empty;
            sandboxMode = string.Empty;
            workingDirectory = string.Empty;
        }
        else if (valueState == "null")
        {
            modelAlias = null;
            reasoningEffort = null;
            permissionMode = null;
            sandboxMode = null;
            workingDirectory = null;
        }

        var promptRoot = Path.Combine(
            Path.GetTempPath(),
            $"provider-command-builder-{Guid.NewGuid():N}");
        string? openaiBaseUrl = LlamaCppDefaults.BuildOpenAiCompatibleBaseUrl(LlamaCppDefaults.BaseUrl);
        string? openaiApiKey = LlamaCppDefaults.OpenAiApiKey;
        string? approvalMode = isWriteCapable ? "yolo" : "plan";
        if (valueState == "blank")
        {
            openaiBaseUrl = string.Empty;
            openaiApiKey = string.Empty;
            approvalMode = string.Empty;
        }
        else if (valueState == "null")
        {
            openaiBaseUrl = null;
            openaiApiKey = null;
            approvalMode = null;
        }

        var variables = new Dictionary<string, string?>
        {
            ["subscriptionModelName"] = modelAlias,
            ["subscriptionReasoningEffort"] = reasoningEffort,
            ["permissionMode"] = permissionMode,
            ["sandboxMode"] = sandboxMode,
            ["workingDirectory"] = workingDirectory,
            ["openaiBaseUrl"] = openaiBaseUrl,
            ["openaiApiKey"] = openaiApiKey,
            ["approvalMode"] = approvalMode
        };

        try
        {
            var legacy = WorkerCommandTemplate.Prepare(
                new TaskBrief(
                    new GoalId("goal123456789"),
                    new TaskId("task123456789"),
                    isWriteCapable ? AgentRole.Developer : AgentRole.Researcher,
                    "Provider command parity",
                    "Parity prompt."),
                profile.Name,
                profile.CommandTemplate,
                promptRoot,
                variables,
                DateTimeOffset.Parse("2026-07-28T12:00:00Z"));
            var routedTemplate = WorkerProfileDispatcher.BuildDispatchCommandTemplate(
                profile,
                providerKind,
                variables);
            var routed = routedTemplate.Replace(
                "{promptPath}",
                Quote(legacy.PromptPath),
                StringComparison.OrdinalIgnoreCase);

            if (providerKind == ProviderKind.AnthropicClaudeCli &&
                !isWriteCapable &&
                valueState == "populated")
            {
                Assert.Contains("--permission-mode 'dontAsk'", routed, StringComparison.Ordinal);
                Assert.Contains("--restricted", routed, StringComparison.Ordinal);
                Assert.Contains(
                    "--tools 'Read,Glob,Grep,Bash,WebFetch,WebSearch,TodoWrite'",
                    routed,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "--allowed-tools 'Read,Glob,Grep,Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git status *),Bash(git merge-base *),Bash(git rev-parse *),Bash(git blame *),Bash(git ls-files *),Bash(git branch *),Bash(git cat-file *),Bash(rg *),WebFetch,WebSearch,TodoWrite'",
                    routed,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "--disallowed-tools 'Edit,Write,NotebookEdit'",
                    routed,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("--allowed-tools 'Read,Glob,Grep,Bash,", routed, StringComparison.Ordinal);
                Assert.DoesNotContain(",Task", routed, StringComparison.Ordinal);
                Assert.DoesNotContain("--permission-mode 'plan'", routed, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(legacy.Command, routed);
            }
        }
        finally
        {
            if (Directory.Exists(promptRoot))
            {
                Directory.Delete(promptRoot, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ProviderCommandBuilder_qwen_reads_prompt_from_stdin")]
    public void ProviderCommandBuilderQwenReadsPromptFromStdin()
    {
        var command = string.Join(
            ' ',
            ProviderCommandBuilder.Build(
                ProviderKind.OllamaQwenCodeCli,
                "qwen3.6-35b-a3b",
                reasoningEffort: null,
                resolvedPermissionMode: "bypassPermissions",
                resolvedSandboxMode: "workspace-write",
                workingDirectory: @"C:\worker repo",
                openaiBaseUrl: "http://127.0.0.1:8080/v1",
                openaiApiKey: "llamacpp",
                approvalMode: "yolo"));

        Assert.Contains("--input-format text", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-p", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content", command, StringComparison.Ordinal);
        Assert.DoesNotContain("{promptPath}", command, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ProviderCommandBuilder_appends_optional_claude_session_id")]
    public void ProviderCommandBuilderAppendsOptionalClaudeSessionId()
    {
        var command = ProviderCommandBuilder.Build(
            ProviderKind.AnthropicClaudeCli,
            "claude-sonnet-4-6",
            reasoningEffort: null,
            resolvedPermissionMode: "plan",
            resolvedSandboxMode: "read-only",
            workingDirectory: null,
            sessionId: "12345678-1234-1234-1234-123456789abc");

        Assert.Equal(
            "claude -p --model 'claude-sonnet-4-6' --permission-mode 'dontAsk' --restricted --tools 'Read,Glob,Grep,Bash,WebFetch,WebSearch,TodoWrite' --allowed-tools 'Read,Glob,Grep,Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git status *),Bash(git merge-base *),Bash(git rev-parse *),Bash(git blame *),Bash(git ls-files *),Bash(git branch *),Bash(git cat-file *),Bash(rg *),WebFetch,WebSearch,TodoWrite' --disallowed-tools 'Edit,Write,NotebookEdit' --session-id 12345678-1234-1234-1234-123456789abc",
            string.Join(' ', command));
    }

    [Xunit.Theory(DisplayName = "ProviderCommandBuilder_composes_structured_output_for_subscription_codex")]
    [Xunit.InlineData(ProviderKind.OpenAICodexCli)]
    [Xunit.InlineData(ProviderKind.OpenAICodexLuna)]
    public void ProviderCommandBuilderComposesStructuredOutputForSubscriptionCodex(
        ProviderKind providerKind)
    {
        var profile = WorkerProfileCatalog.Default().GetRequired(
            WorkerProviderCatalog.Default().Resolve(providerKind).ProfileName);
        var command = WorkerProfileDispatcher.BuildDispatchCommandTemplate(
            profile,
            providerKind,
            new Dictionary<string, string?>
            {
                ["subscriptionModelName"] = AgentCatalog.OpenAiSolSubscriptionModelAlias,
                ["subscriptionReasoningEffort"] = "high",
                ["permissionMode"] = "plan",
                ["sandboxMode"] = "read-only",
                ["workingDirectory"] = @"C:\worker repo"
            });

        Assert.Equal(
            $"codex exec --json --skip-git-repo-check --model '{AgentCatalog.OpenAiSolSubscriptionModelAlias}' -c model_reasoning_effort='high' -c project_doc_max_bytes=65536 --sandbox 'read-only' --cd 'C:\\worker repo'",
            command);
    }

    [Xunit.Fact(DisplayName = "ProviderCommandBuilder_preserves_blank_codex_template_literals")]
    public void ProviderCommandBuilderPreservesBlankCodexTemplateLiterals()
    {
        var command = string.Join(
            ' ',
            ProviderCommandBuilder.Build(
                ProviderKind.OpenAICodexCli,
                AgentCatalog.OpenAiSubscriptionModelAlias,
                reasoningEffort: null,
                resolvedPermissionMode: "plan",
                resolvedSandboxMode: "read-only",
                workingDirectory: null));

        Assert.Equal(
            $"codex exec --json --skip-git-repo-check --model '{AgentCatalog.OpenAiSubscriptionModelAlias}' -c model_reasoning_effort= --sandbox 'read-only' --cd ",
            command);
    }

    // These cases only construct arguments and transform strings; they are parallel-safe.
    [Xunit.Theory]
    [Xunit.InlineData(false, null)]
    [Xunit.InlineData(true, null)]
    [Xunit.InlineData(false, "12345678-1234-1234-1234-123456789abc")]
    [Xunit.InlineData(true, "12345678-1234-1234-1234-123456789abc")]
    public void Claude_AvailableOrOmitted_PreservesLiteralBaseline(bool readOnly, string? sessionId)
    {
        var omitted = ProviderCommandBuilder.Build(
            ProviderKind.AnthropicClaudeCli, "claude-sonnet-4-6", "high",
            readOnly ? "plan" : "bypassPermissions", "read-only", null, sessionId);
        var available = ProviderCommandBuilder.Build(
            ProviderKind.AnthropicClaudeCli, "claude-sonnet-4-6", "high",
            readOnly ? "plan" : "bypassPermissions", "read-only", null, sessionId,
            claudeBashAvailability: ClaudeBashAvailability.Available);

        Assert.Equal(ClaudeBaseline(readOnly, sessionId), omitted);
        Assert.Equal(ClaudeBaseline(readOnly, sessionId), available);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("12345678-1234-1234-1234-123456789abc")]
    public void Claude_UnusableWriteCapable_AddsOnlyBashDenial(string? sessionId)
    {
        var command = ProviderCommandBuilder.Build(
            ProviderKind.AnthropicClaudeCli, "claude-sonnet-4-6", "high",
            "bypassPermissions", "workspace-write", null, sessionId,
            claudeBashAvailability: ClaudeBashAvailability.Unusable);

        Assert.Equal(new[] { "--disallowed-tools", "'Bash'" }, command.Skip(8).Take(2));
        var unchanged = command.ToList();
        unchanged.RemoveRange(8, 2);
        Assert.Equal(ClaudeBaseline(readOnly: false, sessionId), unchanged);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("12345678-1234-1234-1234-123456789abc")]
    public void Claude_UnusableReadOnly_ChangesOnlyBashEntries(string? sessionId)
    {
        var command = ProviderCommandBuilder.Build(
            ProviderKind.AnthropicClaudeCli, "claude-sonnet-4-6", "high",
            "plan", "read-only", null, sessionId,
            claudeBashAvailability: ClaudeBashAvailability.Unusable);

        var expected = ClaudeBaseline(readOnly: true, sessionId).ToList();
        expected[10] = "'Read,Glob,Grep,WebFetch,WebSearch,TodoWrite'";
        expected[12] = "'Read,Glob,Grep,WebFetch,WebSearch,TodoWrite'";
        expected[14] = "'Edit,Write,NotebookEdit,Bash'";
        Assert.Equal(expected, command);
    }

    [Xunit.Theory]
    [Xunit.InlineData(ProviderKind.OpenAICodexCli, false)]
    [Xunit.InlineData(ProviderKind.OpenAICodexCli, true)]
    [Xunit.InlineData(ProviderKind.OpenAICodexLuna, false)]
    [Xunit.InlineData(ProviderKind.OpenAICodexLuna, true)]
    [Xunit.InlineData(ProviderKind.OpenAICodexOssCli, false)]
    [Xunit.InlineData(ProviderKind.OpenAICodexOssCli, true)]
    [Xunit.InlineData(ProviderKind.OllamaQwenCodeCli, false)]
    [Xunit.InlineData(ProviderKind.OllamaQwenCodeCli, true)]
    public void NonClaude_BashAvailability_DoesNotChangeArguments(ProviderKind kind, bool readOnly)
    {
        var available = ProviderCommandBuilder.Build(
            kind, "test-model", "high", readOnly ? "plan" : "bypassPermissions",
            readOnly ? "read-only" : "workspace-write", @"C:\worker's repo",
            openaiBaseUrl: "http://127.0.0.1:8080/v1", openaiApiKey: "test-key",
            approvalMode: readOnly ? "plan" : "yolo", repositoryPolicyMaxBytes: 65536,
            claudeBashAvailability: ClaudeBashAvailability.Available);
        var unusable = ProviderCommandBuilder.Build(
            kind, "test-model", "high", readOnly ? "plan" : "bypassPermissions",
            readOnly ? "read-only" : "workspace-write", @"C:\worker's repo",
            openaiBaseUrl: "http://127.0.0.1:8080/v1", openaiApiKey: "test-key",
            approvalMode: readOnly ? "plan" : "yolo", repositoryPolicyMaxBytes: 65536,
            claudeBashAvailability: ClaudeBashAvailability.Unusable);

        Assert.Equal(available, unusable);
    }

    [Xunit.Theory]
    [Xunit.InlineData(ClaudeBashAvailability.Available,
        "Read,Bash,BashOutput,bash,WebFetch", "Bash(rg *),Read,BashOutput(*),bash(*),WebSearch", "Edit,Write")]
    [Xunit.InlineData(ClaudeBashAvailability.Unusable,
        "Read,BashOutput,bash,WebFetch", "Read,BashOutput(*),bash(*),WebSearch", "Edit,Write,Bash")]
    public void ToolLists_Availability_PreservesNonBashEntries(
        ClaudeBashAvailability availability, string tools, string allowed, string disallowed)
    {
        const string originalTools = "Read,Bash,BashOutput,bash,WebFetch";
        const string originalAllowed = "Bash(rg *),Read,BashOutput(*),bash(*),WebSearch";
        const string originalDisallowed = "Edit,Write";

        var result = ClaudeBashToolLists.Apply(
            availability, originalTools, originalAllowed, originalDisallowed);

        Assert.Equal((tools, allowed, disallowed), result);
    }

    [Xunit.Theory]
    [Xunit.InlineData("", "Bash")]
    [Xunit.InlineData("Edit,Bash,Write", "Edit,Bash,Write")]
    [Xunit.InlineData("Edit,Bash(git *),Write", "Edit,Bash(git *),Write,Bash")]
    public void ToolLists_Unusable_HandlesEmptyListsAndExistingDenials(
        string disallowed, string expected)
    {
        var result = ClaudeBashToolLists.Apply(
            ClaudeBashAvailability.Unusable, "Bash", "Bash(rg *)", disallowed);

        Assert.Equal((string.Empty, string.Empty, expected), result);
    }

    private static IReadOnlyList<string> ClaudeBaseline(bool readOnly, string? sessionId)
    {
        // Literal pre-change arguments, independent of the production tool constants.
        List<string> baseline = readOnly
            ? [
                "claude", "-p", "--model", "'claude-sonnet-4-6'",
                "--permission-mode", "'dontAsk'", "--effort", "'high'", "--restricted",
                "--tools", "'Read,Glob,Grep,Bash,WebFetch,WebSearch,TodoWrite'",
                "--allowed-tools", "'Read,Glob,Grep,Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git status *),Bash(git merge-base *),Bash(git rev-parse *),Bash(git blame *),Bash(git ls-files *),Bash(git branch *),Bash(git cat-file *),Bash(rg *),WebFetch,WebSearch,TodoWrite'",
                "--disallowed-tools", "'Edit,Write,NotebookEdit'"
            ]
            : [
                "claude", "-p", "--model", "'claude-sonnet-4-6'",
                "--permission-mode", "'bypassPermissions'", "--effort", "'high'"
            ];
        if (sessionId is not null)
            baseline.AddRange(["--session-id", sessionId]);
        return baseline;
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";
}
