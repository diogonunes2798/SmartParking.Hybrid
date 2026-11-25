using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Persistence;

namespace SmartParking.UnitTests.Application;

public sealed class ConcurrencyCheckTests
{
    [Fact]
    public void Verify_NoExpectedVersion_IsPreconditionRequired()
    {
        // Act
        var error = ConcurrencyCheck.Verify(null, 42);

        // Assert
        Assert.Equal(ErrorType.PreconditionRequired, error?.Type);
    }

    [Fact]
    public void Verify_DifferentVersion_IsPreconditionFailed()
    {
        // Act
        var error = ConcurrencyCheck.Verify(41, 42);

        // Assert
        Assert.Equal(ErrorType.PreconditionFailed, error?.Type);
    }

    [Fact]
    public void Verify_SameVersion_IsNull()
    {
        // Act
        var error = ConcurrencyCheck.Verify(42, 42);

        // Assert
        Assert.Null(error);
    }
}
