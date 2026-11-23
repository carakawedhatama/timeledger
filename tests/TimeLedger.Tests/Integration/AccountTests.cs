using System.Net;
using Microsoft.EntityFrameworkCore;
using TimeLedger.Domain;

namespace TimeLedger.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class AccountTests(Containers containers)
{
    private const string NewPassword = "a-brand-new-password";
    private TestApp App => containers.Shared;

    [Fact]
    public async Task Login_returns_a_token_and_profile()
    {
        var person = await App.PersonAsync(UserRole.Supervisor);

        var body = await App.Anonymous.Post("/api/auth/login", new { email = person.Email, password = TestApp.Password }).Expect(HttpStatusCode.OK);

        Assert.Equal("Bearer", body.Str("tokenType"));
        Assert.Equal("Supervisor", body.GetProperty("user").Str("role"));
        Assert.Equal(person.EmployeeId, body.GetProperty("user").GetProperty("employeeId").GetGuid());
        Assert.StartsWith("Person ", body.GetProperty("user").Str("fullName"));
    }

    [Fact]
    public async Task Login_ignores_email_case_and_surrounding_space()
    {
        var person = await App.PersonAsync(UserRole.Employee);

        await App.LoginAsync($"  {person.Email.ToUpperInvariant()} ");
    }

    [Fact]
    public async Task Unknown_wrong_and_disabled_accounts_get_the_same_answer()
    {
        var active = await App.PersonAsync(UserRole.Employee);
        var disabled = await App.PersonAsync(UserRole.Employee);
        await App.Admin.Patch($"/api/users/{disabled.UserId}", new { isActive = false }).Expect(HttpStatusCode.OK);

        var answers = new HashSet<string>();
        foreach (var (email, password) in new[]
                 {
                     ("nobody@login.test", TestApp.Password), (active.Email, "wrong-password-here"), (disabled.Email, TestApp.Password),
                 })
        {
            answers.Add((await App.Anonymous.Post("/api/auth/login", new { email, password }).Expect(HttpStatusCode.Unauthorized)).Str("title"));
        }

        Assert.Single(answers);
    }

