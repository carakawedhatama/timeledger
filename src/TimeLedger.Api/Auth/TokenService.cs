using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TimeLedger.Domain.Entities;

namespace TimeLedger.Api.Auth;

public sealed class TokenService
{
    private readonly AuthOptions _options;

    public SymmetricSecurityKey Key { get; }

    public TokenService(IOptions<AuthOptions> options, IHostEnvironment environment, ILogger<TokenService> log)
    {
        _options = options.Value;

        byte[] key;
        if (string.IsNullOrWhiteSpace(_options.SigningKey))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Auth:SigningKey is required outside Development.");
            }

            key = RandomNumberGenerator.GetBytes(64);
            log.LogWarning("Auth:SigningKey is not set; using a random key. Sign-ins will not survive a restart.");
        }
        else
        {
            key = Encoding.UTF8.GetBytes(_options.SigningKey);
            if (key.Length < AuthOptions.MinSigningKeyBytes)
            {
                throw new InvalidOperationException($"Auth:SigningKey must be at least {AuthOptions.MinSigningKeyBytes} bytes.");
            }
        }

        Key = new SymmetricSecurityKey(key);
    }

    /// <summary>The token identifies a session and nothing else: role and team are read from the database on each request.</summary>
    public string Create(UserAccount user, AuthSession session, DateTimeOffset now) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = session.ExpiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Sid] = session.Id.ToString(),
            },
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
}
