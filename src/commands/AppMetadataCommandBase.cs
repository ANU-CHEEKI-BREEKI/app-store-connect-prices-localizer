using System.Text.Json.Nodes;

/// <summary>
/// shared plumbing for the commands that read and write the localizable texts of the app store product page.
///
/// App Store Connect keeps those texts in two different places:
/// - 'Name' and 'Subtitle' live on the app info (the "App Information" page, shared by all versions)
/// - everything else lives on the app store version (the "iOS App x.y.z" page)
/// so every command here has to resolve both, and always the editable ones
/// </summary>
public abstract class AppMetadataCommandBase : CommandBase
{
    public enum MetadataScope
    {
        AppInfo,
        Version,
    }

    /// <summary>a single localizable text of the product page, one csv row</summary>
    public record MetadataField(string Key, string Title, MetadataScope Scope, int MaxLength)
    {
        public string Comment => Scope == MetadataScope.AppInfo
            ? $"App Information > {Title}. Max {MaxLength} characters."
            : $"App Store version page > {Title}. Max {MaxLength} characters.";
    }

    /// <summary>the csv rows, in the same order they appear in App Store Connect</summary>
    public static readonly MetadataField[] Fields =
    {
        new("name", "Name", MetadataScope.AppInfo, 30),
        new("subtitle", "Subtitle", MetadataScope.AppInfo, 30),
        new("promotional_text", "Promotional Text", MetadataScope.Version, 170),
        new("description", "Description", MetadataScope.Version, 4000),
        new("whats_new", "What's New in This Version", MetadataScope.Version, 4000),
        new("keywords", "Keywords", MetadataScope.Version, 100),
    };

    public const string KeyColumn = "Key";
    public const string IdColumn = "Id";
    public const string CommentsColumn = "Shared Comments";

    /// <summary>
    /// version states in which App Store Connect still lets you edit the metadata.
    /// a READY_FOR_DISTRIBUTION version is frozen, writing to it fails with a 409
    /// </summary>
    private static readonly string[] EditableVersionStates =
    {
        "PREPARE_FOR_SUBMISSION",
        "DEVELOPER_REJECTED",
        "REJECTED",
        "METADATA_REJECTED",
        "INVALID_BINARY",
        "READY_FOR_REVIEW",
        "WAITING_FOR_REVIEW",
    };

    /// <summary>everything the commands need to touch a single product page, in a single place</summary>
    public class MetadataTarget
    {
        public JsonNode Version { get; set; } = null!;
        public JsonNode? AppInfo { get; set; }

        public List<JsonNode> VersionLocalizations { get; set; } = new();
        public List<JsonNode> AppInfoLocalizations { get; set; } = new();

        public string VersionString => (string?)Version["attributes"]?["versionString"] ?? "?";

        /// <summary>every locale that exists on either of the two pages</summary>
        public List<string> Locales => VersionLocalizations
            .Select(l => (string?)l["attributes"]?["locale"] ?? "")
            .Concat(AppInfoLocalizations.Select(l => (string?)l["attributes"]?["locale"] ?? ""))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        public JsonNode? FindVersionLocalization(string locale)
            => VersionLocalizations.FirstOrDefault(l => string.Equals((string?)l["attributes"]?["locale"], locale, StringComparison.OrdinalIgnoreCase));

