using System.Security.Cryptography;
using System.Text.Json.Nodes;

/// <summary>
/// The way App Store Connect takes any binary: reserve the asset to get a list of byte ranges,
/// PUT every range where it says, then commit the asset with a checksum of the whole file.
///
/// The twin of MediaUpload for the direct http client: the byte ranges arrive as the
/// 'uploadOperations' attribute of the reserved asset and are read straight off the JsonNode.
/// </summary>
public static class AscUpload
{
    // the chunks go to apple's storage hosts, not the api, and a big one needs its time
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// one upload operation is one byte range of the file. the headers apple sends back have to be
    /// replayed as given, and 'Content-Type' among them belongs on the content, not on the request
    /// </summary>
    public static async Task SendChunkAsync(JsonNode operation, byte[] bytes)
    {
        var method = (string?)operation["method"];
        var url = (string?)operation["url"]
            ?? throw new InvalidOperationException("an upload operation came without a url.");

        using var request = new HttpRequestMessage(
            new HttpMethod(string.IsNullOrWhiteSpace(method) ? "PUT" : method),
            url
        );

        var content = new ByteArrayContent(bytes, (int?)operation["offset"] ?? 0, (int?)operation["length"] ?? 0);

        foreach (var header in operation["requestHeaders"] as JsonArray ?? new JsonArray())
        {
            var name = (string?)header?["name"];
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var value = (string?)header?["value"];

            if (!request.Headers.TryAddWithoutValidation(name, value))
                content.Headers.TryAddWithoutValidation(name, value);
        }

        request.Content = content;

        using var response = await Client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"chunk upload failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }
    }

    /// <summary>every byte range apple asked for, in order. Answers how many chunks went out</summary>
    public static async Task<int> SendAllChunksAsync(JsonNode? uploadOperations, byte[] bytes)
    {
        var all = uploadOperations as JsonArray ?? new JsonArray();
        if (all.Count == 0)
            throw new InvalidOperationException("App Store Connect returned no upload operations.");

        foreach (var operation in all)
            await SendChunkAsync(operation!, bytes);

        return all.Count;
    }

    /// <summary>the checksum apple wants on the commit: lowercase hex md5 of the whole file</summary>
    public static string Checksum(byte[] bytes)
        => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// fills the '{w}', '{h}' and '{f}' placeholders of an 'imageAsset' template url, which is the
    /// only way to get the bytes of something already uploaded back out
    /// </summary>
    public static string? DownloadUrl(JsonNode? asset, string format = "png")
    {
        var template = (string?)asset?["templateUrl"];
        if (string.IsNullOrWhiteSpace(template))
            return null;

        return template
            .Replace("{w}", ((int?)asset?["width"] ?? 0).ToString())
            .Replace("{h}", ((int?)asset?["height"] ?? 0).ToString())
            .Replace("{f}", format);
    }
}
