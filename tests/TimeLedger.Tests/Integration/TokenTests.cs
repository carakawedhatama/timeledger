using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TimeLedger.Domain;

namespace TimeLedger.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class TokenTests(Containers containers)
{
    public enum Forgery
    {
        WrongKey,
        WrongIssuer,
        WrongAudience,
        MissingIssuer,
        MissingAudience,
        Expired,
        NoExpiry,
        NotYetValid,
        OtherAlgorithm,
        AlgNone,
        Unsigned,
        TamperedPayload,
        UnknownSession,
        MissingSession,
        MalformedSession,
        AnotherUsersSession,
        MissingSubject,
        UnknownSubject,
    }

    public static TheoryData<Forgery> Forgeries() => new(Enum.GetValues<Forgery>());

    [Fact]
    public async Task A_correctly_rebuilt_token_is_accepted()
    {
        // The control: without it, every forgery below could be failing for the wrong reason.
        var person = await containers.Shared.PersonAsync(UserRole.Employee);

        await containers.Shared.WithToken(Mint(Claims(person))).Get("/api/auth/me").Expect(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(Forgeries))]
    public async Task Forged_token_is_rejected(Forgery forgery)
    {
        var person = await containers.Shared.PersonAsync(UserRole.Employee);
        var claims = Claims(person);
        var now = DateTime.UtcNow;
        string token;

        switch (forgery)
        {
            case Forgery.WrongKey: token = Mint(claims, key: new string('k', 64)); break;
            case Forgery.WrongIssuer: token = Mint(claims, issuer: "someone-else"); break;
            case Forgery.WrongAudience: token = Mint(claims, audience: "another-api"); break;
            case Forgery.MissingIssuer: token = Mint(claims, issuer: null); break;
            case Forgery.MissingAudience: token = Mint(claims, audience: null); break;
            case Forgery.Expired: token = Mint(claims, notBefore: now.AddHours(-2), expires: now.AddSeconds(-2)); break;
            case Forgery.NoExpiry: token = Mint(claims, withExpiry: false); break;
            case Forgery.NotYetValid: token = Mint(claims, notBefore: now.AddMinutes(10), expires: now.AddHours(1)); break;
            case Forgery.OtherAlgorithm:
                token = Mint(claims, algorithm: SecurityAlgorithms.HmacSha512); // the right key, the wrong algorithm
                break;
            case Forgery.AlgNone:
                token = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""") + "." + person.Api.Token!.Split('.')[1] + ".";
                break;
            case Forgery.Unsigned: token = person.Api.Token![..(person.Api.Token!.LastIndexOf('.') + 1)]; break;
            case Forgery.TamperedPayload:
                var parts = person.Api.Token!.Split('.');
                var payload = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!;
                payload["role"] = "PayrollAdmin";
                token = $"{parts[0]}.{Base64UrlEncoder.Encode(payload.ToJsonString())}.{parts[2]}";
                break;
            case Forgery.UnknownSession: claims["sid"] = Guid.NewGuid().ToString(); token = Mint(claims); break;
            case Forgery.MissingSession: claims.Remove("sid"); token = Mint(claims); break;
            case Forgery.MalformedSession: claims["sid"] = "' OR 1=1 --"; token = Mint(claims); break;
            case Forgery.AnotherUsersSession:
                claims["sid"] = Claims(await containers.Shared.PersonAsync(UserRole.PayrollAdmin))["sid"];
                token = Mint(claims);
                break;
            case Forgery.MissingSubject: claims.Remove("sub"); token = Mint(claims); break;
            case Forgery.UnknownSubject: claims["sub"] = Guid.NewGuid().ToString(); token = Mint(claims); break;
            default: throw new ArgumentOutOfRangeException(nameof(forgery));
        }

        await containers.Shared.WithToken(token).Get("/api/auth/me").Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Role_claim_in_a_validly_signed_token_is_ignored()
    {
        var person = await containers.Shared.PersonAsync(UserRole.Employee);
        var claims = Claims(person);
        claims["role"] = "PayrollAdmin";
        var caller = containers.Shared.WithToken(Mint(claims));

        Assert.Equal("Employee", (await caller.Get("/api/auth/me").Expect(HttpStatusCode.OK)).Str("role"));
        await caller.Get("/api/users").Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Token_holds_no_role_email_or_pay()
    {
        var person = await containers.Shared.PersonAsync(UserRole.Employee);

        var payload = JsonNode.Parse(Base64UrlEncoder.Decode(person.Api.Token!.Split('.')[1]))!.AsObject();

        Assert.Equal(["aud", "exp", "iat", "iss", "nbf", "sid", "sub"], payload.Select(p => p.Key).Order());
    }

    [Fact]
    public async Task Token_in_the_query_string_is_not_accepted()
    {
        var person = await containers.Shared.PersonAsync(UserRole.Employee);

        await containers.Shared.Anonymous.Get($"/api/auth/me?access_token={person.Api.Token}").Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Other_authentication_schemes_are_not_accepted()
    {
        var caller = containers.Shared.Anonymous;
        caller.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{containers.Shared.AdminEmail}:{TestApp.Password}")));

        await caller.Get("/api/auth/me").Expect(HttpStatusCode.Unauthorized);
    }

    private static Dictionary<string, object> Claims(Person person)
    {
        var jwt = new JsonWebToken(person.Api.Token);
        return new Dictionary<string, object> { ["sub"] = jwt.Subject, ["sid"] = jwt.GetClaim("sid").Value };
    }

    private static string Mint(
        Dictionary<string, object> claims, string key = TestApp.SigningKey, string? issuer = "timeledger",
        string? audience = "timeledger-api", string algorithm = SecurityAlgorithms.HmacSha256,
        DateTime? notBefore = null, DateTime? expires = null, bool withExpiry = true) =>
        new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            Issuer = issuer,
            Audience = audience,
            NotBefore = notBefore,
            Expires = withExpiry ? expires ?? DateTime.UtcNow.AddHours(1) : null,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm),
        });
}
