using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Model;

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
    /// <summary>an achievement and every language it has, in one object</summary>
    public class Achievement
    {
        public GameCenterAchievement Data { get; set; } = null!;
        public List<GameCenterAchievementLocalization> Localizations { get; set; } = new();

        /// <summary>localization id -> its image, for the languages that have one</summary>
        public Dictionary<string, GameCenterAchievementImage> Images { get; set; } = new(StringComparer.Ordinal);

        /// <summary>the id you typed when you created it, and the key of its csv rows</summary>
        public string VendorIdentifier => Data.Attributes?.VendorIdentifier ?? "";

        public string ReferenceName => Data.Attributes?.ReferenceName ?? "";

        public List<string> Locales => Localizations
            .Select(l => l.Attributes?.Locale ?? "")
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        public GameCenterAchievementLocalization? Find(string locale)
            => Localizations.FirstOrDefault(l => string.Equals(l.Attributes?.Locale, locale, StringComparison.OrdinalIgnoreCase));

        public GameCenterAchievementImage? ImageOf(GameCenterAchievementLocalization localization)
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

        GameCenterDetail? detail;

        try
        {
            var response = await new AppsApi(Service).AppsGameCenterDetailGetToOneRelatedAsync(Config.AppId);
            detail = response?.Data;
        }
        catch (AppStoreConnect.Net.Client.ApiException ex) when (ex.ErrorCode == 404)
        {
            detail = null;
        }

        if (detail is null)
        {
            Console.WriteLine("[ERROR] this app has no Game Center configuration.");
            Console.WriteLine("        turn Game Center on in App Store Connect first.");
            return null;
        }

        GameCenterDetailId = detail.Id;

        var groupId = detail.Relationships?.GameCenterGroup?.Data?.Id;

        var achievements = string.IsNullOrWhiteSpace(groupId)
            ? await FetchAchievementsAsync(api => api.GameCenterDetailsGameCenterAchievementsGetToManyRelatedAsync(detail.Id, limit: 200), verbose)
            : await FetchGroupAchievementsAsync(groupId, verbose);

        if (!string.IsNullOrWhiteSpace(groupId))
            Console.WriteLine($"   -> this app is in a Game Center group, its achievements are shared.");

        achievements = achievements
            .Where(a => !string.IsNullOrWhiteSpace(a.Attributes?.VendorIdentifier))
            .OrderBy(a => a.Attributes?.ReferenceName, StringComparer.OrdinalIgnoreCase)
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

    private async Task<List<GameCenterAchievement>> FetchAchievementsAsync(
        Func<GameCenterDetailsApi, Task<GameCenterAchievementsResponse>> fetch, bool verbose)
    {
        var api = new GameCenterDetailsApi(Service);

        return await FetchAllPagesAsync<GameCenterAchievementsResponse, GameCenterAchievement>(
            api.AsynchronousClient,
            api.Configuration,
            () => fetch(api),
            r => r.Data,
            r => r.Links?.Next,
            verbose
        );
    }

    private async Task<List<GameCenterAchievement>> FetchGroupAchievementsAsync(string groupId, bool verbose)
    {
        var api = new GameCenterGroupsApi(Service);

        return await FetchAllPagesAsync<GameCenterAchievementsResponse, GameCenterAchievement>(
            api.AsynchronousClient,
            api.Configuration,
            () => api.GameCenterGroupsGameCenterAchievementsGetToManyRelatedAsync(groupId, limit: 200),
            r => r.Data,
            r => r.Links?.Next,
            verbose
        );
    }

    /// <summary>
    /// the languages of one achievement, and the image of each. The image rides along in the same
    /// request: a localization without one can never go live, so it is never just extra data
    /// </summary>
    protected async Task LoadLocalizationsAsync(Achievement achievement, bool verbose)
    {
        var api = new GameCenterAchievementsApi(Service);

        try
        {
            var response = await api.GameCenterAchievementsLocalizationsGetToManyRelatedAsync(
                achievement.Data.Id,
                limit: 200,
                include: new List<string> { "gameCenterAchievementImage" }
            );

            achievement.Localizations = response?.Data ?? new();
            achievement.Images = MapImages(achievement.Localizations, response?.Included);

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
    private static Dictionary<string, GameCenterAchievementImage> MapImages(
        List<GameCenterAchievementLocalization> localizations,
        List<GameCenterAchievementLocalizationsResponseIncludedInner>? included)
    {
        var images = (included ?? new())
            .Select(i => i.ActualInstance)
            .OfType<GameCenterAchievementImage>()
            .Where(i => !string.IsNullOrWhiteSpace(i.Id))
            .ToDictionary(i => i.Id, StringComparer.Ordinal);

        var result = new Dictionary<string, GameCenterAchievementImage>(StringComparer.Ordinal);

        foreach (var localization in localizations)
        {
            var imageId = localization.Relationships?.GameCenterAchievementImage?.Data?.Id;

            if (!string.IsNullOrWhiteSpace(imageId) && images.TryGetValue(imageId, out var image))
                result[localization.Id] = image;
        }

        return result;
    }

    /// <summary>
    /// The language to copy an image from: the one configured as default, or the first that has an
    /// image at all. An achievement whose primary language has no image yet cannot seed anything.
    /// </summary>
    protected GameCenterAchievementLocalization? FindImageSource(Achievement achievement)
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
        HttpClient http,
        Achievement achievement,
        GameCenterAchievementLocalization target,
        byte[] bytes,
        string fileName,
        bool verbose)
    {
        var api = new GameCenterAchievementImagesApi(Service);

        var createRequest = new GameCenterAchievementImageCreateRequest(
            new GameCenterAchievementImageCreateRequestData(
                GameCenterAchievementImageCreateRequestData.TypeEnum.GameCenterAchievementImages,
                new AppClipAdvancedExperienceImageCreateRequestDataAttributes(bytes.Length, fileName),
                new GameCenterAchievementImageCreateRequestDataRelationships(
                    new GameCenterAchievementImageCreateRequestDataRelationshipsGameCenterAchievementLocalization(
                        new GameCenterAchievementImageRelationshipsGameCenterAchievementLocalizationData(
                            GameCenterAchievementImageRelationshipsGameCenterAchievementLocalizationData.TypeEnum.GameCenterAchievementLocalizations,
                            target.Id
                        )
                    )
                )
            )
        );

        var created = await api.GameCenterAchievementImagesCreateInstanceAsync(createRequest);

        var imageId = created?.Data?.Id;
        if (string.IsNullOrEmpty(imageId))
            throw new InvalidOperationException("App Store Connect did not return an image id.");

        var chunks = await MediaUpload.SendAllChunksAsync(http, created?.Data?.Attributes?.UploadOperations, bytes);

        await api.GameCenterAchievementImagesUpdateInstanceAsync(
            imageId,
            new GameCenterAchievementImageUpdateRequest(
                new GameCenterAchievementImageUpdateRequestData(
                    GameCenterAchievementImageUpdateRequestData.TypeEnum.GameCenterAchievementImages,
                    imageId,
                    // unlike a screenshot, a game center image is committed with the flag alone
                    new AppEventScreenshotUpdateRequestDataAttributes(uploaded: true)
                )
            )
        );

        if (verbose)
            Console.WriteLine($"         {fileName} {bytes.Length / 1024} KB in {chunks} chunk(s)");

        return true;
    }

    /// <summary>the bytes behind an already uploaded image, or null when it is not downloadable yet</summary>
    protected static async Task<(byte[] Bytes, string FileName)?> DownloadImageAsync(HttpClient http, GameCenterAchievementImage image)
    {
        var url = MediaUpload.DownloadUrl(image.Attributes?.ImageAsset);
        if (url is null)
            return null;

        var bytes = await http.GetByteArrayAsync(url);
        var fileName = string.IsNullOrWhiteSpace(image.Attributes?.FileName) ? "achievement.png" : image.Attributes.FileName;

        return (bytes, fileName);
    }
}