    [Theory]
    [InlineData(null, "some-password-123")]
    [InlineData("", "some-password-123")]
    [InlineData("not-an-email", "some-password-123")]
    [InlineData("a@b.c", null)]
    [InlineData("a@b.c", "")]
    public async Task Incomplete_login_is_a_400(string? email, string? password)
    {
        await App.Anonymous.Post("/api/auth/login", new { email, password }).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Oversized_password_is_refused_before_hashing()
    {
        await App.Anonymous.Post("/api/auth/login", new { email = "a@b.c", password = new string('x', 129) }).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sql_in_the_email_is_just_an_unknown_account()
    {
        await App.Anonymous.Post("/api/auth/login", new { email = "' OR '1'='1' --@x", password = "some-password-123" })
            .Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Nul_in_the_email_is_a_client_error()
    {
        await App.Anonymous.Post("/api/auth/login", new { email = "a\0b@c.d", password = "some-password-123" })
            .Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_for_the_right_password()
    {
        var person = await App.PersonAsync(UserRole.Employee);
        for (var i = 0; i < 10; i++)
        {
            await App.Anonymous.Post("/api/auth/login", new { email = person.Email, password = "wrong-password-here" }).Expect(HttpStatusCode.Unauthorized);
        }

        using var response = await App.Anonymous.Post("/api/auth/login", new { email = person.Email.ToUpperInvariant(), password = TestApp.Password });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 300);
    }

    [Fact]
    public async Task Parallel_guesses_cannot_slip_past_the_limit()
    {
        var person = await App.PersonAsync(UserRole.Employee);

        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ =>
            App.Anonymous.Post("/api/auth/login", new { email = person.Email, password = "wrong-password-here" })));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
        Assert.Equal(20, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_count()
    {
        var person = await App.PersonAsync(UserRole.Employee);
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 8; i++)
            {
                await App.Anonymous.Post("/api/auth/login", new { email = person.Email, password = "wrong-password-here" }).Expect(HttpStatusCode.Unauthorized);
            }

            await App.LoginAsync(person.Email);
        }
    }

    [Fact]
    public async Task Logout_ends_only_that_session()
    {
        var person = await App.PersonAsync(UserRole.Employee);
        var other = await App.LoginAsync(person.Email);

        await person.Api.Post("/api/auth/logout").Expect(HttpStatusCode.NoContent);

        await person.Api.Get("/api/auth/me").Expect(HttpStatusCode.Unauthorized);
        await other.Get("/api/auth/me").Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changing_the_password_ends_every_session()
    {
        var person = await App.PersonAsync(UserRole.Employee);
        var other = await App.LoginAsync(person.Email);

        await person.Api.Post("/api/auth/change-password", new { currentPassword = TestApp.Password, newPassword = NewPassword })
            .Expect(HttpStatusCode.NoContent);

        await person.Api.Get("/api/auth/me").Expect(HttpStatusCode.Unauthorized);
        await other.Get("/api/auth/me").Expect(HttpStatusCode.Unauthorized);
        await App.Anonymous.Post("/api/auth/login", new { email = person.Email, password = TestApp.Password }).Expect(HttpStatusCode.Unauthorized);
        await App.LoginAsync(person.Email, NewPassword);
    }

    [Theory]
    [InlineData("wrong-current-pass", NewPassword)]
    [InlineData(TestApp.Password, "short")]
    [InlineData(TestApp.Password, TestApp.Password)]
    [InlineData("", NewPassword)]
    public async Task Invalid_password_change_is_refused_without_signing_out(string currentPassword, string newPassword)
    {
        var person = await App.PersonAsync(UserRole.Employee);

        await person.Api.Post("/api/auth/change-password", new { currentPassword, newPassword }).Expect(HttpStatusCode.BadRequest);

        await person.Api.Get("/api/auth/me").Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_stolen_token_cannot_guess_the_password_without_limit()
    {
        var person = await App.PersonAsync(UserRole.Employee);
        var guess = new { currentPassword = "wrong-current-pass", newPassword = NewPassword };
        for (var i = 0; i < 10; i++)
        {
            await person.Api.Post("/api/auth/change-password", guess).Expect(HttpStatusCode.BadRequest);
        }

        await person.Api.Post("/api/auth/change-password", guess).Expect(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Deactivation_is_immediate_and_reactivation_does_not_revive_old_tokens()
    {
        var person = await App.PersonAsync(UserRole.Employee);

        await App.Admin.Patch($"/api/users/{person.UserId}", new { isActive = false }).Expect(HttpStatusCode.OK);
        await person.Api.Get("/api/auth/me").Expect(HttpStatusCode.Unauthorized);

        await App.Admin.Patch($"/api/users/{person.UserId}", new { isActive = true }).Expect(HttpStatusCode.OK);
        await person.Api.Get("/api/auth/me").Expect(HttpStatusCode.Unauthorized);
        await App.LoginAsync(person.Email);
    }

    [Fact]
    public async Task Deactivating_the_employee_shuts_out_their_account()
    {
        var person = await App.PersonAsync(UserRole.Employee);

        await App.DbAsync(db => db.Employees.Where(e => e.Id == person.EmployeeId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false)));

        await person.Api.Get("/api/auth/me").Expect(HttpStatusCode.Unauthorized);
        await App.Anonymous.Post("/api/auth/login", new { email = person.Email, password = TestApp.Password }).Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Demotion_takes_effect_immediately()
    {
        var admin = await App.PersonAsync(UserRole.PayrollAdmin);

        await App.Admin.Patch($"/api/users/{admin.UserId}", new { role = "Employee" }).Expect(HttpStatusCode.OK);

        await admin.Api.Get("/api/users").Expect(HttpStatusCode.Unauthorized);
        await (await App.LoginAsync(admin.Email)).Get("/api/users").Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unchanged_patch_keeps_the_session()
    {
        var person = await App.PersonAsync(UserRole.Employee);

        await App.Admin.Patch($"/api/users/{person.UserId}", new { role = "Employee", isActive = true }).Expect(HttpStatusCode.OK);

        await person.Api.Get("/api/auth/me").Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_password_reset_revokes_sessions_and_clears_the_lockout()
    {
        var person = await App.PersonAsync(UserRole.Employee);
        await Task.WhenAll(Enumerable.Range(0, 11).Select(_ =>
            App.Anonymous.Post("/api/auth/login", new { email = person.Email, password = "wrong-password-here" })));

        await App.Admin.Patch($"/api/users/{person.UserId}", new { newPassword = NewPassword }).Expect(HttpStatusCode.OK);

        await person.Api.Get("/api/auth/me").Expect(HttpStatusCode.Unauthorized);
        await App.LoginAsync(person.Email, NewPassword);
    }

    [Theory]
    [InlineData("role", "Employee")]
    [InlineData("isActive", false)]
    [InlineData("newPassword", NewPassword)]
    public async Task Admins_cannot_administer_themselves(string field, object value)
    {
        var admin = await App.PersonAsync(UserRole.PayrollAdmin);

        await admin.Api.Patch($"/api/users/{admin.UserId}", new Dictionary<string, object> { [field] = value }).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Two_admins_demoting_each_other_leave_one_admin()
    {
        for (var round = 0; round < 5; round++)
        {
            var a = await App.PersonAsync(UserRole.PayrollAdmin);
            var b = await App.PersonAsync(UserRole.PayrollAdmin);

            var results = await Task.WhenAll(
                a.Api.Patch($"/api/users/{b.UserId}", new { role = "Employee" }),
                b.Api.Patch($"/api/users/{a.UserId}", new { role = "Employee" }));

            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        }
    }

    [Theory]
    [InlineData("Employee")]
    [InlineData("Supervisor")]
    public async Task Non_admin_accounts_need_an_employee(string role)
    {
        await App.Admin.Post("/api/users", new { email = $"{Guid.NewGuid():N}@login.test", password = TestApp.Password, role })
            .Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unlinked_admin_cannot_become_a_supervisor()
    {
        var user = await App.Admin.Post("/api/users", new { email = $"{Guid.NewGuid():N}@login.test", password = TestApp.Password, role = "PayrollAdmin" })
            .Expect(HttpStatusCode.Created);

        await App.Admin.Patch($"/api/users/{user.Id()}", new { role = "Supervisor" }).Expect(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("not-an-email", TestApp.Password, "PayrollAdmin")]
    [InlineData("weak@login.test", "short", "PayrollAdmin")]
    [InlineData("role@login.test", TestApp.Password, "Owner")]
    [InlineData("role2@login.test", TestApp.Password, 3)]
    [InlineData("role3@login.test", TestApp.Password, null)]
    public async Task Invalid_account_is_refused(string email, string password, object? role)
    {
        await App.Admin.Post("/api/users", new { email, password, role }).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Email_and_employee_are_unique_per_account()
    {
        var person = await App.PersonAsync(UserRole.Employee);

        await App.Admin.Post("/api/users", new { email = person.Email.ToUpperInvariant(), password = TestApp.Password, role = "PayrollAdmin" })
            .Expect(HttpStatusCode.Conflict);
        await App.Admin.Post("/api/users", new { email = "second@login.test", password = TestApp.Password, role = "Employee", employeeId = person.EmployeeId })
            .Expect(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Account_listing_never_includes_hashes_or_stamps()
    {
        var text = (await App.Admin.Get("/api/users").Expect(HttpStatusCode.OK)).GetRawText();

        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stamp", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sessions_expire()
    {
        await using var app = await containers.StartAppAsync(settings: new() { ["Auth:SessionMinutes"] = "30" });

        app.Clock.Advance(TimeSpan.FromMinutes(29));
        await app.Admin.Get("/api/auth/me").Expect(HttpStatusCode.OK);

        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await app.Admin.Get("/api/auth/me").Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_prunes_that_users_expired_sessions()
    {
        await using var app = await containers.StartAppAsync();
        app.Clock.Advance(TimeSpan.FromHours(9));

        await app.LoginAsync(app.AdminEmail);

        Assert.Equal(1, await app.DbAsync(db => db.AuthSessions.CountAsync()));
    }
}
