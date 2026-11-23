namespace TimeLedger.Api.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public const int MinSigningKeyBytes = 32;

    /// <summary>HS256 key. Required outside Development, where a random per-process key is used if empty.</summary>
    public string? SigningKey { get; set; }

    public string Issuer { get; set; } = "timeledger";
    public string Audience { get; set; } = "timeledger-api";
    public int SessionMinutes { get; set; } = 480;
    public int MaxFailedLogins { get; set; } = 10;
    public int LockoutMinutes { get; set; } = 5;
    public BootstrapAdminOptions BootstrapAdmin { get; set; } = new();
}

/// <summary>Creates the first payroll admin. Ignored once an active one exists.</summary>
public sealed class BootstrapAdminOptions
{
    public string? Email { get; set; }
    public string? Password { get; set; }
}
