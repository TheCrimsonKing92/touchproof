using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: tests read repository guidance and own their temporary context and store roots.
public sealed class WorkerGuidanceScopeTests
{
    [Theory]
    [InlineData(AgentRole.Developer, true)]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Planner, true)]
    [InlineData(AgentRole.Researcher, false)]
    [InlineData(AgentRole.Tester, false)]
    public void Write_RepositoryGuidance_PreservesRoleSectionsAndCompleteArtifacts(
        AgentRole role, bool keepDesign)
    {
        var repositoryRoot = VerifiedRepositoryRoot.Find();
        var sourcePath = Path.Combine(repositoryRoot, "AGENTS.md");
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var skillDirectory = Path.Combine(repositoryRoot, ".agents", "skills");
        var root = Directory.CreateTempSubdirectory("worker-guidance-scope-");
        try
        {
            File.Copy(sourcePath, Path.Combine(root.FullName, "AGENTS.md"));
            var targetHome = new WorkerTargetHome(false, projectHomeDirectory: root.FullName);
            var task = new TaskSpec(TaskId.New(), "Verify the selected worker skill and context artifacts.", role);
            var goal = new AgentOrchestratorKernel().CreateGoal("Preserve scoped repository guidance.", [task]);
            var skillName = role == AgentRole.Developer ? "verification-before-completion" : "skill-authoring";
            var selectedSkill = Assert.Single(WorkerContextArtifacts.SelectSkillRequirements(
                goal, task, root.FullName, skillDirectory, targetHome), skill => skill.Name == skillName);
            Assert.Equal(WorkerSkillSource.Orchestrator, selectedSkill.ResolvedSource);
            Assert.False(File.Exists(selectedSkill.WorkingDirectoryCandidatePath));
            var skillSource = Path.Combine(skillDirectory, skillName, "SKILL.md");
            Assert.Equal(skillSource, selectedSkill.ResolvedPath);

            var context = WorkerContextArtifacts.Write(goal, task, root.FullName,
                orchestratorStoreRoot: Path.Combine(root.FullName, "store"),
                orchestratorSkillDirectory: skillDirectory, targetHome: targetHome);
            var contextBytes = File.ReadAllBytes(Path.Combine(context, "AGENTS.md"));
            var contextText = File.ReadAllText(Path.Combine(context, "AGENTS.md"));
            var preambleEnd = AnchorOffset(sourceBytes, "output-discipline");
            Assert.True(contextBytes.AsSpan().StartsWith(sourceBytes.AsSpan(0, preambleEnd)),
                "The complete repository preamble must be retained byte-for-byte.");
            foreach (var (anchor, nextAnchor) in new (string, string?)[]
            {
                ("output-discipline", "retry-and-loop-control"),
                ("retry-and-loop-control", "repository-rules"),
                ("repository-rules", "architecture-and-design-discipline"),
                ("specification-discipline", "diagnosis-discipline"),
                ("diagnosis-discipline", "dogfood-boundary"),
                ("safety", "evidence"),
                ("evidence", null)
            })
            {
                Assert.True(contextBytes.AsSpan().IndexOf(RepositorySection(sourceBytes, anchor, nextAnchor)) >= 0,
                    $"{role} must retain the complete {anchor} section, including its anchor and original bytes.");
            }
            Assert.DoesNotContain("\n## Dogfood Boundary", contextText, StringComparison.Ordinal);
            Assert.DoesNotContain("\n## Operating the goal loop", contextText, StringComparison.Ordinal);
            Assert.DoesNotContain("<!-- shared-discipline:dogfood-boundary -->", contextText, StringComparison.Ordinal);
            Assert.DoesNotContain("<!-- shared-discipline:operating-the-goal-loop -->", contextText, StringComparison.Ordinal);
            var design = RepositorySection(sourceBytes, "architecture-and-design-discipline", "specification-discipline");
            if (keepDesign)
                Assert.True(contextBytes.AsSpan().IndexOf(design) >= 0, $"{role} must retain design guidance byte-for-byte.");
            else
            {
                Assert.DoesNotContain("\n## Architecture & Design Discipline", contextText, StringComparison.Ordinal);
                Assert.DoesNotContain("<!-- shared-discipline:architecture-and-design-discipline -->", contextText, StringComparison.Ordinal);
                Assert.True(contextBytes.LongLength * 10 <= sourceBytes.LongLength * 7,
                    $"{role} guidance is {contextBytes.Length} of {sourceBytes.Length} bytes; at least 30 percent must be removed.");
            }
            Assert.Equal(File.ReadAllBytes(skillSource),
                File.ReadAllBytes(Path.Combine(context, selectedSkill.RelativePath)));
            if (role == AgentRole.Developer)
            {
                Assert.Contains(goal.Objective, File.ReadAllText(Path.Combine(context, "objective.md")), StringComparison.Ordinal);
                Assert.Contains(task.Description, File.ReadAllText(Path.Combine(context, "current-task.md")), StringComparison.Ordinal);
                var rules = File.ReadAllText(Path.Combine(context, WorkerStandingRules.ContextFileName));
                foreach (var rule in WorkerStandingRules.RulesForRole(AgentRole.Developer, stack: null))
                    Assert.Contains("- " + rule, rules, StringComparison.Ordinal);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static int AnchorOffset(byte[] source, string anchor)
    {
        var offset = source.AsSpan().IndexOf(Encoding.UTF8.GetBytes($"<!-- shared-discipline:{anchor} -->"));
        Assert.True(offset >= 0, $"Repository guidance must contain the {anchor} anchor.");
        return offset;
    }

    private static byte[] RepositorySection(byte[] source, string anchor, string? nextAnchor)
    {
        var start = AnchorOffset(source, anchor);
        var end = nextAnchor is null ? source.Length : AnchorOffset(source, nextAnchor);
        Assert.True(end > start, $"Repository guidance must order {anchor} before {nextAnchor}.");
        return source[start..end];
    }

    [Fact]
    public void Project_RepositoryDeveloper_RemovesOnlyOperatorAnchorSpan()
    {
        var path = Path.Combine(VerifiedRepositoryRoot.Find(), "AGENTS.md");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("<!-- shared-discipline:dogfood-boundary -->", StringComparison.Ordinal);
        var end = source.IndexOf("<!-- shared-discipline:safety -->", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Repository operator span must exist.");
        var expected = source.Remove(start, end - start);

        var actual = WorkerGuidanceScope.Project(source, AgentRole.Developer);

        Assert.Equal(expected, actual);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
        Assert.Equal(File.ReadAllBytes(path), Encoding.UTF8.GetBytes(source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# Guidance\r\nNo H2 headings.  ")]
    [InlineData("## Safety\nKeep all of this.\n")]
    public void Project_NoExcludedSections_PreservesWholeInput(string source)
    {
        Assert.Equal(source, WorkerGuidanceScope.Project(source, AgentRole.Tester));
    }

    [Theory]
    [InlineData(AgentRole.Developer, true)]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Planner, true)]
    [InlineData(AgentRole.Ideation, true)]
    [InlineData((AgentRole)int.MaxValue, true)]
    [InlineData(AgentRole.Researcher, false)]
    [InlineData(AgentRole.Tester, false)]
    public void Project_Role_KeepsOnlyApplicableDesignGuidance(AgentRole role, bool keepDesign)
    {
        const string preamble = "# Guidance\nPreamble.\n\n";
        const string design = "<!-- shared-discipline:architecture-and-design-discipline -->\n" +
                              "## Architecture & Design Discipline\nDesign.\n\n";
        const string operators = "<!-- shared-discipline:dogfood-boundary -->\n" +
                                 "## Dogfood Boundary\nOperator procedure.\n\n" +
                                 "<!-- shared-discipline:operating-the-goal-loop -->\n" +
                                 "## Operating the goal loop\nMore procedure.\n\n";
        const string safety = "<!-- shared-discipline:safety -->\n## Safety\nInvariant.\n";

        Assert.Equal(preamble + (keepDesign ? design : "") + safety,
            WorkerGuidanceScope.Project(preamble + design + operators + safety, role));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Project_ExcludedSection_PreservesRetainedWhitespace(string newline)
    {
        var preamble = "# Préambule  " + newline + newline;
        var removed = "<!-- shared-discipline:dogfood-boundary -->" + newline +
                      "  ##\tDogfood Boundary  ### \t" + newline + "Remove." + newline;
        var retained = "<!-- shared-discipline:safety -->" + newline +
                       "## Safety" + newline + "  Keep — exactly.  ";

        Assert.Equal(preamble + retained,
            WorkerGuidanceScope.Project(preamble + removed + retained, AgentRole.Developer));
    }

    [Theory]
    [InlineData("```csharp", "```")]
    [InlineData("~~~markdown", "~~~~")]
    [InlineData("````", "````")]
    [InlineData("   ```", "   ```")]
    public void Project_FencedHeadings_KeepExamplesInsideTheirSection(string open, string close)
    {
        var retained = "## Safety\n" + open + "\n" +
                       "<!-- shared-discipline:dogfood-boundary -->\n## Dogfood Boundary\n" +
                       "## Operating the goal loop\n## Architecture & Design Discipline\n" +
                       close + "\nKeep.\n";
        const string removed = "## Dogfood Boundary\nRemove.\n";
        const string tail = "## Evidence\nReceipt.";

        Assert.Equal(retained + tail,
            WorkerGuidanceScope.Project(retained + removed + tail, AgentRole.Tester));
    }

    [Fact]
    public void Project_ShortOrMismatchedFence_DoesNotEndExample()
    {
        const string source = "## Safety\n````\n```\n~~~\n" +
                              "## Dogfood Boundary\nStill an example.\n````\n" +
                              "## Operating the goal loop\nRemove.\n## Evidence\nKeep.";
        const string expected = "## Safety\n````\n```\n~~~\n" +
                                "## Dogfood Boundary\nStill an example.\n````\n" +
                                "## Evidence\nKeep.";

        Assert.Equal(expected, WorkerGuidanceScope.Project(source, AgentRole.Developer));
    }

    [Fact]
    public void Project_RepeatedExclusions_RemovesNestedHeadingsAndAttachedComments()
    {
        const string source = "Preamble.\n<!-- operator comment -->\n" +
                              "<!-- shared-discipline:dogfood-boundary -->\n" +
                              "## Dogfood Boundary\n### Safety\nNested operator content.\n" +
                              "## New Guidance\nUnknown invariant.\n" +
                              "## Dogfood Boundary\nRemove again.\n" +
                              "<!-- shared-discipline:safety -->\n## Safety\nKeep.";
        const string expected = "Preamble.\n## New Guidance\nUnknown invariant.\n" +
                                "<!-- shared-discipline:safety -->\n## Safety\nKeep.";

        Assert.Equal(expected, WorkerGuidanceScope.Project(source, AgentRole.Developer));
    }

    [Fact]
    public void Project_SimilarTitlesAndSubheadings_RetainsUnknownGuidance()
    {
        const string retained = "## dogfood boundary\nLowercase.\n" +
                                "## Dogfood Boundary revised\nDifferent title.\n" +
                                "### Operating the goal loop\nSubheading.\n" +
                                "##Dogfood Boundary\nNot a Markdown heading.\n" +
                                "    ## Dogfood Boundary\nIndented code.\n";

        Assert.Equal(retained, WorkerGuidanceScope.Project(
            retained + "## Dogfood Boundary\nRemove without a final newline.", AgentRole.Developer));
    }

    [Fact]
    public void Project_OnlyExcludedSection_ReturnsEmpty()
    {
        Assert.Equal("", WorkerGuidanceScope.Project("## Dogfood Boundary", AgentRole.Developer));
    }
}
