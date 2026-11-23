using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TimeLedger.Api.Data;
using TimeLedger.Domain;

namespace TimeLedger.Api.Auth;

public static class Policies
{
    public const string SignedIn = nameof(SignedIn);
    public const string LeaveApprover = nameof(LeaveApprover);
    public const string PayrollAdmin = nameof(PayrollAdmin);
}

public static class AuthSetup
{
    private const string RoleClaim = "role";

    public static IServiceCollection AddTimeLedgerAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AuthOptions>()
            .Bind(config.GetSection(AuthOptions.Section))
            .Validate(o => o.SessionMinutes is >= 1 and <= 43_200, "Auth:SessionMinutes must be between 1 and 43200.")
            .Validate(o => o.MaxFailedLogins >= 1 && o.LockoutMinutes >= 1, "Auth lockout settings must be positive.")
            .ValidateOnStart();

        services.AddSingleton<TokenService>();
        services.AddSingleton<Passwords>();
        services.AddSingleton<LoginThrottle>();
        services.AddScoped<CurrentUser>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenService, IOptions<AuthOptions>, TimeProvider>(ConfigureBearer);

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.SignedIn, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.LeaveApprover, p => p.RequireAuthenticatedUser()
                .RequireClaim(RoleClaim, nameof(UserRole.Supervisor), nameof(UserRole.PayrollAdmin)))
            .AddPolicy(Policies.PayrollAdmin, p => p.RequireAuthenticatedUser()
                .RequireClaim(RoleClaim, nameof(UserRole.PayrollAdmin)))
            // An endpoint someone forgets to annotate is closed, not open.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }

    private static void ConfigureBearer(JwtBearerOptions bearer, TokenService tokens, IOptions<AuthOptions> options, TimeProvider time)
    {
        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = options.Value.Issuer,
            ValidAudience = options.Value.Audience,
            IssuerSigningKey = tokens.Key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return expires > now && (notBefore is null || notBefore <= now.AddSeconds(30));
            },
        };
        bearer.Events = new JwtBearerEvents { OnTokenValidated = LoadSessionAsync };
    }

    /// <summary>One indexed query per request buys immediate logout, deactivation and role changes.</summary>
    private static async Task LoadSessionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId) ||
            !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sid), out var sessionId))
        {
            context.Fail("Malformed token.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var db = services.GetRequiredService<TimeLedgerDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        var user = await (
            from s in db.AuthSessions.AsNoTracking()
            join u in db.UserAccounts.AsNoTracking() on s.UserId equals u.Id
            where s.Id == sessionId && u.Id == userId && s.ExpiresAt > now
                  && s.SecurityStamp == u.SecurityStamp && u.IsActive
                  && (u.EmployeeId == null || db.Employees.Any(e => e.Id == u.EmployeeId && e.IsActive))
            select u).FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (user is null)
        {
            context.Fail("The session has ended.");
            return;
        }

        services.GetRequiredService<CurrentUser>().SignIn(user, sessionId);

        // A fresh identity, so the only role claim in play is the one from the database.
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim(RoleClaim, user.Role.ToString())],
            context.Scheme.Name));
    }
}
