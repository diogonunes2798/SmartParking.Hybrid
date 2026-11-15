namespace SmartParking.Application.Availability;

/// <summary>
/// Holds the projectors back until the read model has been loaded (initial load or rebuild, ADR-0008). Without it,
/// events consumed before the load would find no spots (<see cref="ApplyOutcome.UnknownSpot"/>) and be lost for
/// the read model. Messages wait unacknowledged in the stream meanwhile, so nothing is lost.
/// </summary>
public sealed class ReadModelGate
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsOpen => ready.Task.IsCompleted;

    public void Open() => ready.TrySetResult();

    public Task WaitAsync(CancellationToken cancellationToken) => ready.Task.WaitAsync(cancellationToken);

    /// <summary>A gate that is already open (hosts that load nothing, e.g. tests of a single projector).</summary>
    public static ReadModelGate Opened()
    {
        var gate = new ReadModelGate();
        gate.Open();
        return gate;
    }
}
