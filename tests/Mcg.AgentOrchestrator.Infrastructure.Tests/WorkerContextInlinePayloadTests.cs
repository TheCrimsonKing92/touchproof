using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: packages are in memory; the committed brief fixture is read-only.
public sealed class WorkerContextInlinePayloadTests(Xunit.ITestOutputHelper output)
{
    [Xunit.Fact]
    public void Render_MixedLineEndings_RehydratesExactBytes()
    {
        const string text = "<tag> `code` \"quotes\" 'apostrophe' &\r\nCRLF\nLF without trailing newline";
        var artifact = Artifact("instructions/task.md", Encoding.UTF8.GetBytes(text));
        var package = Package(artifact);
        var rendered = WorkerContextPackageBuilder.Render(package);
        var section = WorkerContextPackageBuilder.RenderArtifact(artifact);

        Assert.Contains($"encoding=utf8-raw; chars={text.Length}" + Environment.NewLine, section);
        Assert.Contains(Environment.NewLine + text + Environment.NewLine, section);
        Assert.EndsWith("END INLINE " + artifact.ContentHash, section);
        Assert.DoesNotContain("\\u00", section, StringComparison.Ordinal);
        Assert.Equal(artifact.AuthoritativeBytes, Rehydrate(package, rendered, artifact.Identity.Value));
    }

    [Xunit.Theory]
    [Xunit.InlineData("\n", "")]
    [Xunit.InlineData("\r\n", "")]
    [Xunit.InlineData("\n", "mixed\r\nlines\nno final newline")]
    [Xunit.InlineData("\r\n", "mixed\r\nlines\nno final newline")]
    [Xunit.InlineData("\n", "trailing CR\r")]
    [Xunit.InlineData("\r\n", "trailing CR\r")]
    [Xunit.InlineData("\n", "trailing LF\n")]
    [Xunit.InlineData("\r\n", "trailing CRLF\r\n")]
    [Xunit.InlineData("\n", "\uFEFFBOM and emoji \U0001F600\0")]
    [Xunit.InlineData("\r\n", "\uFEFFBOM and emoji \U0001F600\0")]
    public void Rehydrate_FramingVariants_PreserveEveryPayloadCharacter(string framing, string text)
    {
        var artifact = Artifact("instructions/task.md", Encoding.UTF8.GetBytes(text));
        var package = Package(artifact);
        var section = WorkerContextPackageBuilder.RenderArtifact(artifact);
        var header = section[..section.IndexOf('\n')].TrimEnd('\r');
        Assert.EndsWith("chars=" + text.Length.ToString(CultureInfo.InvariantCulture), header);
        var reframed = header + framing + text + framing + "END INLINE " + artifact.ContentHash;
        var rendered = WorkerContextPackageBuilder.Render(package).Replace(section, reframed, StringComparison.Ordinal);

        Assert.Equal(artifact.AuthoritativeBytes, Rehydrate(package, rendered, artifact.Identity.Value));
    }

