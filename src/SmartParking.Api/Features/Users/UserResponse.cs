using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Api.Features.Users;

/// <summary>A user on the wire.</summary>
internal sealed record UserResponse(Guid Id, string Name, string Email)
{
    public static UserResponse From(UserView view) => new(view.Id, view.Name, view.Email);
}
