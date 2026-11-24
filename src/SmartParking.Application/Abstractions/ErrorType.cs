namespace SmartParking.Application.Abstractions;

/// <summary>Expected failure categories. Each maps to one HTTP status code at the API boundary.</summary>
public enum ErrorType
{
    NotFound,
    Validation,
    Conflict,
    Unauthorized,
}
