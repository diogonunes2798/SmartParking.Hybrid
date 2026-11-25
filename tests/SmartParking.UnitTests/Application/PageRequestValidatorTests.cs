using SmartParking.Application.Abstractions.Paging;

namespace SmartParking.UnitTests.Application;

public sealed class PageRequestValidatorTests
{
    private readonly PageRequestValidator validator = new();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, PageRequest.DefaultPageSize)]
    [InlineData(3, PageRequest.MaxPageSize)]
    public void Validate_WithinLimits_IsValid(int page, int pageSize)
    {
        // Act
        var result = validator.Validate(new PageRequest(page, pageSize));

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0, 20, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, PageRequest.MaxPageSize + 1, "pageSize")]
    public void Validate_OutsideLimits_ReportsTheField(int page, int pageSize, string field)
    {
        // Act
        var result = validator.Validate(new PageRequest(page, pageSize));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == field);
    }

    [Fact]
    public void Offset_SecondPageOfTen_SkipsTen()
    {
        // Act
        var request = new PageRequest(Page: 2, PageSize: 10);

        // Assert
        Assert.Equal(10, request.Offset);
    }
}
