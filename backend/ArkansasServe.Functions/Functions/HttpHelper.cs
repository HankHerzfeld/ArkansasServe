using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;

namespace ArkansasServe.Functions.Functions;

internal static class HttpHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task<HttpResponseData> OkJson(HttpRequestData req, object data)
    {
        var res = req.CreateResponse(HttpStatusCode.OK);
        await res.WriteStringAsync(JsonSerializer.Serialize(data, JsonOpts));
        res.Headers.Add("Content-Type", "application/json");
        return res;
    }

    public static async Task<HttpResponseData> CreatedJson(HttpRequestData req, object data)
    {
        var res = req.CreateResponse(HttpStatusCode.Created);
        await res.WriteStringAsync(JsonSerializer.Serialize(data, JsonOpts));
        res.Headers.Add("Content-Type", "application/json");
        return res;
    }

    public static async Task<HttpResponseData> Error(HttpRequestData req, HttpStatusCode code, string message)
    {
        var res = req.CreateResponse(code);
        await res.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, JsonOpts));
        res.Headers.Add("Content-Type", "application/json");
        return res;
    }

    public static async Task<T?> ReadBody<T>(HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            return string.IsNullOrEmpty(body) ? default : JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch
        {
            return default;
        }
    }
}