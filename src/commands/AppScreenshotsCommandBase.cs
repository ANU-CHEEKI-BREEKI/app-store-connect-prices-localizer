using System.Text.Json.Nodes;

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
        JsonNode? Asset,
        string? State
    )
    {
        /// <summary>an image apple is still processing has no asset yet, so there is nothing to download</summary>
        public bool IsDownloadable => Asset is not null && !string.IsNullOrWhiteSpace((string?)Asset["templateUrl"]);
    }

    /// <summary>the locales of the version, narrowed down by '--locales' when it is given</summary>
    protected List<string> FilterLocales(MetadataTarget target, bool announce = true)
    {
        var all = target.VersionLocalizations
            .Select(l => (string?)l["attributes"]?["locale"] ?? "")
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
        var entries = new List<ScreenshotEntry>();

        foreach (var locale in locales)
        {
            var localization = target.FindVersionLocalization(locale);
            if (localization is null)
                continue;

            var sets = await Http.GetPagedAsync(
                $"/v1/appStoreVersionLocalizations/{(string?)localization["id"]}/appScreenshotSets?limit=50"
            );

            var localeCount = 0;

            foreach (var set in sets.Data)
            {
                var displayType = DisplayTypeName(set?["attributes"]?["screenshotDisplayType"]);

                if (displayTypeFilter.Count > 0
                    && !displayTypeFilter.Any(f => string.Equals(f, displayType, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var screenshots = await Http.GetPagedAsync(
                    $"/v1/appScreenshotSets/{(string?)set?["id"]}/appScreenshots?limit=50"
                );

                // the api returns the screenshots in the order they are shown on the product page
                var position = 0;
                foreach (var screenshot in screenshots.Data)
                {
                    position++;

                    entries.Add(new ScreenshotEntry(
                        locale,
                        displayType,
                        position,
                        (string?)set?["id"] ?? "",
                        (string?)screenshot?["id"] ?? "",
                        (string?)screenshot?["attributes"]?["fileName"] ?? (string?)screenshot?["id"] ?? "",
                        screenshot?["attributes"]?["imageAsset"],
                        StateName(screenshot?["attributes"]?["assetDeliveryState"]?["state"])
                    ));

                    localeCount++;
                }
            }

            if (verbose)
                Console.WriteLine($"      {locale,-12} {localeCount} screenshots in {sets.Data.Count} sets");
        }

        return entries;
    }

    /// <summary>the json value is the name apple actually uses, missing means the api added a new one</summary>
    protected static string DisplayTypeName(JsonNode? displayType)
        => (string?)displayType ?? "UNKNOWN";

    /// <summary>the state the way the generated client printed it: the enum names had no underscores</summary>
    protected static string? StateName(JsonNode? state)
        => ((string?)state)?.Replace("_", "");

    /// <summary>
    /// every display type the api knows about, the way the generated client's enum listed them.
    /// a new device size apple adds shows up in apple's api docs, not here
    /// </summary>
    protected static readonly string[] KnownDisplayTypes =
    {
        "APP_IPHONE_67",
        "APP_IPHONE_61",
        "APP_IPHONE_65",
        "APP_IPHONE_58",
        "APP_IPHONE_55",
        "APP_IPHONE_47",
        "APP_IPHONE_40",
        "APP_IPHONE_35",
        "APP_IPAD_PRO_3GEN_129",
        "APP_IPAD_PRO_3GEN_11",
        "APP_IPAD_PRO_129",
        "APP_IPAD_105",
        "APP_IPAD_97",
        "APP_DESKTOP",
        "APP_WATCH_ULTRA",
        "APP_WATCH_SERIES_10",
        "APP_WATCH_SERIES_7",
        "APP_WATCH_SERIES_4",
        "APP_WATCH_SERIES_3",
        "APP_APPLE_TV",
        "APP_APPLE_VISION_PRO",
        "IMESSAGE_APP_IPHONE_67",
        "IMESSAGE_APP_IPHONE_61",
        "IMESSAGE_APP_IPHONE_65",
        "IMESSAGE_APP_IPHONE_58",
        "IMESSAGE_APP_IPHONE_55",
        "IMESSAGE_APP_IPHONE_47",
        "IMESSAGE_APP_IPHONE_40",
        "IMESSAGE_APP_IPAD_PRO_3GEN_129",
        "IMESSAGE_APP_IPAD_PRO_3GEN_11",
        "IMESSAGE_APP_IPAD_PRO_129",
        "IMESSAGE_APP_IPAD_105",
        "IMESSAGE_APP_IPAD_97",
    };

    /// <summary>every display type the api knows about, as the codes '--display-types' expects</summary>
    protected static List<string> AllDisplayTypes()
        => KnownDisplayTypes
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
