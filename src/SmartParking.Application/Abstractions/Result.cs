namespace SmartParking.Application.Abstractions;

/// <summary>Outcome of an operation without a value: success, or a typed <see cref="Abstractions.Error"/>.</summary>
public class Result
{
    protected Result(Error? error) => Error = error;

    public bool IsSuccess => Error is null;

    public bool IsFailure => !IsSuccess;

    /// <summary>The failure; <c>null</c> when the operation succeeded.</summary>
    public Error? Error { get; }

    public static Result Success() => new(null);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(error);
    }

    public static Result<TValue> Success<TValue>(TValue value) => new(value, null);

    public static Result<TValue> Failure<TValue>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }

    public static implicit operator Result(Error error) => Failure(error);
}
