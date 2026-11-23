namespace TimeLedger.Domain.Entities;

/// <summary>A sign-in account. Kept apart from <see cref="Employee"/>: not every admin is on the payroll.</summary>
public class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; }

    /// <summary>The employee this account acts as. Required for every role except payroll admin.</summary>
    public Guid? EmployeeId { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Replaced whenever existing sessions must stop working (password change, role change, deactivation).</summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optimistic concurrency token (mapped to PostgreSQL xmin).</summary>
    public uint Version { get; set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public void RevokeSessions() => SecurityStamp = Guid.NewGuid();
}

/// <summary>One sign-in. A token is only as valid as the session row behind it.</summary>
public class AuthSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>The account's security stamp when the password was verified.</summary>
    public Guid SecurityStamp { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
