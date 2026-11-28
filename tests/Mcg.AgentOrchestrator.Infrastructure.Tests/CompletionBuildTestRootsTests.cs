using System.Text.Json;

public sealed class CompletionBuildTestRootsTests
{
    [Fact]
    public void RemovesEvidenceRootAndFirstCandidateProfileRoot()
    {
        var temp = SharedTestSupport.CreateTempDirectory();
        try
        {
            var evidence = Directory.CreateDirectory(Path.Combine(temp, "evidence")).FullName;
            var profile = Directory.CreateDirectory(Path.Combine(temp, "profile")).FullName;
            var laterProfile = Directory.CreateDirectory(Path.Combine(temp, "later-profile")).FullName;
            var receipts = Path.Combine(evidence, "receipts.jsonl");
            File.WriteAllLines(receipts,
                [Receipt("tick", laterProfile), Receipt("candidate", profile), Receipt("candidate", laterProfile)]);
            File.WriteAllText(Path.Combine(profile, "profile-file"), "profile content");
            Assert.True(Directory.Exists(evidence));
            Assert.True(Directory.Exists(profile));

            CompletionBuildTestRoots.Remove(evidence, receipts);

            Assert.False(Directory.Exists(evidence));
            Assert.False(Directory.Exists(profile));
            Assert.True(Directory.Exists(laterProfile));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(temp);
        }
    }

    [Fact]
    public void RemovesEvidenceRootWhenReceiptsFileIsMissing()
    {
        var evidence = SharedTestSupport.CreateTempDirectory();
        try
        {
            CompletionBuildTestRoots.Remove(evidence, Path.Combine(evidence, "receipts.jsonl"));

            Assert.False(Directory.Exists(evidence));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(evidence);
        }
    }

    [Fact]
    public void RemovesEvidenceRootWhenCandidateProfileRootIsAbsent()
    {
        var evidence = SharedTestSupport.CreateTempDirectory();
        try
        {
            var profile = Path.Combine(evidence, "absent-profile");
            var receipts = Path.Combine(evidence, "receipts.jsonl");
            File.WriteAllText(receipts, Receipt("candidate", profile));
            Assert.False(Directory.Exists(profile));

            CompletionBuildTestRoots.Remove(evidence, receipts);

            Assert.False(Directory.Exists(evidence));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(evidence);
        }
    }

    [Fact]
    public void RemovesEvidenceRootAndSurfacesMalformedReceipt()
    {
        var evidence = SharedTestSupport.CreateTempDirectory();
        try
        {
            var receipts = Path.Combine(evidence, "receipts.jsonl");
            File.WriteAllText(receipts, "{");

            Assert.ThrowsAny<JsonException>(() => { CompletionBuildTestRoots.Remove(evidence, receipts); });

            Assert.False(Directory.Exists(evidence));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(evidence);
        }
    }

    private static string Receipt(string kind, string profile) =>
        JsonSerializer.Serialize(new { kind, at = DateTimeOffset.UnixEpoch, pid = 1, payload = new { Profile = profile } });
}
