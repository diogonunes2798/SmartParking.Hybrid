using SmartParking.Domain.Common;

namespace SmartParking.Domain.Tariffs;

/// <summary>Tariff of a zone (spec §3): validity period and calculation rules (RF-06).</summary>
public sealed class Tariff : IVersioned
{
    public const int ZoneMaxLength = 100;

    private Tariff()
    {
        Zone = string.Empty;
        Validity = null!;
        Rules = null!;
    }

    public Guid Id { get; private set; }

    public string Zone { get; private set; }

    public ValidityPeriod Validity { get; private set; }

    public TariffRules Rules { get; private set; }

    public uint Version { get; private set; }

    public static Tariff Create(string zone, ValidityPeriod validity, TariffRules rules)
    {
        var tariff = new Tariff { Id = Guid.CreateVersion7() };
        tariff.Update(zone, validity, rules);
        return tariff;
    }

    public void Update(string zone, ValidityPeriod validity, TariffRules rules)
    {
        ArgumentNullException.ThrowIfNull(validity);
        ArgumentNullException.ThrowIfNull(rules);
        Zone = Guard.Text(zone, nameof(zone), ZoneMaxLength);
        Validity = validity;
        Rules = rules;
    }
}

/// <summary>Half-open interval [<see cref="ValidFrom"/>, <see cref="ValidTo"/>); open-ended when <see cref="ValidTo"/> is null.</summary>
public sealed record ValidityPeriod
{
    public ValidityPeriod(DateTimeOffset validFrom, DateTimeOffset? validTo)
    {
        // Compare after normalising to the stored precision, so the domain and ck_tariffs_validity agree.
        ValidFrom = Guard.Utc(validFrom);
        ValidTo = validTo is { } to ? Guard.Utc(to) : null;

        if (ValidTo is { } end && end <= ValidFrom)
        {
            throw new DomainValidationException("validity.validTo", "must be later than validFrom");
        }
    }

    public DateTimeOffset ValidFrom { get; }

    public DateTimeOffset? ValidTo { get; }

    public bool Contains(DateTimeOffset instant) => instant >= ValidFrom && (ValidTo is null || instant < ValidTo);
}

/// <summary>
/// Calculation rules of a tariff, stored as a typed JSON document (ADR-0003): new rule kinds can be added
/// without a relational migration. Amounts in EUR (single currency in the prototype).
/// TODO(Diogo): this is a placeholder model (hourly rate, free minutes, daily cap). Replace it with the
/// rule model of Chapter 3 when it is defined; the spec only says «regras de cálculo».
/// </summary>
public sealed record TariffRules
{
    public TariffRules(decimal hourlyRate, int freeMinutes, decimal? dailyCap)
    {
        if (hourlyRate < 0)
        {
            throw new DomainValidationException("rules.hourlyRate", "must not be negative");
        }

        if (freeMinutes < 0)
        {
            throw new DomainValidationException("rules.freeMinutes", "must not be negative");
        }

        if (dailyCap is < 0)
        {
            throw new DomainValidationException("rules.dailyCap", "must not be negative");
        }

        HourlyRate = hourlyRate;
        FreeMinutes = freeMinutes;
        DailyCap = dailyCap;
    }

    public decimal HourlyRate { get; }

    public int FreeMinutes { get; }

    public decimal? DailyCap { get; }
}
