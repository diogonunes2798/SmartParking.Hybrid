namespace SmartParking.Application.Abstractions;

/// <summary>Expected failure categories. Each maps to one HTTP status code at the API boundary.</summary>
public enum ErrorType
{
    NotFound,
    Validation,
    Conflict,
    Unauthorized,

    /// <summary>The <c>If-Match</c> version is stale (spec 001: optimistic concurrency over <c>xmin</c>).</summary>
    PreconditionFailed,

    /// <summary>A <c>PUT</c> or <c>DELETE</c> arrived without the <c>If-Match</c> version (spec 001).</summary>
    PreconditionRequired,

    /// <summary>The payment gateway declined the charge (spec 002, 402); the failed payment is recorded.</summary>
    PaymentDeclined,
}
