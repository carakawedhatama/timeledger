using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TimeLedger.Domain;

namespace TimeLedger.Tests.Integration;

/// <summary>Every endpoint, for every kind of caller. A new endpoint fails the suite until it is listed here.</summary>
[Collection(IntegrationCollection.Name)]
public class EndpointAuthorizationTests(Containers containers)
{
    public enum Access
    {
        Public,
        SignedIn,
        Approver,
        Admin,
    }

    private static readonly (string Method, string Route, Access Access)[] Endpoints =
    [
        ("GET", "/health", Access.Public),
        ("POST", "/api/auth/login", Access.Public),
        ("POST", "/mock/payroll-provider/webhooks", Access.Public),

        ("GET", "/api/auth/me", Access.SignedIn),
        ("POST", "/api/auth/logout", Access.SignedIn),
        ("POST", "/api/auth/change-password", Access.SignedIn),
        ("GET", "/api/employees", Access.SignedIn),
        ("GET", "/api/employees/{id:guid}", Access.SignedIn),
        ("GET", "/api/employees/{id:guid}/clock-events", Access.SignedIn),
        ("GET", "/api/employees/{id:guid}/leave-balance", Access.SignedIn),
        ("POST", "/api/clock-events", Access.SignedIn),
        ("GET", "/api/presence", Access.SignedIn),
        ("GET", "/api/leave-requests", Access.SignedIn),
        ("POST", "/api/leave-requests", Access.SignedIn),
        ("GET", "/api/holidays", Access.SignedIn),
        ("GET", "/api/pay-periods", Access.SignedIn),
        ("GET", "/api/pay-periods/{id:guid}/timesheets", Access.SignedIn),

        ("POST", "/api/leave-requests/{id:guid}/approve", Access.Approver),
        ("POST", "/api/leave-requests/{id:guid}/reject", Access.Approver),

        ("POST", "/api/employees", Access.Admin),
        ("PUT", "/api/employees/{id:guid}/supervisor", Access.Admin),
        ("GET", "/api/users", Access.Admin),
        ("POST", "/api/users", Access.Admin),
        ("PATCH", "/api/users/{id:guid}", Access.Admin),
        ("PUT", "/api/holidays/{date}", Access.Admin),
        ("POST", "/api/pay-periods", Access.Admin),
        ("POST", "/api/pay-periods/{id:guid}/close", Access.Admin),
        ("GET", "/api/integrations", Access.Admin),
        ("POST", "/api/integrations", Access.Admin),
        ("POST", "/api/integrations/{id:guid}/enabled", Access.Admin),
        ("GET", "/api/deliveries", Access.Admin),
        ("POST", "/api/deliveries/{id:guid}/retry", Access.Admin),
        ("GET", "/mock/payroll-provider/received", Access.Admin),
    ];

    public static TheoryData<string, string, Access, UserRole?> Matrix()
    {
        var data = new TheoryData<string, string, Access, UserRole?>();
        foreach (var (method, route, access) in Endpoints)
        {
            data.Add(method, route, access, null);
            foreach (var role in Enum.GetValues<UserRole>())
            {
                data.Add(method, route, access, role);
            }
        }

        return data;
    }

    public static TheoryData<string, string> Protected()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, route, _) in Endpoints.Where(e => e.Access != Access.Public))
        {
            data.Add(method, route);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Endpoint_enforces_its_policy(string method, string route, Access access, UserRole? role)
    {
        var app = containers.Shared;

        // A fresh sign-in each time: one of the endpoints under test is logout.
        var caller = role is { } r ? (await app.PersonAsync(r)).Api : app.Anonymous;

        using var response = await SendAsync(caller, method, route);

        var allowed = access switch
        {
            Access.Public => true,
            Access.SignedIn => role is not null,
            Access.Approver => role is UserRole.Supervisor or UserRole.PayrollAdmin,
            _ => role is UserRole.PayrollAdmin,
        };

        if (!allowed)
        {
            Assert.Equal(role is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
            return;
        }

        // Past authorization, the empty body or random id may fail validation, but never with a challenge or a 500.
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True((int)response.StatusCode < 500, $"{method} {route} returned {(int)response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(Protected))]
    public async Task Endpoint_rejects_an_invalid_token(string method, string route)
    {
        using var response = await SendAsync(containers.Shared.WithToken("not.a.token"), method, route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Every_mapped_endpoint_is_in_the_matrix()
    {
        var mapped = MappedEndpoints()
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method} {Normalize(e.RoutePattern.RawText!)}"))
            .Order();

        Assert.Equal(Endpoints.Select(e => $"{e.Method} {Normalize(e.Route)}").Order(), mapped);
    }

    [Fact]
    public void Every_mapped_endpoint_states_its_access_explicitly()
    {
        Assert.All(MappedEndpoints(), e => Assert.True(
            e.Metadata.GetMetadata<IAllowAnonymous>() is not null || e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0,
            e.RoutePattern.RawText));
    }

    [Fact]
    public async Task Unknown_routes_are_closed_by_the_fallback_policy()
    {
        await containers.Shared.Anonymous.Get("/api/not-a-route").Expect(HttpStatusCode.Unauthorized);
        await containers.Shared.Admin.Get("/api/not-a-route").Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Api_responses_are_not_cacheable()
    {
        using var response = await containers.Shared.Admin.Get("/api/employees");

        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Swagger_is_off_outside_development()
    {
        using var response = await containers.Shared.Admin.Get("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private IEnumerable<RouteEndpoint> MappedEndpoints() =>
        containers.Shared.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    private static string Normalize(string route) => "/" + route.Trim('/');

    private static Task<HttpResponseMessage> SendAsync(Api caller, string method, string route)
    {
        var path = route.Replace("{id:guid}", Guid.NewGuid().ToString()).Replace("{date}", "2099-01-01");
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET")
        {
            request.Content = JsonContent.Create(new { });
        }

        return caller.Client.SendAsync(request);
    }
}
