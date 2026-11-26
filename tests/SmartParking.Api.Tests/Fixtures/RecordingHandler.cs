using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Records every ingestion request the simulator sends (spec 003, F4b): the <c>eventId</c>s in order, the answer and
/// a monotonic timestamp (<see cref="Stopwatch"/>), so a test can check batch sizes and the wait before a resend.
/// The handler sits on the HTTP boundary; the Api behind it is the real test host.
/// </summary>
internal sealed class RecordingHandler : DelegatingHandler
{
    private readonly ConcurrentQueue<Sent> requests = new();

    /// <summary><see cref="Timestamp"/> is taken when the request leaves, <see cref="AnsweredAt"/> when its answer arrives.</summary>
    public sealed record Sent(IReadOnlyList<Guid> EventIds, HttpStatusCode StatusCode, TimeSpan? RetryAfter, long Timestamp, long AnsweredAt);

    public IReadOnlyList<Sent> Requests => [.. requests];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var timestamp = Stopwatch.GetTimestamp();
        var response = await base.SendAsync(request, cancellationToken);
        var answeredAt = Stopwatch.GetTimestamp();

        if (body is not null && request.RequestUri?.AbsolutePath == IngestionApi.Route)
        {
            using var document = JsonDocument.Parse(body);
            Guid[] ids = [.. document.RootElement.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("eventId").GetGuid())];
            requests.Enqueue(new Sent(ids, response.StatusCode, response.Headers.RetryAfter?.Delta, timestamp, answeredAt));
        }

        return response;
    }
}
