using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppStoreConnect.Net.Client;

/// <summary>
/// Raw calls to App Store Connect for what the generated client does not know yet.
///
/// The client is built from an older spec; Game Center submissions went through a rewrite after
/// that, so the few endpoints needed for them are called by hand with the same token.
/// </summary>
public class AscHttp
{
    private const string BaseUrl = "https://api.appstoreconnect.apple.com";

    private readonly Configuration _service;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public AscHttp(Configuration service)
        => _service = service;

    public async Task<JsonNode> GetAsync(string path)
        => await SendAsync(HttpMethod.Get, path, null);

    public async Task<JsonNode> PostAsync(string path, JsonNode body)
        => await SendAsync(HttpMethod.Post, path, body);

    public async Task<JsonNode> PatchAsync(string path, JsonNode body)
        => await SendAsync(HttpMethod.Patch, path, body);

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _service.AccessToken);

        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new ApiException((int)response.StatusCode, $"{method} {path}: {text}", text);

        return string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : JsonNode.Parse(text) ?? new JsonObject();
    }

    /// <summary>a {"data": {"type": ..., "relationships": {...}}} body, the shape every create request has</summary>
    public static JsonObject Body(string type, JsonObject relationships, JsonObject? attributes = null)
    {
        var data = new JsonObject { ["type"] = type, ["relationships"] = relationships };
        if (attributes is not null)
            data["attributes"] = attributes;

        return new JsonObject { ["data"] = data };
    }

    public static JsonObject Link(string type, string id)
        => new() { ["data"] = new JsonObject { ["type"] = type, ["id"] = id } };
}
