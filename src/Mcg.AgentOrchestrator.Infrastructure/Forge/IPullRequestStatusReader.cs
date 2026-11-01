namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IPullRequestStatusReader
{
    PullRequestStatus Read(string repositoryRoot, int number);
}
