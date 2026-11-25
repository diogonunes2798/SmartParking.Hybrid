namespace SmartParking.Api.Features.Users;

/// <summary>Body of <c>POST /api/users</c>. The email is stored in lower case.</summary>
internal sealed record RegisterUserRequest(string? Name, string? Email);
