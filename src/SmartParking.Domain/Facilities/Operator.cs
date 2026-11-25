using SmartParking.Domain.Common;

namespace SmartParking.Domain.Facilities;

/// <summary>
/// Company that runs parking facilities. In F2 there are no operator endpoints (F11):
/// the reference operator is seeded by the <c>Initial</c> migration.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Domain term fixed by the thesis glossary (Operador → Operator); the assembly is consumed from C# only.")]
public sealed class Operator
{
    public const int NameMaxLength = 200;

    public const string ReferenceOperatorName = "Reference Operator";

    /// <summary>Fixed identifier of the seeded reference operator (UUID v7 layout).</summary>
    public static readonly Guid ReferenceOperatorId = new("0193a1b0-0000-7000-8000-000000000001");

    private Operator()
    {
    }

    public Guid Id { get; private init; }

    public string Name { get; private set; } = string.Empty;

    public static Operator Create(Guid id, string name) => new()
    {
        Id = Guard.Id(id),
        Name = Guard.Text(name, NameMaxLength),
    };
}