    [Xunit.Theory]
    [Xunit.InlineData("changed-character")]
    [Xunit.InlineData("missing-chars")]
    [Xunit.InlineData("non-digit-chars")]
    [Xunit.InlineData("negative-chars")]
    [Xunit.InlineData("overflow-chars")]
    [Xunit.InlineData("wrong-count")]
    [Xunit.InlineData("missing-marker")]
    [Xunit.InlineData("partial-marker")]
    [Xunit.InlineData("wrong-marker-hash")]
    [Xunit.InlineData("missing-separator")]
    [Xunit.InlineData("wrong-separator")]
    [Xunit.InlineData("invalid-surrogate")]
    public void Rehydrate_MalformedRaw_ReportsInlineRawMismatch(string scenario)
    {
        const string text = "raw payload";
        var artifact = Artifact("instructions/task.md", Encoding.UTF8.GetBytes(text));
        var package = Package(artifact);
        var section = WorkerContextPackageBuilder.RenderArtifact(artifact);
        var marker = "END INLINE " + artifact.ContentHash;
        var tampered = scenario switch
        {
            "changed-character" => section.Replace(text, "Raw payload", StringComparison.Ordinal),
            "missing-chars" => section.Replace("; chars=11", "", StringComparison.Ordinal),
            "non-digit-chars" => section.Replace("chars=11", "chars=abc", StringComparison.Ordinal),
            "negative-chars" => section.Replace("chars=11", "chars=-1", StringComparison.Ordinal),
            "overflow-chars" => section.Replace("chars=11", "chars=2147483648", StringComparison.Ordinal),
            "wrong-count" => section.Replace("chars=11", "chars=12", StringComparison.Ordinal),
            "missing-marker" => section.Replace(marker, "", StringComparison.Ordinal),
            "partial-marker" => section.Replace(marker, marker + " extra", StringComparison.Ordinal),
            "wrong-marker-hash" => section.Replace(marker, "END INLINE " + new string('0', 64), StringComparison.Ordinal),
            "missing-separator" => section.Replace(Environment.NewLine + marker, marker, StringComparison.Ordinal),
            "wrong-separator" => section.Replace(Environment.NewLine + marker,
                (Environment.NewLine == "\r\n" ? "\n" : "\r\n") + marker, StringComparison.Ordinal),
            "invalid-surrogate" => section.Replace(text, "\uD800aw payload", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var rendered = WorkerContextPackageBuilder.Render(package).Replace(section, tampered, StringComparison.Ordinal);

        var error = Assert.Throws<WorkerContextPreparationException>(() =>
            Rehydrate(package, rendered, artifact.Identity.Value));

        Assert.Equal("inline-raw-mismatch", error.Reason);
        Assert.Equal(artifact.Identity, error.Identity);
    }

    [Xunit.Fact]
    public void Rehydrate_StoredUtf8Json_RemainsLossless()
    {
        const string text = "legacy <tag> `code` \"quote\"\r\nline\n";
        var artifact = Artifact("instructions/task.md", Encoding.UTF8.GetBytes(text));
        var package = Package(artifact);
        var json = JsonSerializer.Serialize(text);
        var legacy = LegacySection(artifact, text);
        var rendered = WorkerContextPackageBuilder.Render(package).Replace(
            WorkerContextPackageBuilder.RenderArtifact(artifact), legacy, StringComparison.Ordinal);

        Assert.Equal(artifact.AuthoritativeBytes,
            WorkerContextPackageBuilder.RecoverInlineBytes(json, "utf8-json"));
        Assert.Equal(artifact.AuthoritativeBytes, Rehydrate(package, rendered, artifact.Identity.Value));
    }

    [Xunit.Fact]
    public void Render_InvalidUtf8_UsesBase64AndRehydratesExactBytes()
    {
        var artifact = Artifact("instructions/binary", [0xff, 0x00, 0xc3, 0x28]);
        var package = Package(artifact);
        var rendered = WorkerContextPackageBuilder.Render(package);

        Assert.Contains("encoding=base64" + Environment.NewLine + Convert.ToBase64String(artifact.AuthoritativeBytes!), rendered);
        Assert.DoesNotContain("END INLINE " + artifact.ContentHash, rendered, StringComparison.Ordinal);
        Assert.Equal(artifact.AuthoritativeBytes, Rehydrate(package, rendered, artifact.Identity.Value));
    }

    [Xunit.Fact]
    public void Rehydrate_QuotedArtifactHeader_DoesNotCreateAnotherSection()
    {
        var inner = Artifact("instructions/b.md", Encoding.UTF8.GetBytes("actual second artifact"));
        var outer = Artifact("instructions/a.md", Encoding.UTF8.GetBytes(
            "Quoted section:\n" + WorkerContextPackageBuilder.RenderArtifact(inner) + "\nEnd quote."));
        var package = Package(outer, inner);
        var rendered = WorkerContextPackageBuilder.Render(package);

        Assert.Equal(inner.AuthoritativeBytes, Rehydrate(package, rendered, inner.Identity.Value));
        Assert.Equal(outer.AuthoritativeBytes, Rehydrate(package, rendered, outer.Identity.Value));
    }

    [Xunit.Fact]
    public void Rehydrate_DuplicateActualHeader_ReportsAmbiguous()
    {
        var artifact = Artifact("instructions/task.md", Encoding.UTF8.GetBytes("payload"));
        var package = Package(artifact);
        var rendered = WorkerContextPackageBuilder.Render(package) + Environment.NewLine +
            WorkerContextPackageBuilder.RenderArtifact(artifact);

        var error = Assert.Throws<WorkerContextPreparationException>(() =>
            Rehydrate(package, rendered, artifact.Identity.Value));

        Assert.Equal("inline-rehydration-ambiguous", error.Reason);
    }

    [Xunit.Fact]
    public void Render_RepositoryGoalBrief_ShrinksSectionAndUpdatesReceipt()
    {
        var path = Path.Combine(VerifiedRepositoryRoot.Find(), "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Fixtures", "WorkerContextInlinePayload", "goal-brief-fd1d4f72.md");
        var text = File.ReadAllText(path);
        var artifact = Artifact("goal/objective.md", Encoding.UTF8.GetBytes(text));
        var package = Package(artifact);
        var raw = WorkerContextPackageBuilder.RenderArtifact(artifact);
        var legacy = LegacySection(artifact, text);
        var ratio = (double)raw.Length / legacy.Length;
        output.WriteLine($"Rendered section: raw={raw.Length}; utf8-json={legacy.Length}; ratio={ratio:F4}");
        Assert.True(ratio <= 0.90, $"Expected at least 10% reduction; raw/json ratio was {ratio:F4}.");

        var rendered = WorkerContextPackageBuilder.Render(package);
        var receipt = WorkerContextPackageBuilder.CreateReceipt(package);
        var section = Assert.Single(receipt.Sections, item => item.LogicalIdentity == artifact.Identity.Value);
        Assert.Equal(raw.Length, section.CharacterCount);
        Assert.Equal(rendered.Length, receipt.RenderedPromptCharacters);
        Assert.Equal(Encoding.UTF8.GetByteCount(rendered), receipt.RenderedPromptBytes);
        Assert.Equal(WorkerPromptInputBudget.CountTokens(rendered), receipt.ModelInputTokenEstimate);
        Assert.Equal(artifact.AuthoritativeBytes, Rehydrate(package, rendered, artifact.Identity.Value));
    }

    private static WorkerContextArtifact Artifact(string identity, byte[] bytes) =>
        WorkerContextArtifact.Create(new LogicalArtifactIdentity(identity), ContextArtifactKind.OperatorInstructions,
            bytes, [AgentRole.Developer], ContextDeliveryMode.InlineFull, ContextContractVersion.V1);

    private static WorkerContextPackage Package(params WorkerContextArtifact[] artifacts)
    {
        var builder = new WorkerContextPackageBuilder();
        var prepared = builder.Prepare(AgentRole.Developer, Path.GetTempPath(), artifacts);
        return WorkerProfileDispatcher.FinalizeContextPackageWithManifest(builder, prepared);
    }

    private static byte[] Rehydrate(WorkerContextPackage package, string rendered, string identity)
    {
        var restored = new WorkerContextPackageBuilder().RehydrateAndAppendInlineArtifact(
            AgentRole.Developer, Path.GetTempPath(), rendered, WorkerContextPackageBuilder.CreateReceipt(package),
            new LogicalArtifactIdentity("instructions/appended.md"), Encoding.UTF8.GetBytes("appended"));
        return Assert.Single(restored.Artifacts, artifact => artifact.Identity.Value == identity).AuthoritativeBytes!;
    }

    private static string LegacySection(WorkerContextArtifact artifact, string text) =>
        $"INLINE FULL: identity={artifact.Identity.Value}; sha256={artifact.ContentHash}; contract={artifact.ContractVersion.Value}; encoding=utf8-json" +
        Environment.NewLine + JsonSerializer.Serialize(text);
}
