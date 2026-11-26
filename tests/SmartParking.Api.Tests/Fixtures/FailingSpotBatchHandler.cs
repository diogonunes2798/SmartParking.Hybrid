namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Lets the facility and zone requests reach the test Api and throws <see cref="HttpRequestException"/> on every
/// <c>spots/batch</c> request, as a connection lost in the middle of <c>--provision</c> (review of F4b, finding 2).
/// </summary>
internal sealed class FailingSpotBatchHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.RequestUri?.AbsolutePath.EndsWith("/spots/batch", StringComparison.Ordinal) == true
            ? throw new HttpRequestException("Connection lost (test).")
            : base.SendAsync(request, cancellationToken);
}
