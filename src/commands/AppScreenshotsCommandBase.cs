using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Model;
using Newtonsoft.Json;

/// <summary>
/// shared plumbing for the commands that work with the product page screenshots.
///
/// App Store Connect nests them three levels deep: an app store version has one localization per
/// language, every localization has one screenshot set per device size ('display type'), and every
/// set holds the images in the order they appear on the product page. So both listing and
/// downloading start with the same walk, and only differ in what they do with the result
/// </summary>
public abstract class AppScreenshotsCommandBase : AppMetadataCommandBase
{
    /// <summary>one screenshot, flattened out of the localization / set / image nesting</summary>
    protected record ScreenshotEntry(
        string Locale,
        string DisplayType,
        int Position,
        string SetId,
        string ScreenshotId,
        string SourceFileName,
        ImageAsset? Asset,
        AppMediaAssetState.StateEnum? State
    )
    {
        /// <summary>an image apple is still processing has no asset yet, so there is nothing to download</summary>
        public bool IsDownloadable => Asset is not null && !string.IsNullOrWhiteSpace(Asset.TemplateUrl);
    }

    /// <summary>the locales of the version, narrowed down by '--locales' when it is given</summary>
    protected List<string> FilterLocales(MetadataTarget target, bool announce = true)
    {
        var all = target.VersionLocalizations
            .Select(l => l.Attributes?.Locale ?? "")
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (all.Count == 0)
        {
            Console.WriteLine("[ERROR] the version has no localizations, so it has no screenshots either.");
            return all;
        }

        var requested = ParseList("--locales");
        if (requested.Count == 0)
        {
            if (announce)
                Console.WriteLine($"   -> {all.Count} locales: {string.Join(", ", all)}");

            return all;
        }

        var selected = all
            .Where(l => requested.Any(r => string.Equals(r, l, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var unknown = requested
            .Where(r => !all.Any(l => string.Equals(r, l, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (unknown.Count > 0)
            Console.WriteLine($"[WARN] the version has no localization for: {string.Join(", ", unknown)}");

        if (selected.Count == 0)
            Console.WriteLine($"[ERROR] none of the requested locales exist. Available: {string.Join(", ", all)}");
        else if (announce)
            Console.WriteLine($"   -> {selected.Count} locales: {string.Join(", ", selected)}");

        return selected;
    }

    /// <summary>
    /// walks localization -> screenshot set -> screenshot for every locale and flattens the result.
    /// doing this in one pass keeps the callers free of api calls
    /// </summary>
    protected async Task<List<ScreenshotEntry>> ScanAsync(
        MetadataTarget target,
        List<string> locales,
        List<string> displayTypeFilter,
        bool verbose)
    {
        var localizationsApi = new AppStoreVersionLocalizationsApi(Service);
        var setsApi = new AppScreenshotSetsApi(Service);

        var entries = new List<ScreenshotEntry>();

        foreach (var locale in locales)
        {
            var localization = target.FindVersionLocalization(locale);
            if (localization is null)
                continue;

            var sets = await FetchAllPagesAsync<AppScreenshotSetsResponse, AppScreenshotSet>(
                localizationsApi.AsynchronousClient,
                localizationsApi.Configuration,
                () => localizationsApi.AppStoreVersionLocalizationsAppScreenshotSetsGetToManyRelatedAsync(localization.Id, limit: 50),
                r => r.Data,
                r => r.Links?.Next,
                verbose
            );

            var localeCount = 0;

            foreach (var set in sets)
            {
                var displayType = DisplayTypeName(set.Attributes?.ScreenshotDisplayType);

                if (displayTypeFilter.Count > 0
                    && !displayTypeFilter.Any(f => string.Equals(f, displayType, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var screenshots = await FetchAllPagesAsync<AppScreenshotsResponse, AppScreenshot>(
                    setsApi.AsynchronousClient,
                    setsApi.Configuration,
                    () => setsApi.AppScreenshotSetsAppScreenshotsGetToManyRelatedAsync(set.Id, limit: 50),
                    r => r.Data,
                    r => r.Links?.Next,
                    verbose
                );

                // the api returns the screenshots in the order they are shown on the product page
                var position = 0;
                foreach (var screenshot in screenshots)
                {
                    position++;

                    entries.Add(new ScreenshotEntry(
                        locale,
                        displayType,
                        position,
                        set.Id,
                        screenshot.Id,
                        screenshot.Attributes?.FileName ?? screenshot.Id,
                        screenshot.Attributes?.ImageAsset,
                        screenshot.Attributes?.AssetDeliveryState?.State
                    ));

                    localeCount++;
                }
            }

            if (verbose)
                Console.WriteLine($"      {locale,-12} {localeCount} screenshots in {sets.Count} sets");
        }

        return entries;
    }

    /// <summary>the generated enum drops the underscores, the json value is the name apple actually uses</summary>
    protected static string DisplayTypeName(ScreenshotDisplayType? displayType)
        => displayType is null
            ? "UNKNOWN"
            : JsonConvert.SerializeObject(displayType).Trim('"');

    /// <summary>every display type the api knows about, as the codes '--display-types' expects</summary>
    protected static List<string> AllDisplayTypes()
        => Enum.GetValues<ScreenshotDisplayType>()
            .Select(t => DisplayTypeName(t))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    protected static string SanitizeSegment(string segment)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            segment = segment.Replace(invalid, '_');

        return segment;
    }

    /// <summary>reads a comma separated option like '--locales en-US,de-DE' into its parts</summary>
    protected List<string> ParseList(string option)
        => Args.TryGetOption(option, "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
