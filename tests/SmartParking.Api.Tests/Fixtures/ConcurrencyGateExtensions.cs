namespace SmartParking.Api.Tests.Fixtures;

/// <summary>Runs two requests against the <see cref="ConcurrencyGate"/> (AC-11, AC-16, AC-27).</summary>
internal static class ConcurrencyGateExtensions
{
    /// <summary>Arms the gate for both requests, runs them together and always disarms it, even when a request fails.</summary>
    public static async Task<HttpResponseMessage[]> RaceAsync(
        this ConcurrencyGate gate, Func<Task<HttpResponseMessage>> first, Func<Task<HttpResponseMessage>> second)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        gate.Arm(2);
        try
        {
            return await Task.WhenAll(first(), second());
        }
        finally
        {
            gate.Disarm();
        }
    }
}
