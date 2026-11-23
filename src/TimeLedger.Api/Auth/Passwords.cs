using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TimeLedger.Domain.Entities;

namespace TimeLedger.Api.Auth;

public sealed class Passwords
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    // PBKDF2-HMAC-SHA512 at OWASP's recommended work factor; the framework default is 100k.
    private const int Iterations = 210_000;

    private readonly PasswordHasher<UserAccount> _hasher =
        new(Options.Create(new PasswordHasherOptions { IterationCount = Iterations }));

    private readonly UserAccount _nobody = new() { Email = string.Empty };

    public Passwords() => _nobody.PasswordHash = Hash(_nobody, Guid.NewGuid().ToString());

    public string Hash(UserAccount user, string password) => _hasher.HashPassword(user, password);

    /// <summary>An unknown account costs the same hashing work as a known one, so timing doesn't reveal which emails exist.</summary>
    public bool Verify(UserAccount? user, string password)
    {
        var target = user ?? _nobody;
        var matches = _hasher.VerifyHashedPassword(target, target.PasswordHash, password) != PasswordVerificationResult.Failed;
        return user is not null && matches;
    }

    public static string? Validate(string? password, string email)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length is < MinLength or > MaxLength)
        {
            return $"Must be {MinLength}–{MaxLength} characters.";
        }

        return string.Equals(password, email, StringComparison.OrdinalIgnoreCase) ? "Must not be the email address." : null;
    }
}
