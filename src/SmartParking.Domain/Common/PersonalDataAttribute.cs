namespace SmartParking.Domain.Common;

/// <summary>
/// Marks a property holding personal data (GDPR, RNF-06). Its values are masked in the audit log:
/// the audit records <em>that</em> it changed, not <em>what</em> it was.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class PersonalDataAttribute : Attribute;
