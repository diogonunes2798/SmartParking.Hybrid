using SmartParking.Application.Abstractions;

namespace SmartParking.Infrastructure.Hosting;

/// <summary>Actor of every change while there is no authentication (spec 001). Replaced in F11.</summary>
internal sealed class AnonymousCurrentActor : ICurrentActor
{
    public const string AnonymousName = "anonymous";

    public string Name => AnonymousName;
}
