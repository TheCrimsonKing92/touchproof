using Mcg.AgentOrchestrator.Infrastructure;
using Repository = GitRefFileResolverFixtureConfigParityTests.Repository;

// Parallel-safe: each class fixture owns its repository and restores config bytes after every row.
public sealed class GitRefFileResolverFixtureConfigDeclineTests(Repository repository)
    : IClassFixture<Repository>
{
    public static IEnumerable<object[]> UnsupportedConfigurations()
    {
        foreach (var (section, key) in new[]
                 { ("core", "autocrlf"), ("core", "longpaths"), ("commit", "gpgsign"), ("gc", "auto") })
        {
            foreach (var value in new[] { "maybe", "\"1\"", "\\1" })
                yield return [$"[{section}]\n{key} = {value}\n"];
            yield return [$"[{section}]\n{key}\n"];
        }
        foreach (var value in new[] { "INPUT", "\"input\"", "2" })
            yield return [$"[core]\nautocrlf = {value}\n"];
        yield return ["[core]\nlongpaths = 2\n"];
        yield return ["[commit]\ngpgsign = 2\n"];
        foreach (var value in new[] { "-1", "+5", "0x10", "1k", "1.5", "", "2147483648", "99999999999999999999", "١", "１" })
            yield return [$"[gc]\nauto = {value}\n"];
        yield return ["[commit]\ntemplate = message.txt\n"];
        yield return ["[core]\nhookspath = hooks\n"];
        yield return ["[gc]\nreflogExpire = never\n"];
        yield return ["[gc]\nautoPackLimit = 0\n"];
    }

    [Theory]
    [MemberData(nameof(UnsupportedConfigurations))]
    public void UnsupportedConfig_DeclinesAllRefsAfterPositiveControl(string config)
    {
        var expected = repository.Oracle();
        for (var i = 0; i < Repository.Revisions.Length; i++)
        {
            Assert.True(GitRefFileResolver.TryResolve(repository.Repo, Repository.Revisions[i], out var actual));
            Assert.Equal(expected[i], actual);
        }

        repository.WithConfig(config, () =>
        {
            foreach (var revision in Repository.Revisions)
            {
                Assert.False(GitRefFileResolver.TryResolve(repository.Repo, revision, out var actual));
                Assert.Empty(actual);
            }
        });
    }
}
