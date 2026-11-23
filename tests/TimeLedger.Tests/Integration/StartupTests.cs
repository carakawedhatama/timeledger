using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TimeLedger.Api.Auth;
using TimeLedger.Api.Data;
using TimeLedger.Domain;

namespace TimeLedger.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class StartupTests(Containers containers)
{
    [Theory]
    [InlineData("Testing", "")]
    [InlineData("Production", "")]
    [InlineData("Production", "too-short")]
    [InlineData("Development", "too-short")]
    public void Missing_or_weak_signing_key_stops_startup(string environment, string key)
    {
        using var app = TestApp.Create(containers.NewDatabases(), environment, new() { ["Auth:SigningKey"] = key });

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains("Auth:SigningKey", error.ToString());
    }

    [Fact]
    public async Task Development_works_without_a_signing_key()
    {
        await using var app = await containers.StartAppAsync("Development", new() { ["Auth:SigningKey"] = "" });

        await app.Admin.Get("/api/auth/me").Expect(HttpStatusCode.OK);
    }

    [Fact]
    public void Weak_bootstrap_password_stops_startup()
    {
        using var app = TestApp.Create(containers.NewDatabases(), settings: new() { ["Auth:BootstrapAdmin:Password"] = "short" });

        Assert.Contains("BootstrapAdmin", Assert.ThrowsAny<Exception>(() => app.CreateClient()).ToString());
    }

    [Fact]
    public async Task No_bootstrap_settings_means_no_accounts()
    {
        await using var app = TestApp.Create(containers.NewDatabases(), settings: new() { ["Auth:BootstrapAdmin:Email"] = "" });

        Assert.Equal(0, await app.DbAsync(db => db.UserAccounts.CountAsync()));
    }

    [Fact]
    public async Task Bootstrap_never_overwrites_an_existing_admin()
    {
        var databases = containers.NewDatabases();
        await using (await TestApp.StartAsync(databases, "Testing", null))
        {
        }

        await using var restarted = TestApp.Create(databases, settings: new()
        {
            ["Auth:BootstrapAdmin:Email"] = "intruder@test.local",
            ["Auth:BootstrapAdmin:Password"] = "a-different-password",
        });

        Assert.Equal(["root@test.local"], await restarted.DbAsync(db => db.UserAccounts.Select(u => u.Email).ToListAsync()));
    }

    [Fact]
    public async Task Development_seed_creates_the_documented_demo_accounts()
    {
        await using var app = TestApp.Create(containers.NewDatabases(), "Development", new()
        {
            ["Seed:Enabled"] = "true",
            ["Auth:BootstrapAdmin:Email"] = "",
        });

        var admin = await app.LoginAsync(AccountProvisioner.DemoAdminEmail, AccountProvisioner.DemoPassword);
        var users = await admin.Get("/api/users").Expect(HttpStatusCode.OK);
        Assert.Equal(9, users.GetArrayLength());
        Assert.Equal(2, users.EnumerateArray().Count(u => u.Str("role") == nameof(UserRole.Supervisor)));

        foreach (var (name, visible) in new[] { ("ayu", 4), ("eka", 4), ("bima", 1) })
        {
            var api = await app.LoginAsync($"{name}@timeledger.local", AccountProvisioner.DemoPassword);
            Assert.Equal(visible, (await api.Get("/api/employees").Expect(HttpStatusCode.OK)).GetArrayLength());
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Seeding_outside_development_never_creates_demo_accounts(string environment)
    {
        await using var app = await containers.StartAppAsync(environment, new() { ["Seed:Enabled"] = "true" });

        await app.Anonymous.Post("/api/auth/login", new { email = AccountProvisioner.DemoAdminEmail, password = AccountProvisioner.DemoPassword })
            .Expect(HttpStatusCode.Unauthorized);
        Assert.Equal(1, await app.DbAsync(db => db.UserAccounts.CountAsync()));
    }

    [Fact]
    public async Task EnsureCreated_database_from_before_auth_is_upgraded_with_its_data()
    {
        var databases = containers.NewDatabases();
        var options = new DbContextOptionsBuilder<TimeLedgerDbContext>().UseNpgsql(databases.Postgres).Options;
        await using (var db = new TimeLedgerDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("InitialCreate");
            await db.Database.ExecuteSqlRawAsync("""DROP TABLE "__EFMigrationsHistory" """);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO employees (id, employee_code, full_name, email, monthly_base_salary, schedule, time_zone_id, is_active, created_at)
                VALUES (gen_random_uuid(), 'LEGACY', 'Legacy Person', 'legacy@company.test', 5000000, 'FiveDay', 'Asia/Jakarta', true, now())
                """);
        }

        await using var app = await TestApp.StartAsync(databases, "Testing", null);

        var employees = await app.Admin.Get("/api/employees").Expect(HttpStatusCode.OK);
        Assert.Equal("LEGACY", Assert.Single(employees.EnumerateArray()).Str("employeeCode"));
        Assert.Equal(2, await app.DbAsync(async db => (await db.Database.GetAppliedMigrationsAsync()).Count()));
    }

    [Fact]
    public async Task Model_and_migrations_are_in_sync()
    {
        var differences = await containers.Shared.DbAsync(db =>
        {
            var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
            var before = db.GetService<IModelRuntimeInitializer>().Initialize(
                ((Microsoft.EntityFrameworkCore.Metadata.IMutableModel)snapshot).FinalizeModel()).GetRelationalModel();
            var now = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetRelationalModel();
            return Task.FromResult(db.GetService<IMigrationsModelDiffer>().GetDifferences(before, now).Count);
        });

        Assert.Equal(0, differences);
    }

    [Fact]
    public async Task Replicas_can_start_together_on_an_empty_database()
    {
        var databases = containers.NewDatabases();
        await using (var db = new TimeLedgerDbContext(new DbContextOptionsBuilder<TimeLedgerDbContext>().UseNpgsql(databases.Postgres).Options))
        {
            await db.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().CreateAsync();
        }

        var apps = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => TestApp.StartAsync(databases, "Testing", null))));

        Assert.Equal(1, await apps[0].DbAsync(db => db.UserAccounts.CountAsync()));
        foreach (var app in apps)
        {
            await app.DisposeAsync();
        }
    }
}
