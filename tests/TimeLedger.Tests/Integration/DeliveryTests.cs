using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TimeLedger.Domain;
using TimeLedger.Domain.Integration;

namespace TimeLedger.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class DeliveryTests(Containers containers) : IAsyncLifetime
{
    private const string Secret = "whsec_integration_test_secret";

    private readonly ConcurrentQueue<(string Method, string Path, string Body, string? Signature)> _received = new();
    private WebApplication _receiver = null!;
    private TestApp _app = null!;
    private string _url = null!;
    private int _inFlight;
    private int _peak;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _receiver = builder.Build();
        _receiver.Run(ReceiveAsync);
        await _receiver.StartAsync();
        _url = _receiver.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _app = await containers.StartAppAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    [Fact]
    public async Task Delivery_is_signed()
    {
        await ConnectAsync("/ok");
        await PublishAsync();

        await WaitForStatusAsync(DeliveryStatus.Succeeded);

        var (_, _, body, signature) = Assert.Single(_received);
        Assert.True(WebhookSigner.Verify(Secret, signature, body, _app.Clock.GetUtcNow().ToUnixTimeSeconds()));
    }

    [Fact]
    public async Task Redirect_is_a_failure_and_is_not_followed()
    {
        await ConnectAsync("/redirect");
        await PublishAsync();

        await WaitForStatusAsync(DeliveryStatus.DeadLettered);

        Assert.DoesNotContain(_received, r => r.Method == "GET");
    }

    [Fact]
    public async Task Nul_in_an_error_body_is_stored_and_retried()
    {
        await ConnectAsync("/nul");
        await PublishAsync();

        await HttpAssertions.Eventually(() => _app.DbAsync(db => db.WebhookDeliveries.AnyAsync(d => d.Attempts > 0)));

        var delivery = await _app.DbAsync(db => db.WebhookDeliveries.SingleAsync());
        Assert.Contains("badbody", delivery.LastError);
        Assert.Equal(500, delivery.LastStatusCode);
    }

    [Fact]
    public async Task Huge_error_body_is_truncated()
    {
        await ConnectAsync("/huge");
        await PublishAsync();

        await WaitForStatusAsync(DeliveryStatus.DeadLettered);

        Assert.Equal(1000, (await _app.DbAsync(db => db.WebhookDeliveries.SingleAsync())).LastError!.Length);
    }

    [Fact]
    public async Task Paused_connection_gets_nothing_until_resumed()
    {
        var connection = await ConnectAsync("/flaky");
        await PublishAsync();
        await HttpAssertions.Eventually(() => _app.DbAsync(db => db.WebhookDeliveries.AnyAsync(d => d.Attempts > 0)));
        await _app.Admin.Post($"/api/integrations/{connection}/enabled?enabled=false").Expect(HttpStatusCode.OK);

        await _app.DbAsync(db => db.WebhookDeliveries.ExecuteUpdateAsync(
            s => s.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await Task.Delay(4500); // two polling intervals
        Assert.Single(_received);

        await _app.Admin.Post($"/api/integrations/{connection}/enabled?enabled=true").Expect(HttpStatusCode.OK);
        await HttpAssertions.Eventually(() => Task.FromResult(_received.Count > 1));
    }

    [Fact]
    public async Task A_batch_is_sent_in_parallel_without_duplicates()
    {
        await ConnectAsync("/slow");
        await PublishAsync(10);

        await HttpAssertions.Eventually(async () => await _app.DbAsync(db => db.WebhookDeliveries.CountAsync(d => d.Status == DeliveryStatus.Succeeded)) == 10);

        Assert.True(_peak > 1, $"Peak concurrency was {_peak}.");
        Assert.Equal(10, _received.Select(r => r.Body).Distinct().Count());
        Assert.Equal(10, _received.Count);
    }

    private async Task<Guid> ConnectAsync(string path)
    {
        var body = await _app.Admin.Post("/api/integrations", new
        {
            name = path,
            provider = "GenericWebhook",
            endpointUrl = _url + path,
            eventTypes = new[] { "leave.approved" },
            secret = Secret,
        }).Expect(HttpStatusCode.Created);
        return body.GetProperty("integration").Id();
    }

    private async Task PublishAsync(int count = 1)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var employee = await _app.EmployeeAsync();
            ids.Add((await _app.Admin.Leave(employee, "2061-03-07", type: "Sick").Expect(HttpStatusCode.Created)).Id());
        }

        var approvals = await Task.WhenAll(ids.Select(id => _app.Admin.Post($"/api/leave-requests/{id}/approve")));
        Assert.All(approvals, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    private Task WaitForStatusAsync(DeliveryStatus status) =>
        HttpAssertions.Eventually(() => _app.DbAsync(db => db.WebhookDeliveries.AnyAsync(d => d.Status == status)));

    private async Task ReceiveAsync(HttpContext http)
    {
        using var reader = new StreamReader(http.Request.Body);
        var path = http.Request.Path.Value!;
        _received.Enqueue((http.Request.Method, path, await reader.ReadToEndAsync(), http.Request.Headers[WebhookSigner.SignatureHeader]));

        switch (path)
        {
            case "/redirect":
                http.Response.Redirect("/landing");
                break;
            case "/nul":
                http.Response.StatusCode = 500;
                await http.Response.WriteAsync("bad\0body");
                break;
            case "/huge":
                http.Response.StatusCode = 400;
                await http.Response.WriteAsync(new string('x', 200_000));
                break;
            case "/flaky":
                http.Response.StatusCode = 503;
                break;
            case "/slow":
                var now = Interlocked.Increment(ref _inFlight);
                lock (_received)
                {
                    _peak = Math.Max(_peak, now);
                }

                await Task.Delay(700);
                Interlocked.Decrement(ref _inFlight);
                break;
        }
    }
}
