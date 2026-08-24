using System.Text.Json.Nodes;

/// <summary>
/// Shared plumbing for the subcommands that touch Game Center achievements.
///
/// Getting to them is two hops and one fork: an app has a game center detail, and the achievements
/// hang off that - unless the app was put into a game center group, in which case they moved to the
/// group and the detail has none of its own. Both games of a group share one set of achievements,
/// which is the whole point of a group, so the fork is not an edge case.
/// </summary>
public abstract class GameCenterCommandBase : LocalesCommandBase
{
    /// <summary>the attributes of one achievement language, read straight off its node</summary>
    public class LocalizationAttributes
    {
        private readonly JsonNode _node;

        public LocalizationAttributes(JsonNode node)
            => _node = node;

        public string? Locale => (string?)_node["attributes"]?["locale"];
        public string? Name => (string?)_node["attributes"]?["name"];
        public string? BeforeEarnedDescription => (string?)_node["attributes"]?["beforeEarnedDescription"];
        public string? AfterEarnedDescription => (string?)_node["attributes"]?["afterEarnedDescription"];
    }

    /// <summary>one language of an achievement: the raw node behind the fields everything reads</summary>
    public class Localization
    {
        public JsonNode Node { get; }

        public Localization(JsonNode node)
        {
            Node = node;
            Attributes = new LocalizationAttributes(node);
        }

        public string Id => (string?)Node["id"] ?? "";

        public LocalizationAttributes? Attributes { get; }
    }

    /// <summary>an achievement and every language it has, in one object</summary>
    public class Achievement
    {
        /// <summary>the raw achievement resource, as App Store Connect sent it</summary>
        public JsonNode Data { get; set; } = null!;

        public List<Localization> Localizations { get; set; } = new();

        /// <summary>localization id -> its image node, for the languages that have one</summary>
        public Dictionary<string, JsonNode> Images { get; set; } = new(StringComparer.Ordinal);

        public string Id => (string?)Data["id"] ?? "";

        /// <summary>the id you typed when you created it, and the key of its csv rows</summary>
        public string VendorIdentifier => (string?)Data["attributes"]?["vendorIdentifier"] ?? "";

        public string ReferenceName => (string?)Data["attributes"]?["referenceName"] ?? "";

        public List<string> Locales => Localizations
            .Select(l => l.Attributes?.Locale ?? "")
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        public Localization? Find(string locale)
            => Localizations.FirstOrDefault(l => string.Equals(l.Attributes?.Locale, locale, StringComparison.OrdinalIgnoreCase));

        public JsonNode? ImageOf(Localization localization)
            => Images.TryGetValue(localization.Id, out var image) ? image : null;
    }

    /// <summary>where the achievements live, which is also what a release has to point at</summary>
    protected string? GameCenterDetailId { get; private set; }

    /// <summary>
    /// the whole achievement catalog with its languages. Returns null when the app has no Game
    /// Center at all, which is a different thing from having no achievements yet
    /// </summary>
    protected async Task<List<Achievement>?> GetAchievementsAsync(bool verbose)
    {
        if (string.IsNullOrWhiteSpace(Config.AppId))
        {
            Console.WriteLine("[ERROR] no app id. specify it in config.json or with --app-id");
            return null;
        }

        Console.WriteLine("   -> Receiving Game Center details...");

        JsonNode? detail;

        try
        {
            var response = await Http.GetAsync($"/v1/apps/{Config.AppId}/gameCenterDetail");
            detail = response["data"];
        }
        catch (AscApiException ex) when (ex.StatusCode == 404)
        {
            detail = null;
        }

        if (detail is null)
        {
            Console.WriteLine("[ERROR] this app has no Game Center configuration.");
            Console.WriteLine("        turn Game Center on in App Store Connect first.");
            return null;
        }

        GameCenterDetailId = (string?)detail["id"];

        var groupId = (string?)detail["relationships"]?["gameCenterGroup"]?["data"]?["id"];

        var page = string.IsNullOrWhiteSpace(groupId)
            ? await Http.GetPagedAsync($"/v1/gameCenterDetails/{GameCenterDetailId}/gameCenterAchievements?limit=200")
            : await Http.GetPagedAsync($"/v1/gameCenterGroups/{groupId}/gameCenterAchievements?limit=200");

        if (!string.IsNullOrWhiteSpace(groupId))
            Console.WriteLine($"   -> this app is in a Game Center group, its achievements are shared.");

        var achievements = page.Data
            .OfType<JsonNode>()
            .Where(a => !string.IsNullOrWhiteSpace((string?)a["attributes"]?["vendorIdentifier"]))
            .OrderBy(a => (string?)a["attributes"]?["referenceName"], StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"   -> {achievements.Count} achievement(s), receiving their languages...");

        var result = new List<Achievement>();

        foreach (var data in achievements)
        {
            var achievement = new Achievement { Data = data };
            await LoadLocalizationsAsync(achievement, verbose);
            result.Add(achievement);
        }

        return result;
    }

