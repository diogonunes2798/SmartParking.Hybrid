namespace SmartParking.Application.Abstractions;

/// <summary>
/// Outcome of an operation that produces a value on success.
/// Create it with <see cref="Result.Success{TValue}(TValue)"/>, <see cref="Result.Failure{TValue}(Error)"/>
/// or the implicit conversions from a value or an <see cref="Abstractions.Error"/>.
/// </summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? value;

    internal Result(TValue? value, Error? error)
        : base(error) => this.value = value;

    /// <summary>The value. Reading it from a failed result is a programming error.</summary>
    public TValue Value => IsSuccess
        ? value!
        : throw new InvalidOperationException("The value of a failed result cannot be read.");

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}
