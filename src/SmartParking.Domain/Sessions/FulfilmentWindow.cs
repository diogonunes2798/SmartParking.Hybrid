namespace SmartParking.Domain.Sessions;

/// <summary>Instants in which a reservation can be fulfilled, [<see cref="From"/>, <see cref="To"/>], closed at both ends.</summary>
public readonly record struct FulfilmentWindow(DateTimeOffset From, DateTimeOffset To)
{
    public bool Contains(DateTimeOffset instant) => instant >= From && instant <= To;
}
