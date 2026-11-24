using SmartParking.Application.Abstractions;

namespace SmartParking.UnitTests.Application;

public sealed class ResultTests
{
    [Fact]
    public void Success_WithValue_ExposesValueAndNoError()
    {
        // Act
        Result<int> result = 42;

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Failure_FromError_ExposesErrorAndHidesValue()
    {
        // Arrange
        var error = Error.NotFound("Spot.NotFound", "Spot not found.");

        // Act
        Result<int> result = error;

        // Assert
        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Theory]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.PreconditionFailed)]
    [InlineData(ErrorType.PreconditionRequired)]
    public void ErrorFactory_NonValidationType_HasTypeAndNoFieldErrors(ErrorType type)
    {
        // Act
        var error = type switch
        {
            ErrorType.NotFound => Error.NotFound("Code", "Description"),
            ErrorType.Conflict => Error.Conflict("Code", "Description"),
            ErrorType.PreconditionFailed => Error.PreconditionFailed("Code", "Description"),
            ErrorType.PreconditionRequired => Error.PreconditionRequired("Code", "Description"),
            _ => Error.Unauthorized("Code", "Description"),
        };

        // Assert
        Assert.Equal(type, error.Type);
        Assert.Empty(error.ValidationErrors);
    }

    [Fact]
    public void ErrorValidation_WithFieldErrors_KeepsTheDictionary()
    {
        // Arrange
        var errors = new Dictionary<string, string[]> { ["eventId"] = ["Required."] };

        // Act
        var error = Error.Validation("Event.Invalid", "Invalid event.", errors);

        // Assert
        Assert.Equal(ErrorType.Validation, error.Type);
        Assert.Equal(["Required."], error.ValidationErrors["eventId"]);
    }

    [Fact]
    public void Success_WithoutValue_IsSuccess()
    {
        // Act
        var result = Result.Success();

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
    }
}
