using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

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
    private static readonly AppVersionState[] EditableVersionStates =
    {
        AppVersionState.PREPAREFORSUBMISSION,
        AppVersionState.DEVELOPERREJECTED,
        AppVersionState.REJECTED,
        AppVersionState.METADATAREJECTED,
        AppVersionState.INVALIDBINARY,
        AppVersionState.READYFORREVIEW,
        AppVersionState.WAITINGFORREVIEW,
    };

    /// <summary>everything the commands need to touch a single product page, in a single place</summary>
    public class MetadataTarget
    {
        public AppStoreVersion Version { get; set; } = null!;
        public AppInfo? AppInfo { get; set; }

        public List<AppStoreVersionLocalization> VersionLocalizations { get; set; } = new();
        public List<AppInfoLocalization> AppInfoLocalizations { get; set; } = new();

        public string VersionString => Version.Attributes?.VersionString ?? "?";

        /// <summary>every locale that exists on either of the two pages</summary>
        public List<string> Locales => VersionLocalizations
            .Select(l => l.Attributes?.Locale ?? "")
            .Concat(AppInfoLocalizations.Select(l => l.Attributes?.Locale ?? ""))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        public AppStoreVersionLocalization? FindVersionLocalization(string locale)
            => VersionLocalizations.FirstOrDefault(l => string.Equals(l.Attributes?.Locale, locale, StringComparison.OrdinalIgnoreCase));

        public AppInfoLocalization? FindAppInfoLocalization(string locale)
            => AppInfoLocalizations.FirstOrDefault(l => string.Equals(l.Attributes?.Locale, locale, StringComparison.OrdinalIgnoreCase));
    }

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

        Console.WriteLine($"   -> using version {version.Attributes?.VersionString} ({version.Attributes?.AppVersionState}).");

        var target = new MetadataTarget
        {
            Version = version,
            AppInfo = await GetEditableAppInfoAsync(verbose),
        };

        target.VersionLocalizations = await GetVersionLocalizationsAsync(version.Id, verbose);

        if (target.AppInfo is not null)
            target.AppInfoLocalizations = await GetAppInfoLocalizationsAsync(target.AppInfo.Id, verbose);

        return target;
    }

    protected async Task<List<AppStoreVersion>> GetVersionsAsync(bool verbose)
    {
        Console.WriteLine("   -> Receiving app store versions...");

        // only the fields below are asked for on purpose: the generated client can not deserialize
        // the empty 'earliestReleaseDate' apple returns for a version without a scheduled release
        var platform = Args.TryGetOption("--platform", "IOS").ToUpperInvariant();

        var response = await new AppsApi(Service).AppsAppStoreVersionsGetToManyRelatedAsync(
            Config.AppId,
            filterPlatform: new List<string> { platform },
            fieldsAppStoreVersions: new List<string> { "versionString", "appVersionState", "appStoreState", "platform", "createdDate" },
            limit: 50
        );

        // newest first, so "the previous version" is just the next item in the list
        var versions = (response.Data ?? new())
            .OrderByDescending(v => v.Attributes?.CreatedDate ?? DateTimeOffset.MinValue)
            .ToList();

        if (verbose)
        {
            foreach (var v in versions)
                Console.WriteLine($"      {v.Attributes?.VersionString,-10} {v.Attributes?.AppVersionState,-25} created {v.Attributes?.CreatedDate:yyyy-MM-dd}");
        }

        return versions;
    }

    /// <summary>picks the version named by '--version', or the editable one, or the newest one</summary>
    protected AppStoreVersion? PickVersion(List<AppStoreVersion> versions, bool requireEditable, bool verbose)
    {
        var requested = Args.TryGetOption("--version", "");
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var explicitVersion = versions.FirstOrDefault(
                v => string.Equals(v.Attributes?.VersionString, requested, StringComparison.OrdinalIgnoreCase)
            );

            if (explicitVersion is null)
                Console.WriteLine($"[ERROR] no app store version '{requested}' found. Available: {string.Join(", ", versions.Select(v => v.Attributes?.VersionString))}");

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

    protected static bool IsEditable(AppStoreVersion version)
        => version.Attributes?.AppVersionState is { } state && EditableVersionStates.Contains(state);

    /// <summary>
    /// an app usually has two app infos: the live one and the editable one you are preparing.
    /// only the editable one accepts Name/Subtitle writes
    /// </summary>
    protected async Task<AppInfo?> GetEditableAppInfoAsync(bool verbose)
    {
        Console.WriteLine("   -> Receiving app info...");

        var response = await new AppsApi(Service).AppsAppInfosGetToManyRelatedAsync(Config.AppId, limit: 50);
        var appInfos = response.Data ?? new();

        if (verbose)
        {
            foreach (var info in appInfos)
                Console.WriteLine($"      appInfo {info.Id} state {info.Attributes?.State}");
        }

        var editable = appInfos.FirstOrDefault(i => i.Attributes?.State == AppInfoAttributes.StateEnum.PREPAREFORSUBMISSION)
            ?? appInfos.FirstOrDefault(i => i.Attributes?.State != AppInfoAttributes.StateEnum.READYFORDISTRIBUTION)
            ?? appInfos.FirstOrDefault();

        if (editable is null)
            Console.WriteLine("[WARN] no app info found, 'name' and 'subtitle' will be unavailable.");

        return editable;
    }

    protected async Task<List<AppStoreVersionLocalization>> GetVersionLocalizationsAsync(string versionId, bool verbose)
    {
        var api = new AppStoreVersionsApi(Service);
        var localizations = await FetchAllPagesAsync<AppStoreVersionLocalizationsResponse, AppStoreVersionLocalization>(
            api.AsynchronousClient,
            api.Configuration,
            () => api.AppStoreVersionsAppStoreVersionLocalizationsGetToManyRelatedAsync(versionId, limit: 200),
            r => r.Data,
            r => r.Links?.Next,
            verbose
        );

        if (verbose)
            Console.WriteLine($"   -> {localizations.Count} version localizations.");

        return localizations;
    }

    protected async Task<List<AppInfoLocalization>> GetAppInfoLocalizationsAsync(string appInfoId, bool verbose)
    {
        var api = new AppInfosApi(Service);
        var localizations = await FetchAllPagesAsync<AppInfoLocalizationsResponse, AppInfoLocalization>(
            api.AsynchronousClient,
            api.Configuration,
            () => api.AppInfosAppInfoLocalizationsGetToManyRelatedAsync(appInfoId, limit: 200),
            r => r.Data,
            r => r.Links?.Next,
            verbose
        );

        if (verbose)
            Console.WriteLine($"   -> {localizations.Count} app info localizations.");

        return localizations;
    }

    /// <summary>
    /// Builds the attributes of an app store version localization PATCH.
    ///
    /// The generated client serializes every attribute, including the ones left null, and
    /// App Store Connect reads an explicit null as "clear this field". So a partial update has to
    /// resend the current value of everything it does not mean to change, otherwise setting, say,
    /// the promotional text alone would wipe the description, the keywords and both urls
    /// </summary>
    protected static AppStoreVersionLocalizationUpdateRequestDataAttributes BuildVersionAttributes(
        AppStoreVersionLocalization current,
        string? description = null,
        string? keywords = null,
        string? promotionalText = null,
        string? whatsNew = null)
        => new(
            description: description ?? current.Attributes?.Description,
            keywords: keywords ?? current.Attributes?.Keywords,
            marketingUrl: current.Attributes?.MarketingUrl,
            promotionalText: promotionalText ?? current.Attributes?.PromotionalText,
            supportUrl: current.Attributes?.SupportUrl,
            whatsNew: whatsNew ?? current.Attributes?.WhatsNew
        );

    /// <summary>same as <see cref="BuildVersionAttributes"/>, for the App Information page</summary>
    protected static AppInfoLocalizationUpdateRequestDataAttributes BuildAppInfoAttributes(
        AppInfoLocalization current,
        string? name = null,
        string? subtitle = null)
        => new(
            name: name ?? current.Attributes?.Name,
            subtitle: subtitle ?? current.Attributes?.Subtitle,
            privacyPolicyUrl: current.Attributes?.PrivacyPolicyUrl,
            privacyChoicesUrl: current.Attributes?.PrivacyChoicesUrl,
            privacyPolicyText: current.Attributes?.PrivacyPolicyText
        );

    public static string? GetValue(MetadataField field, AppInfoLocalization? info, AppStoreVersionLocalization? version)
        => field.Key switch
        {
            "name" => info?.Attributes?.Name,
            "subtitle" => info?.Attributes?.Subtitle,
            "promotional_text" => version?.Attributes?.PromotionalText,
            "description" => version?.Attributes?.Description,
            "whats_new" => version?.Attributes?.WhatsNew,
            "keywords" => version?.Attributes?.Keywords,
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
        if (ex is AppStoreConnect.Net.Client.ApiException api)
        {
            Console.WriteLine($"[API ERROR] {what}: {api.Message}");
            Console.WriteLine($"Status: {api.ErrorCode}");
            Console.WriteLine($"Response Body: {api.ErrorContent}");
            return;
        }

        Console.WriteLine($"[ERROR] {what}: {ex.Message}");
    }
}
