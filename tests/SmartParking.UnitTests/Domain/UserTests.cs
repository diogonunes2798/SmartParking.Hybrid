using SmartParking.Domain.Users;
using SmartParking.Domain.Users.Events;

namespace SmartParking.UnitTests.Domain;

/// <summary>The user aggregate raises the events of its vehicles and payment methods (spec 002, ADR 0006).</summary>
public sealed class UserTests
{
    [Fact]
    public void Register_RaisesUserRegisteredWithoutPersonalData()
    {
        // Act
        var user = User.Register(Guid.CreateVersion7(), "Ana", "Ana@Example.PT");

        // Assert
        var raised = Assert.Single(user.DequeueRaisedEvents());
        Assert.Equal(new UserRegistered(user.Id), raised.Event);
        Assert.Same(user, Assert.Single(raised.Changed));
    }

    [Fact]
    public void RegisterVehicle_ChangesOnlyTheVehicle()
    {
        // Arrange
        var user = NewUser();

        // Act
        var vehicle = user.RegisterVehicle(Guid.CreateVersion7(), "aa-12 bb");

        // Assert
        var raised = Assert.Single(user.DequeueRaisedEvents());
        Assert.Equal(new VehicleRegistered(vehicle.Id, user.Id, "AA12BB"), raised.Event);
        Assert.Same(vehicle, Assert.Single(raised.Changed));
        Assert.Equal(user.Id, vehicle.UserId);
    }

    [Fact]
    public void RemoveVehicle_OfOtherUser_Throws()
    {
        // Arrange
        var vehicle = NewUser().RegisterVehicle(Guid.CreateVersion7(), "AA12BB");

        // Act + Assert
        Assert.Throws<ArgumentException>(() => NewUser().RemoveVehicle(vehicle));
    }

    [Fact]
    public void AddPaymentMethod_EventHasNoProviderToken()
    {
        // Arrange
        var user = NewUser();

        // Act
        var method = user.AddPaymentMethod(Guid.CreateVersion7(), PaymentMethodType.Card, "tok_secret", "**** 4242", null);

        // Assert
        var raised = Assert.Single(user.DequeueRaisedEvents());
        var added = Assert.IsType<PaymentMethodAdded>(raised.Event);
        Assert.Same(method, Assert.Single(raised.Changed));
        Assert.DoesNotContain("tok_secret", added.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RemovePaymentMethod_ChangesOnlyTheMethod()
    {
        // Arrange
        var user = NewUser();
        var method = user.AddPaymentMethod(Guid.CreateVersion7(), PaymentMethodType.MbWay, "tok", "*** 123", null);
        user.DequeueRaisedEvents();

        // Act
        user.RemovePaymentMethod(method);

        // Assert
        var raised = Assert.Single(user.DequeueRaisedEvents());
        Assert.IsType<PaymentMethodRemoved>(raised.Event);
        Assert.Same(method, Assert.Single(raised.Changed));
    }

    private static User NewUser()
    {
        var user = User.Register(Guid.CreateVersion7(), "Ana", "ana@example.pt");
        user.DequeueRaisedEvents();
        return user;
    }
}
