namespace SmartParking.Workers.Reservations;

/// <summary>
/// Counts of one expiry pass: reservations expired, ignored because another request changed them first (stale
/// <c>xmin</c>, 409), and skipped because they were no longer <c>Active</c> or not yet due when read again.
/// </summary>
internal sealed record ExpiryPassResult(int Expired, int Conflicts, int Skipped);