        public JsonNode? FindAppInfoLocalization(string locale)
            => AppInfoLocalizations.FirstOrDefault(l => string.Equals((string?)l["attributes"]?["locale"], locale, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>the state the way the generated client printed it: the enum names had no underscores</summary>
    private static string? StateName(JsonNode? state)
        => ((string?)state)?.Replace("_", "");

    /// <summary>
    /// resolves the version to work with and loads all its localizations.
    ///
    /// by default it picks the editable version (the one you are preparing for submission),
    /// so a round trip export -> translate -> import always lands on the same page.
    /// '--version x.y.z' overrides the choice, and 'requireEditable: false' allows falling back
    /// to the newest version when nothing is editable, which is fine for a read only export
    /// </summary>
    protected async Task<MetadataTarget?> ResolveTargetAsync(bool requireEditable, bool verbose)
    {
        var versions = await GetVersionsAsync(verbose);
        if (versions.Count == 0)
        {
            Console.WriteLine($"[ERROR] app '{Config.AppId}' has no app store versions.");
            return null;
        }

        var version = PickVersion(versions, requireEditable, verbose);
        if (version is null)
            return null;

        Console.WriteLine($"   -> using version {(string?)version["attributes"]?["versionString"]} ({StateName(version["attributes"]?["appVersionState"])}).");

        var target = new MetadataTarget
        {
            Version = version,
            AppInfo = await GetEditableAppInfoAsync(verbose),
        };

        target.VersionLocalizations = await GetVersionLocalizationsAsync((string?)version["id"] ?? "", verbose);

        if (target.AppInfo is not null)
            target.AppInfoLocalizations = await GetAppInfoLocalizationsAsync((string?)target.AppInfo["id"] ?? "", verbose);

        return target;
    }

    protected async Task<List<JsonNode>> GetVersionsAsync(bool verbose)
    {
        Console.WriteLine("   -> Receiving app store versions...");

        // only the fields below are asked for on purpose: the generated client could not deserialize
        // the empty 'earliestReleaseDate' apple returns for a version without a scheduled release
        var platform = Args.TryGetOption("--platform", "IOS").ToUpperInvariant();

        var response = await Http.GetAsync(
            $"/v1/apps/{Config.AppId}/appStoreVersions"
            + $"?filter[platform]={platform}"
            + "&fields[appStoreVersions]=versionString,appVersionState,appStoreState,platform,createdDate"
            + "&limit=50"
        );

        // newest first, so "the previous version" is just the next item in the list
        var versions = (response["data"] as JsonArray ?? new JsonArray())
            .OfType<JsonNode>()
            .OrderByDescending(v => (DateTimeOffset?)v["attributes"]?["createdDate"] ?? DateTimeOffset.MinValue)
            .ToList();

        if (verbose)
        {
            foreach (var v in versions)
                Console.WriteLine($"      {(string?)v["attributes"]?["versionString"],-10} {StateName(v["attributes"]?["appVersionState"]),-25} created {(DateTimeOffset?)v["attributes"]?["createdDate"]:yyyy-MM-dd}");
        }

        return versions;
    }

    /// <summary>picks the version named by '--version', or the editable one, or the newest one</summary>
    protected JsonNode? PickVersion(List<JsonNode> versions, bool requireEditable, bool verbose)
    {
        var requested = Args.TryGetOption("--version", "");
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var explicitVersion = versions.FirstOrDefault(
                v => string.Equals((string?)v["attributes"]?["versionString"], requested, StringComparison.OrdinalIgnoreCase)
            );

            if (explicitVersion is null)
                Console.WriteLine($"[ERROR] no app store version '{requested}' found. Available: {string.Join(", ", versions.Select(v => (string?)v["attributes"]?["versionString"]))}");

            return explicitVersion;
        }

        var editable = versions.FirstOrDefault(IsEditable);
        if (editable is not null)
            return editable;

        if (requireEditable)
        {
            Console.WriteLine("[ERROR] there is no editable app store version.");
            Console.WriteLine("        create a new version in App Store Connect first, or pass --version <x.y.z> explicitly.");
            return null;
        }

        Console.WriteLine("   -> no editable version found, falling back to the newest one.");
        return versions.First();
    }

    protected static bool IsEditable(JsonNode version)
        => (string?)version["attributes"]?["appVersionState"] is { } state && EditableVersionStates.Contains(state);

    /// <summary>
    /// an app usually has two app infos: the live one and the editable one you are preparing.
    /// only the editable one accepts Name/Subtitle writes
    /// </summary>
    protected async Task<JsonNode?> GetEditableAppInfoAsync(bool verbose)
    {
        Console.WriteLine("   -> Receiving app info...");

        var response = await Http.GetAsync($"/v1/apps/{Config.AppId}/appInfos?limit=50");
        var appInfos = (response["data"] as JsonArray ?? new JsonArray()).OfType<JsonNode>().ToList();

        if (verbose)
        {
            foreach (var info in appInfos)
                Console.WriteLine($"      appInfo {(string?)info["id"]} state {StateName(info["attributes"]?["state"])}");
        }

        var editable = appInfos.FirstOrDefault(i => (string?)i["attributes"]?["state"] == "PREPARE_FOR_SUBMISSION")
            ?? appInfos.FirstOrDefault(i => (string?)i["attributes"]?["state"] != "READY_FOR_DISTRIBUTION")
            ?? appInfos.FirstOrDefault();

        if (editable is null)
            Console.WriteLine("[WARN] no app info found, 'name' and 'subtitle' will be unavailable.");

        return editable;
    }

    protected async Task<List<JsonNode>> GetVersionLocalizationsAsync(string versionId, bool verbose)
    {
        var page = await Http.GetPagedAsync($"/v1/appStoreVersions/{versionId}/appStoreVersionLocalizations?limit=200");
        var localizations = page.Data.OfType<JsonNode>().ToList();

        if (verbose)
            Console.WriteLine($"   -> {localizations.Count} version localizations.");

        return localizations;
    }

    protected async Task<List<JsonNode>> GetAppInfoLocalizationsAsync(string appInfoId, bool verbose)
    {
        var page = await Http.GetPagedAsync($"/v1/appInfos/{appInfoId}/appInfoLocalizations?limit=200");
        var localizations = page.Data.OfType<JsonNode>().ToList();

        if (verbose)
            Console.WriteLine($"   -> {localizations.Count} app info localizations.");

        return localizations;
    }

    /// <summary>
    /// Builds the attributes of an app store version localization PATCH.
    ///
    /// Every attribute is sent, including the ones left null, the way the generated client did, and
    /// App Store Connect reads an explicit null as "clear this field". So a partial update has to
    /// resend the current value of everything it does not mean to change, otherwise setting, say,
    /// the promotional text alone would wipe the description, the keywords and both urls
    /// </summary>
    protected static JsonObject BuildVersionAttributes(
        JsonNode current,
        string? description = null,
        string? keywords = null,
        string? promotionalText = null,
        string? whatsNew = null)
        => new()
        {
            ["description"] = description ?? (string?)current["attributes"]?["description"],
            ["keywords"] = keywords ?? (string?)current["attributes"]?["keywords"],
            ["marketingUrl"] = (string?)current["attributes"]?["marketingUrl"],
            ["promotionalText"] = promotionalText ?? (string?)current["attributes"]?["promotionalText"],
            ["supportUrl"] = (string?)current["attributes"]?["supportUrl"],
            ["whatsNew"] = whatsNew ?? (string?)current["attributes"]?["whatsNew"],
        };

    /// <summary>same as <see cref="BuildVersionAttributes"/>, for the App Information page</summary>
    protected static JsonObject BuildAppInfoAttributes(
        JsonNode current,
        string? name = null,
        string? subtitle = null)
        => new()
        {
            ["name"] = name ?? (string?)current["attributes"]?["name"],
            ["subtitle"] = subtitle ?? (string?)current["attributes"]?["subtitle"],
            ["privacyPolicyUrl"] = (string?)current["attributes"]?["privacyPolicyUrl"],
            ["privacyChoicesUrl"] = (string?)current["attributes"]?["privacyChoicesUrl"],
            ["privacyPolicyText"] = (string?)current["attributes"]?["privacyPolicyText"],
        };

    public static string? GetValue(MetadataField field, JsonNode? info, JsonNode? version)
        => field.Key switch
        {
            "name" => (string?)info?["attributes"]?["name"],
            "subtitle" => (string?)info?["attributes"]?["subtitle"],
            "promotional_text" => (string?)version?["attributes"]?["promotionalText"],
            "description" => (string?)version?["attributes"]?["description"],
            "whats_new" => (string?)version?["attributes"]?["whatsNew"],
            "keywords" => (string?)version?["attributes"]?["keywords"],
            _ => null,
        };

    /// <summary>the language column header, 'English (United States)(en-US)'</summary>
    public static string LocaleColumnName(string locale)
        => LocaleColumns.ColumnName(locale);

    /// <summary>the locale code out of a column header, null when the column is not a language</summary>
    public static string? ExtractLocale(string header)
        => LocaleColumns.Extract(header);

    /// <summary>
    /// resolves an output/input path the way the rest of the tool does:
    /// an explicit argument wins, then the config value, then a file next to config.json, then the desktop
    /// </summary>
    protected string ResolveMetadataPath(string explicitPath, string defaultFileName)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Directory.Exists(explicitPath)
                ? Path.Combine(explicitPath, defaultFileName)
                : explicitPath;
        }

        if (!string.IsNullOrWhiteSpace(Config.AppMetadataFilePath))
            return Config.AppMetadataFilePath;

        if (!string.IsNullOrWhiteSpace(Config.ConfigDirectory) && Directory.Exists(Config.ConfigDirectory))
            return Path.Combine(Config.ConfigDirectory, defaultFileName);

        // ask the system where the desktop is: on Windows it can live under OneDrive or carry a localized name
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        return Path.Combine(desktop, defaultFileName);
    }

    /// <summary>the first argument after the command name, when it is not an option</summary>
    protected string GetPositionalPath()
        => Args.Length > 1 && !Args[1].StartsWith("-") ? Args[1] : "";

    protected static void PrintApiError(string what, Exception ex)
    {
                if (ex is AscApiException asc)
        {
            Console.WriteLine($"[API ERROR] {what}: {asc.Message}");
            Console.WriteLine($"Status: {asc.StatusCode}");
            Console.WriteLine($"Response Body: {asc.ResponseBody}");
            return;
        }

        Console.WriteLine($"[ERROR] {what}: {ex.Message}");
    }
}
