namespace SmartParking.Api.Tests.Fixtures;

/// <summary>
/// Cancels <see cref="Cancellation"/> when the first request whose path ends with <paramref name="pathSuffix"/> goes
/// out, as a Ctrl+C pressed at that point of a run. Cancelling from inside the run, and not on a timer, keeps the tests
/// independent of how long the host and the mapping take to load (second review of F4b).
/// </summary>
internal sealed class CancelOnRequestHandler(CancellationTokenSource cancellation, string pathSuffix) : DelegatingHandler
{
    public CancellationTokenSource Cancellation { get; } = cancellation;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal) == true)
        {
            Cancellation.Cancel();
        }

        return base.SendAsync(request, cancellationToken);
    }
}
