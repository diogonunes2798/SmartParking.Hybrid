using System.Net;
using System.Text;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Replaces the body of every 200 answer with truncated JSON, after the real test Api has handled the request
/// (review of F4b, finding 1: an unreadable 200 must not fault the gateway). Counts the requests it saw.
/// </summary>
internal sealed class TruncatedOkBodyHandler : DelegatingHandler
{
    private int requests;

    public int Requests => Volatile.Read(ref requests);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Dispose();
            response.Content = new StringContent("{\"accepted\": 1, \"dupl", Encoding.UTF8, "application/json");
        }

        return response;
    }
}
