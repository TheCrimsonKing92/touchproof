using System.Net.Http;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class ForgeHttpTransport : IForgeHttpTransport
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });

    public ForgeHttpResponse Post(Uri uri, IReadOnlyDictionary<string, string> headers, TimeSpan timeout) =>
        PostAsync(uri, headers, timeout).GetAwaiter().GetResult();

    private static async Task<ForgeHttpResponse> PostAsync(Uri uri,
        IReadOnlyDictionary<string, string> headers, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        foreach (var entry in headers) request.Headers.Add(entry.Key, entry.Value);
        using var response = await Client.SendAsync(request, cancellation.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellation.Token).ConfigureAwait(false);
        return new((int)response.StatusCode, body);
    }
}
