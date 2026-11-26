using System.Net;

namespace SmartParking.SensorSimulator.Sending;

/// <summary>
/// Result of sending one batch: the final status code (<see langword="null"/> on a transport failure), the counts of a
/// 200 and how many times the batch was resent after a 503.
/// </summary>
internal sealed record SendOutcome(int Events, HttpStatusCode? StatusCode, int Accepted, int Duplicates, int Rejected, int Retries)
{
    public bool Delivered => StatusCode == HttpStatusCode.OK;
}
