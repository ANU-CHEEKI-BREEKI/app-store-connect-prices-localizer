using System.Security.Cryptography;
using AppStoreConnect.Net.Model;

/// <summary>
/// The way App Store Connect takes any binary: reserve the asset to get a list of byte ranges,
/// PUT every range where it says, then commit the asset with a checksum of the whole file.
///
/// Screenshots and Game Center achievement images are two different endpoints with the same
/// three steps, so the middle one lives here instead of in whichever command needed it first.
/// </summary>
public static class MediaUpload
{
    /// <summary>
    /// one upload operation is one byte range of the file. the headers apple sends back have to be
    /// replayed as given, and 'Content-Type' among them belongs on the content, not on the request
    /// </summary>
    public static async Task SendChunkAsync(HttpClient http, UploadOperation operation, byte[] bytes)
    {
        using var request = new HttpRequestMessage(
            new HttpMethod(string.IsNullOrWhiteSpace(operation.Method) ? "PUT" : operation.Method),
            operation.Url
        );

        var content = new ByteArrayContent(bytes, operation.Offset, operation.Length);

        foreach (var header in operation.RequestHeaders ?? new List<HttpHeader>())
        {
            if (string.IsNullOrWhiteSpace(header.Name))
                continue;

            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
                content.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        request.Content = content;

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"chunk upload failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }
    }

    /// <summary>every byte range apple asked for, in order. Answers how many chunks went out</summary>
    public static async Task<int> SendAllChunksAsync(HttpClient http, List<UploadOperation>? operations, byte[] bytes)
    {
        var all = operations ?? new List<UploadOperation>();
        if (all.Count == 0)
            throw new InvalidOperationException("App Store Connect returned no upload operations.");

        foreach (var operation in all)
            await SendChunkAsync(http, operation, bytes);

        return all.Count;
    }

    /// <summary>the checksum apple wants on the commit: lowercase hex md5 of the whole file</summary>
    public static string Checksum(byte[] bytes)
        => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// fills the '{w}', '{h}' and '{f}' placeholders of an asset template url, which is the only
    /// way to get the bytes of something already uploaded back out
    /// </summary>
    public static string? DownloadUrl(ImageAsset? asset, string format = "png")
    {
        if (asset is null || string.IsNullOrWhiteSpace(asset.TemplateUrl))
            return null;

        return asset.TemplateUrl
            .Replace("{w}", asset.Width.ToString())
            .Replace("{h}", asset.Height.ToString())
            .Replace("{f}", format);
    }
}
