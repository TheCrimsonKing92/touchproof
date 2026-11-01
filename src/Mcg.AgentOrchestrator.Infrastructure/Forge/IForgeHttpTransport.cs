namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IForgeHttpTransport
{
    ForgeHttpResponse Post(Uri uri, IReadOnlyDictionary<string, string> headers, TimeSpan timeout);
}

public sealed record ForgeHttpResponse(int StatusCode, string Body)
{
    public override string ToString() => $"Forge HTTP response: {StatusCode}";
}
