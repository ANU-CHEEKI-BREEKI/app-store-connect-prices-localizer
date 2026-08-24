using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>every page's 'data' elements in one array, every page's 'included' in a second</summary>
public record PagedResult(JsonArray Data, JsonArray Included);

/// <summary>an App Store Connect error, with the status and the raw body kept for the caller</summary>
public class AscApiException : Exception
{
    public int StatusCode { get; }
    public string ResponseBody { get; }

    /// <summary>how long the server asked to wait before trying again, when it did</summary>
    public TimeSpan? RetryAfter { get; init; }

    public AscApiException(int statusCode, string message, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>errors[0].detail of the response, the sentence apple writes for a human</summary>
    public string? FirstErrorDetail
    {
        get
        {
            try
            {
                return (string?)JsonNode.Parse(ResponseBody)?["errors"]?[0]?["detail"];
            }
            catch
            {
                return null;
            }
        }
    }
}

/// <summary>
/// Raw calls to App Store Connect.
///
/// The whole api is four verbs over json, so the client is this thin: a bearer token, a base url
/// and JsonNode in both directions. Endpoints are named by path where they are called, and the
/// response shapes live in apple's api reference instead of in generated types.
/// </summary>
public class AscHttp
{
    private const string BaseUrl = "https://api.appstoreconnect.apple.com";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly Func<string> _token;

    public AscHttp(AscAuth auth)
        => _token = () => auth.Token;

    /// <summary>the bridge for code still built on the generated client: same calls, its token</summary>

    public async Task<JsonNode> GetAsync(string path)
        => await SendAsync(HttpMethod.Get, path, null);

    public async Task<JsonNode> PostAsync(string path, JsonNode body)
        => await SendAsync(HttpMethod.Post, path, body);

    public async Task<JsonNode> PatchAsync(string path, JsonNode body)
        => await SendAsync(HttpMethod.Patch, path, body);

    public async Task<JsonNode> DeleteAsync(string path)
        => await SendAsync(HttpMethod.Delete, path, null);

    /// <summary>a GET over every page: follows links.next until there is none</summary>
    public async Task<PagedResult> GetPagedAsync(string path)
    {
        var data = new JsonArray();
        var included = new JsonArray();

        var next = path;

        while (next is not null)
        {
            var page = await GetAsync(next);

            Drain(page["data"] as JsonArray, data);
            Drain(page["included"] as JsonArray, included);

            var href = (string?)page["links"]?["next"];
            next = href is null ? null : new Uri(href).PathAndQuery;
        }

        return new PagedResult(data, included);
    }

    /// <summary>moves the elements over: a node belongs to one tree, so it leaves the page first</summary>
    private static void Drain(JsonArray? from, JsonArray to)
    {
        while (from is { Count: > 0 })
        {
            var node = from[0];
            from.RemoveAt(0);
            to.Add(node);
        }
    }

    /// <summary>requests sent by every instance since the process started; printed by commands that care</summary>
    public static int RequestCount;

    /// <summary>
    /// requests left in the current hour, straight from the X-Rate-Limit header of the last
    /// response. Null until the first response. The quota is per key, so one number for all
    /// instances is the truth
    /// </summary>
    public static int? HourRemaining { get; private set; }

    /// <summary>statuses worth a second try: the quota and the server having a moment</summary>
    private static bool IsRetryable(int status, HttpMethod method)
        => status == 429 || (status >= 500 && method != HttpMethod.Post);

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body)
    {
        // a 429 means the hourly quota ran out: waiting is the only cure, and the header
        // says how long. 5xx just gets a growing pause. POSTs are not retried on 5xx -
        // the server may have executed them, and a duplicate write is worse than a loud error
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(method, path, body);
            }
            catch (AscApiException ex) when (attempt <= 3 && IsRetryable(ex.StatusCode, method))
            {
                var delay = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5);
                Console.WriteLine($"      [RETRY] {(int)ex.StatusCode} from the api, attempt {attempt} of 3, waiting {delay.TotalSeconds:0}s...");
                await Task.Delay(delay);
            }
        }
    }

    private async Task<JsonNode> SendOnceAsync(HttpMethod method, string path, JsonNode? body)
    {
        Interlocked.Increment(ref RequestCount);

        if (Environment.GetEnvironmentVariable("ASC_HTTP_LOG") == "1")
            Console.WriteLine($"[http] {method} {path[..Math.Min(path.Length, 120)]}");

        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token());

        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        // "user-hour-lim:3600;user-hour-rem:3128;" - the api tells how much quota is left
        if (response.Headers.TryGetValues("X-Rate-Limit", out var rateValues))
        {
            var rem = rateValues
                .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .FirstOrDefault(part => part.StartsWith("user-hour-rem:"));

            if (rem is not null && int.TryParse(rem["user-hour-rem:".Length..], out var remaining))
                HourRemaining = remaining;
        }

        if (!response.IsSuccessStatusCode)
            throw new AscApiException((int)response.StatusCode, $"{method} {path}: {text}", text)
            {
                RetryAfter = response.Headers.RetryAfter?.Delta,
            };

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

    /// <summary>
    /// a {"data": {"type", "id", "attributes"}} body, the shape every update request has.
    /// 'id' left null makes it a create body for the resources created from attributes alone
    /// </summary>
    public static JsonObject BodyWithAttributes(string type, string? id, JsonObject attributes)
    {
        var data = new JsonObject { ["type"] = type };
        if (id is not null)
            data["id"] = id;
        data["attributes"] = attributes;

        return new JsonObject { ["data"] = data };
    }

    public static JsonObject Link(string type, string id)
        => new() { ["data"] = new JsonObject { ["type"] = type, ["id"] = id } };

    public static JsonObject LinkMany(string type, IEnumerable<string> ids)
        => new()
        {
            ["data"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["type"] = type, ["id"] = id }).ToArray()),
        };
}
