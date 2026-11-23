using TimeLedger.Domain;
using TimeLedger.Domain.Entities;

namespace TimeLedger.Api.Contracts;

// These records carry passwords and tokens, so their ToString must never print them.
public sealed record LoginRequest(string? Email, string? Password)
{
    public override string ToString() => nameof(LoginRequest);
}

public sealed record LoginResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, MeDto User)
{
    public override string ToString() => nameof(LoginResponse);
}

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword)
{
    public override string ToString() => nameof(ChangePasswordRequest);
}

public sealed record CreateUserRequest(string? Email, string? Password, UserRole Role, Guid? EmployeeId)
{
    public override string ToString() => nameof(CreateUserRequest);
}

/// <summary>Fields left null are unchanged. A change to any of them signs the user out.</summary>
public sealed record UpdateUserRequest(UserRole? Role, bool? IsActive, string? NewPassword)
{
    public override string ToString() => nameof(UpdateUserRequest);
}

public sealed record MeDto(Guid Id, string Email, UserRole Role, Guid? EmployeeId, string? FullName);

public sealed record UserDto(Guid Id, string Email, UserRole Role, Guid? EmployeeId, bool IsActive, DateTimeOffset CreatedAt)
{
    public static UserDto From(UserAccount u) => new(u.Id, u.Email, u.Role, u.EmployeeId, u.IsActive, u.CreatedAt);
}
