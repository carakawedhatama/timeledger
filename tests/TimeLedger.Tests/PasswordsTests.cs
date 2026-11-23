using System.Buffers.Binary;
using TimeLedger.Api.Auth;
using TimeLedger.Api.Contracts;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;

namespace TimeLedger.Tests;

public class PasswordsTests
{
    private const string Secret = "correct horse battery";
    private static readonly Passwords Passwords = new();

    private static UserAccount User()
    {
        var user = new UserAccount { Email = "a@example.com" };
        user.PasswordHash = Passwords.Hash(user, Secret);
        return user;
    }

    [Theory]
    [InlineData(Secret, true)]
    [InlineData("Correct horse battery", false)]
    [InlineData("correct horse battery ", false)]
    [InlineData("", false)]
    public void Verify_accepts_only_the_exact_password(string attempt, bool expected)
    {
        Assert.Equal(expected, Passwords.Verify(User(), attempt));
    }

    [Fact]
    public void Unknown_user_never_verifies()
    {
        Assert.False(Passwords.Verify(null, Secret));
    }

    [Fact]
    public void Hashes_are_salted_and_do_not_contain_the_password()
    {
        var (a, b) = (User().PasswordHash, User().PasswordHash);

        Assert.NotEqual(a, b);
        Assert.DoesNotContain(Secret, a);
    }

    [Fact]
    public void Hash_uses_pbkdf2_sha512_with_210k_iterations()
    {
        var bytes = Convert.FromBase64String(User().PasswordHash);

        Assert.Equal(1, bytes[0]); // Identity format v3
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1))); // HMAC-SHA512
        Assert.Equal(210_000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("            ", false)]
    [InlineData("elevenchars", false)]
    [InlineData("twelve_chars", true)]
    [InlineData("SOMEONE@EXAMPLE.COM", false)]
    public void Password_policy(string? password, bool valid)
    {
        Assert.Equal(valid, Passwords.Validate(password, "someone@example.com") is null);
    }

    [Fact]
    public void Password_length_is_capped()
    {
        Assert.Null(Passwords.Validate(new string('x', 128), "a@b.c"));
        Assert.NotNull(Passwords.Validate(new string('x', 129), "a@b.c"));
    }

    [Fact]
    public void Requests_and_responses_do_not_print_secrets()
    {
        object[] records =
        [
            new LoginRequest("a@b.c", Secret),
            new ChangePasswordRequest(Secret, Secret),
            new CreateUserRequest("a@b.c", Secret, UserRole.Employee, null),
            new UpdateUserRequest(null, null, Secret),
            new LoginResponse(Secret, "Bearer", default, new MeDto(Guid.Empty, "a@b.c", UserRole.Employee, null, null)),
        ];

        Assert.All(records, r => Assert.DoesNotContain(Secret, r.ToString()));
    }
}
