using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartParking.SensorSimulator.Generation;
using SmartParking.SensorSimulator.Wire;

namespace SmartParking.SensorSimulator.Sending;

/// <summary>
/// Sends one gateway batch to <c>POST /api/ingest/occupancy-events</c> through <see cref="IHttpClientFactory"/>.
/// A 503 is the only answer that is retried (spec 003, round 6): the same serialized batch, so the same <c>eventId</c>s,
/// after the <c>Retry-After</c>, at most <c>maxRetries</c> times. The repeat is safe because the ingestion is idempotent.
/// Any other answer, or a transport failure, is logged and counted; the batch is not resent. A resilience pipeline
/// (Polly) is not used, because it would also retry 5xx answers and timeouts and change these semantics.
/// </summary>
internal sealed partial class IngestionSender(IHttpClientFactory httpClientFactory, TimeProvider clock, ILogger<IngestionSender> logger)
{
    /// <summary>Wait used when a 503 carries no usable <c>Retry-After</c> (the Api always sends one).</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(1);

    public async Task<SendOutcome> SendAsync(IReadOnlyList<SimulatedEvent> batch, int maxRetries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var body = JsonSerializer.SerializeToUtf8Bytes(new OccupancyBatchRequest([.. batch.Select(item => item.ToRequest())]), ApiWire.Json);
        var client = httpClientFactory.CreateClient(ApiWire.ClientName);
        var retries = 0;

        while (true)
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(ApiWire.IngestRoute, content, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                LogTransportFailure(logger, exception, batch.Count);
                return new SendOutcome(batch.Count, null, 0, 0, 0, retries);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout: the request was not cancelled by the run, it timed out.
                LogTransportFailure(logger, exception, batch.Count);
                return new SendOutcome(batch.Count, null, 0, 0, 0, retries);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && retries < maxRetries)
                {
                    var wait = RetryAfter(response.Headers.RetryAfter, clock.GetUtcNow());
                    retries++;
                    LogRetry(logger, batch.Count, wait.TotalSeconds, retries);
                    await Task.Delay(wait, clock, cancellationToken);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    LogNotDelivered(logger, batch.Count, (int)response.StatusCode, retries);
                    return new SendOutcome(batch.Count, response.StatusCode, 0, 0, 0, retries);
                }

                OccupancyBatchResponse? result;
                try
                {
                    result = await response.Content.ReadFromJsonAsync<OccupancyBatchResponse>(ApiWire.Json, cancellationToken);
                }
                catch (JsonException exception)
                {
                    return Unreadable(exception);
                }
                catch (HttpRequestException exception)
                {
                    return Unreadable(exception);
                }
                catch (IOException exception)
                {
                    return Unreadable(exception);
                }

                return result is null
                    ? Unreadable(null)
                    : new SendOutcome(batch.Count, response.StatusCode, result.Accepted, result.Duplicates, result.Rejected, retries);
            }
        }

        // A 200 whose counts cannot be read: the batch may be stored, but nothing confirms it, so it is counted as not
        // delivered instead of faulting the gateway (review of F4b, finding 1).
        SendOutcome Unreadable(Exception? exception)
        {
            LogUnreadableAnswer(logger, exception, batch.Count);
            return new SendOutcome(batch.Count, null, 0, 0, 0, retries);
        }
    }

    /// <summary>The wait asked by <c>Retry-After</c>: seconds, or an HTTP date relative to <paramref name="now"/>; never negative.</summary>
    internal static TimeSpan RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now) => header switch
    {
        { Delta: { } delta } => delta < TimeSpan.Zero ? TimeSpan.Zero : delta,
        { Date: { } date } => date - now is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
        _ => DefaultRetryAfter,
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Batch of {Count} events got 503; resending the same batch in {WaitSeconds} s (retry {Retry})")]
    private static partial void LogRetry(ILogger logger, int count, double waitSeconds, int retry);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Batch of {Count} events not delivered: status {StatusCode} after {Retries} retries")]
    private static partial void LogNotDelivered(ILogger logger, int count, int statusCode, int retries);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Batch of {Count} events not delivered: transport failure")]
    private static partial void LogTransportFailure(ILogger logger, Exception exception, int count);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Batch of {Count} events got 200 with an unreadable body; counted as not delivered")]
    private static partial void LogUnreadableAnswer(ILogger logger, Exception? exception, int count);
}
