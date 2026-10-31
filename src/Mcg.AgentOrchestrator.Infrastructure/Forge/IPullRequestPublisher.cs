namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IPullRequestPublisher
{
    PullRequestPublishResult Publish(PullRequestPublishRequest request);
}
