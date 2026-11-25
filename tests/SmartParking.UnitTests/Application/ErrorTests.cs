using SmartParking.Application.Abstractions;

namespace SmartParking.UnitTests.Application;

/// <summary>The 402 error of a declined payment carries its <c>paymentId</c> (spec 002).</summary>
public sealed class ErrorTests
{
    [Fact]
    public void PaymentDeclined_HasTypeAndPaymentIdExtension()
    {
        // Arrange
        var paymentId = Guid.CreateVersion7();

        // Act
        var error = Error.PaymentDeclined("Session.PaymentDeclined", "Declined.", paymentId);

        // Assert
        Assert.Equal(ErrorType.PaymentDeclined, error.Type);
        Assert.Equal(paymentId, Assert.Single(error.Extensions, extension => extension.Key == "paymentId").Value);
        Assert.Empty(error.ValidationErrors);
    }

    [Fact]
    public void OtherErrors_HaveNoExtensions()
    {
        // Act
        var error = Error.Conflict("Test.Conflict", "Conflict.");

        // Assert
        Assert.Empty(error.Extensions);
    }
}
