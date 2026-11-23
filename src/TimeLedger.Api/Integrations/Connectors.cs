using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TimeLedger.Api.Infrastructure;
using TimeLedger.Domain;
using TimeLedger.Domain.Entities;
using TimeLedger.Domain.Integration;

namespace TimeLedger.Api.Integrations;

/// <summary>
/// Translates a TimeLedger domain event into one provider's wire format. Adding a provider means
/// adding one class; the outbox, retry and signing machinery is shared.
/// </summary>
public interface IPayrollConnector
{
    IntegrationProvider Provider { get; }

    /// <summary>Returns the request body, or null if this provider has no mapping for the event type.</summary>
    string? BuildBody(OutboxMessage message);
}

public sealed class ConnectorRegistry(IEnumerable<IPayrollConnector> connectors)
{
    private readonly Dictionary<IntegrationProvider, IPayrollConnector> _byProvider =
        connectors.ToDictionary(c => c.Provider);

    public IPayrollConnector Get(IntegrationProvider provider) =>
        _byProvider.TryGetValue(provider, out var c) ? c : throw new InvalidOperationException($"No connector registered for {provider}.");

    /// <summary>Builds a signed POST. Delivery id lets receivers de-duplicate retries.</summary>
    public static HttpRequestMessage CreateSignedRequest(IntegrationConnection connection, OutboxMessage message, Guid deliveryId, string body, DateTimeOffset now)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, connection.EndpointUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-TimeLedger-Event", message.Type);
        request.Headers.Add("X-TimeLedger-Delivery", deliveryId.ToString());
        request.Headers.Add(WebhookSigner.SignatureHeader, WebhookSigner.Sign(connection.Secret, now.ToUnixTimeSeconds(), body));
        return request;
    }
}

/// <summary>Canonical envelope: { id, type, created_at, data } where data is the event as stored.</summary>
public sealed class GenericWebhookConnector : IPayrollConnector
{
    private static readonly JsonSerializerOptions Output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public IntegrationProvider Provider => IntegrationProvider.GenericWebhook;

    public string? BuildBody(OutboxMessage message)
    {
        var envelope = new JsonObject
        {
            ["id"] = message.Id.ToString(),
            ["type"] = message.Type,
            ["created_at"] = message.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["data"] = JsonNode.Parse(message.Payload),
        };
        return envelope.ToJsonString(Output);
    }
}

/// <summary>
/// Maps a closed pay period to a payroll-provider payload modelled on Gusto's employee-compensation
/// shape (hourly compensations + paid time off). It is illustrative: a production Gusto connector
/// would also need OAuth, Gusto employee UUID mapping, and their payroll-update endpoint.
/// </summary>
public sealed class GustoStyleConnector : IPayrollConnector
{
    public IntegrationProvider Provider => IntegrationProvider.GustoStyle;

    public string? BuildBody(OutboxMessage message)
    {
        if (message.Type != EventTypes.PayrollPeriodClosed)
        {
            return null;
        }

        var evt = JsonSerializer.Deserialize<PayrollPeriodClosedEvent>(message.Payload, ApiJson.Webhook)
                  ?? throw new InvalidOperationException("Empty payroll event payload.");

        var body = new
        {
            pay_period = new { start_date = evt.StartDate, end_date = evt.EndDate },
            external_reference = evt.PayPeriodId,
            employee_compensations = evt.Employees.Select(e => new
            {
                employee_external_id = e.EmployeeCode,
                hourly_compensations = new[]
                {
                    new { name = "Regular Hours", hours = Hours(e.RegularHours) },
                    new { name = "Overtime", hours = Hours(e.OvertimeHours) },
                },
                fixed_compensations = new[]
                {
                    new { name = "Statutory Overtime Pay (IDR)", amount = e.OvertimePay.ToString("0", CultureInfo.InvariantCulture) },
                },
                paid_time_off = new[]
                {
                    new { name = "Vacation Hours", hours = Hours(e.AnnualLeaveDays * e.NormalDailyHours) },
                    new { name = "Sick Hours", hours = Hours(e.SickLeaveDays * e.NormalDailyHours) },
                },
                unpaid_leave_hours = Hours(e.UnpaidLeaveDays * e.NormalDailyHours),
            }),
        };

        return JsonSerializer.Serialize(body);
    }

    private static string Hours(decimal hours) => hours.ToString("0.000", CultureInfo.InvariantCulture);
}
