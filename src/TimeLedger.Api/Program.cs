using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;
using TimeLedger.Api.Auth;
using TimeLedger.Api.Data;
using TimeLedger.Api.Endpoints;
using TimeLedger.Api.Infrastructure;
using TimeLedger.Api.Integrations;
using TimeLedger.Api.Workers;
using TimeLedger.Domain.Integration;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Persistence
builder.Services.AddDbContext<TimeLedgerDbContext>(o =>
    o.UseNpgsql(config.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.")));
builder.Services.AddScoped<TimesheetService>();

// Redis: idempotency keys, presence cache, distributed lock
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(config.GetConnectionString("Redis") ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.")));
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<PresenceCache>();
builder.Services.AddSingleton<RedisLock>();

// Integrations: connectors + transactional outbox workers
builder.Services.AddSingleton<IPayrollConnector, GenericWebhookConnector>();
builder.Services.AddSingleton<IPayrollConnector, GustoStyleConnector>();
builder.Services.AddSingleton<ConnectorRegistry>();
builder.Services.AddSingleton(new RetryPolicy());
builder.Services.AddHttpClient("webhooks", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("TimeLedger-Webhooks/1.0");
})
// A followed 302 turns the POST into a GET and reports the landing page's 200 as a successful export.
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<OutboxFanOutWorker>();
builder.Services.AddHostedService<WebhookDeliveryWorker>();
builder.Services.AddSingleton<MockPayrollProvider>();

builder.Services.AddSingleton(TimeProvider.System);

// Authentication and authorization
builder.Services.AddTimeLedgerAuth(config);
builder.Services.AddScoped<AccountProvisioner>();

// HTTP
builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "TimeLedger API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new()
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "The accessToken returned by POST /api/auth/login.",
    });
    o.AddSecurityRequirement(new()
    {
        [new OpenApiSecurityScheme { Reference = new() { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>(),
    });
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:5173" })
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// Resolve now, so a missing or weak signing key stops startup instead of failing the first login.
app.Services.GetRequiredService<TokenService>();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/mock"))
    {
        context.Response.Headers.CacheControl = "no-store"; // salaries and tokens
    }

    return next(context);
});
if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "TimeLedger API v1"));
}

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapEmployeeAndClockEndpoints();
app.MapLeaveEndpoints();
app.MapPayrollEndpoints();
if (app.Configuration.GetValue("MockProvider:Enabled", app.Environment.IsDevelopment()))
{
    MockPayrollProvider.MapMockPayrollProvider(app);
}

app.MapGet("/health", async (TimeLedgerDbContext db, IConnectionMultiplexer redis) =>
{
    var postgres = await db.Database.CanConnectAsync();
    var redisOk = redis.IsConnected;
    return postgres && redisOk
        ? Results.Ok(new { status = "healthy" })
        : Results.Json(new { status = "unhealthy", postgres, redis = redisOk }, statusCode: 503);
}).AllowAnonymous().ExcludeFromDescription();

await InitializeDatabaseAsync(app);
app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<TimeLedgerDbContext>();
    var accounts = services.GetRequiredService<AccountProvisioner>();

    await DatabaseMigrator.InitializeAsync(db, services.GetRequiredService<ILogger<Program>>(), async () =>
    {
        if (app.Configuration.GetValue("Seed:Enabled", app.Environment.IsDevelopment()))
        {
            await DevSeeder.SeedAsync(db, services.GetRequiredService<TimeProvider>(), app.Configuration);

            // The demo password is public, so the environment is checked as well as the flag.
            if (app.Environment.IsDevelopment())
            {
                await accounts.SeedDemoAccountsAsync();
            }
        }

        await accounts.EnsureAdminAsync();
    });
}

public partial class Program;
