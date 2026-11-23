using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeLedger.Api.Infrastructure;

public static class ApiJson
{
    /// <summary>API responses: camelCase, enums as strings.</summary>
    public static readonly JsonSerializerOptions Web = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Outbound webhook payloads: snake_case, the usual convention for webhooks.</summary>
    public static readonly JsonSerializerOptions Webhook = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        // Names only: an integer would let undefined values such as 99 through to the database.
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