    /// <summary>
    /// the languages of one achievement, and the image of each. The image rides along in the same
    /// request: a localization without one can never go live, so it is never just extra data
    /// </summary>
    protected async Task LoadLocalizationsAsync(Achievement achievement, bool verbose)
    {
        try
        {
            var response = await Http.GetAsync(
                $"/v1/gameCenterAchievements/{achievement.Id}/localizations?limit=200&include=gameCenterAchievementImage"
            );

            achievement.Localizations = (response["data"] as JsonArray ?? new JsonArray())
                .OfType<JsonNode>()
                .Select(n => new Localization(n))
                .ToList();

            achievement.Images = MapImages(achievement.Localizations, response["included"] as JsonArray);

            if (verbose)
                Console.WriteLine($"      {achievement.VendorIdentifier,-32} {achievement.Localizations.Count} language(s), {achievement.Images.Count} image(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not read the languages of {achievement.VendorIdentifier}: {ex.Message}");
            achievement.Localizations = new();
            achievement.Images = new(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 'included' is a flat list, so an image is tied back to its language through the relationship
    /// the localization carries. Doing it the other way around would need one request per language
    /// </summary>
    private static Dictionary<string, JsonNode> MapImages(List<Localization> localizations, JsonArray? included)
    {
        var images = (included ?? new JsonArray())
            .OfType<JsonNode>()
            .Where(i => (string?)i["type"] == "gameCenterAchievementImages")
            .Where(i => !string.IsNullOrWhiteSpace((string?)i["id"]))
            .ToDictionary(i => (string)i["id"]!, StringComparer.Ordinal);

        var result = new Dictionary<string, JsonNode>(StringComparer.Ordinal);

        foreach (var localization in localizations)
        {
            var imageId = (string?)localization.Node["relationships"]?["gameCenterAchievementImage"]?["data"]?["id"];

            if (!string.IsNullOrWhiteSpace(imageId) && images.TryGetValue(imageId, out var image))
                result[localization.Id] = image;
        }

        return result;
    }

    /// <summary>
    /// The language to copy an image from: the one configured as default, or the first that has an
    /// image at all. An achievement whose primary language has no image yet cannot seed anything.
    /// </summary>
    protected Localization? FindImageSource(Achievement achievement)
    {
        var primary = string.IsNullOrWhiteSpace(Config.DefaultLocale) ? "en-US" : Config.DefaultLocale;

        var preferred = achievement.Find(primary);
        if (preferred is not null && achievement.ImageOf(preferred) is not null)
            return preferred;

        return achievement.Localizations.FirstOrDefault(l => achievement.ImageOf(l) is not null);
    }

    /// <summary>
    /// Copies one achievement image onto a language that has none: download the source bytes once,
    /// then reserve, upload and commit against the target localization.
    ///
    /// App Store Connect has no way to point two languages at one image, and no way to copy one, so
    /// the bytes really do have to make the round trip.
    /// </summary>
    protected async Task<bool> CopyImageAsync(
        Achievement achievement,
        Localization target,
        byte[] bytes,
        string fileName,
        bool verbose)
    {
        var created = await Http.PostAsync(
            "/v1/gameCenterAchievementImages",
            AscHttp.Body(
                "gameCenterAchievementImages",
                new JsonObject
                {
                    ["gameCenterAchievementLocalization"] = AscHttp.Link("gameCenterAchievementLocalizations", target.Id),
                },
                new JsonObject
                {
                    ["fileSize"] = bytes.Length,
                    ["fileName"] = fileName,
                }
            )
        );

        var imageId = (string?)created["data"]?["id"];
        if (string.IsNullOrEmpty(imageId))
            throw new InvalidOperationException("App Store Connect did not return an image id.");

        var chunks = await AscUpload.SendAllChunksAsync(created["data"]?["attributes"]?["uploadOperations"], bytes);

        // unlike a screenshot, a game center image is committed with the flag alone
        await Http.PatchAsync(
            $"/v1/gameCenterAchievementImages/{imageId}",
            AscHttp.BodyWithAttributes("gameCenterAchievementImages", imageId, new JsonObject { ["uploaded"] = true })
        );

        if (verbose)
            Console.WriteLine($"         {fileName} {bytes.Length / 1024} KB in {chunks} chunk(s)");

        return true;
    }

    /// <summary>the bytes behind an already uploaded image, or null when it is not downloadable yet</summary>
    protected static async Task<(byte[] Bytes, string FileName)?> DownloadImageAsync(HttpClient http, JsonNode image)
    {
        var url = AscUpload.DownloadUrl(image["attributes"]?["imageAsset"]);
        if (url is null)
            return null;

        var bytes = await http.GetByteArrayAsync(url);

        var name = (string?)image["attributes"]?["fileName"];
        var fileName = string.IsNullOrWhiteSpace(name) ? "achievement.png" : name;

        return (bytes, fileName);
    }
}
