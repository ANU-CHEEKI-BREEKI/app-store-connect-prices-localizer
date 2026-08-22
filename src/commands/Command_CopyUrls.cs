using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Model;

/// <summary>
/// Copies the Privacy Policy, Support and Marketing url from one language onto all the others.
///
/// App Store Connect asks for these per language and does not carry them over when a language is
/// added, so the review refuses to start with a wall of
/// "Greek - Privacy Policy URL - This field is required", one line per language. The urls are
/// almost always the same for every language, and typing the same one thirty times is not a choice
/// anybody makes on purpose.
///
/// Not 'copy-promo': that one carries the Promotional Text from the previous version into the new
/// one. This is one language to the others, within the same version.
/// </summary>
public class Command_CopyUrls : AppMetadataCommandBase
{
    private const string PrivacyField = "privacy_policy_url";
    private const string SupportField = "support_url";
    private const string MarketingField = "marketing_url";

    private static readonly string[] AllFields = { PrivacyField, SupportField, MarketingField };

    public override string Name => "copy-urls";

    public override string Description
        => "Copies the Privacy Policy, Support and Marketing url of one language onto every other language, filling in only the ones that are empty.";

    public override void PrintHelp()
    {
        Console.WriteLine("copy-urls [--from <locale>] [--fields privacy,support,marketing] [--overwrite] [--version <x.y.z>] [-n] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("App Store Connect keeps these three per language and never fills them in for a language you add, so the review refuses to start with one 'Privacy Policy URL - This field is required' per language.");
        CommandLinesUtils.PrintDescription("The Privacy Policy url lives on the App Information page and is not tied to any version. The Support and Marketing urls live on the app store version. This command writes both places, which is why it is one command and not two.");
        CommandLinesUtils.PrintDescription("A language that already has its own url keeps it: a localized support page is a real thing and this must not quietly undo it. Pass --overwrite to make every language match the source instead.");
        CommandLinesUtils.PrintDescription("A url the source language does not have is skipped for everyone, rather than clearing it everywhere.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption("--from <locale>", "Copy from this language. Default is the 'DefaultLocale' of your config.json, or en-US.");
        CommandLinesUtils.PrintOption("--fields <a,b,c>", $"Copy only these: privacy, support, marketing. Default is all three.");
        CommandLinesUtils.PrintOption("--overwrite", "Replace a url a language already has, instead of leaving it alone.");
        CommandLinesUtils.PrintOption("--version <x.y.z>", "Write to this exact app store version instead of the editable one.");
        CommandLinesUtils.PrintOption("-n|--dry-run", "Print everything that would be copied, without sending a single write request.");
        CommandLinesUtils.PrintOption("-v", "Include additional verbose output");
    }

    protected override async Task InternalExecuteAsync()
    {
        var verbose = Args.HasFlag("-v");
        var dryRun = Args.HasFlag("-n") || Args.HasFlag("--dry-run");
        var overwrite = Args.HasFlag("--overwrite");

        var fields = ParseFields();

        try
        {
            Console.WriteLine("   -> Copying the urls across languages...");

            if (dryRun)
                Console.WriteLine("   -> DRY RUN, nothing will be written.");

            var target = await ResolveTargetAsync(requireEditable: true, verbose);
            if (target is null)
                return;

            var source = Args.TryGetOption("--from", "");
            if (string.IsNullOrWhiteSpace(source))
                source = string.IsNullOrWhiteSpace(Config.DefaultLocale) ? "en-US" : Config.DefaultLocale;

            var sourceInfo = target.FindAppInfoLocalization(source);
            var sourceVersion = target.FindVersionLocalization(source);

            if (sourceInfo is null && sourceVersion is null)
            {
                Console.WriteLine($"[ERROR] the app has no '{source}' localization to copy from.");
                Console.WriteLine("        pass --from <locale>, or set 'DefaultLocale' in your config.json");
                return;
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [PrivacyField] = sourceInfo?.Attributes?.PrivacyPolicyUrl,
                [SupportField] = sourceVersion?.Attributes?.SupportUrl,
                [MarketingField] = sourceVersion?.Attributes?.MarketingUrl,
            };

            Console.WriteLine($"   -> source locale {source}");

            foreach (var field in fields)
            {
                var value = values[field];
                Console.WriteLine(string.IsNullOrWhiteSpace(value)
                    ? $"   -> {field,-20} (empty in the source, skipped for everyone)"
                    : $"   -> {field,-20} {value}");
            }

            fields = fields.Where(f => !string.IsNullOrWhiteSpace(values[f])).ToList();

            if (fields.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine($"nothing to copy, '{source}' has none of the requested urls.");
                return;
            }

            var updated = new List<string>();
            var kept = new List<string>();
            var failed = new List<string>();

            foreach (var locale in target.Locales)
            {
                if (string.Equals(locale, source, StringComparison.OrdinalIgnoreCase))
                    continue;

                await CopyToLocaleAsync(target, locale, fields, values, overwrite, dryRun, verbose, updated, kept, failed);
            }

            PrintSummary(target, source, updated, kept, failed);
        }
        catch (Exception ex)
        {
            PrintApiError("failed to copy the urls", ex);
        }
    }

    private List<string> ParseFields()
    {
        var requested = Args.TryGetOption("--fields", "")
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (requested.Count == 0)
            return AllFields.ToList();

        var fields = new List<string>();

        foreach (var name in requested)
        {
            var field = name.ToLowerInvariant() switch
            {
                "privacy" or "privacy_policy_url" or "privacy-policy" => PrivacyField,
                "support" or "support_url" => SupportField,
                "marketing" or "marketing_url" => MarketingField,
                _ => null,
            };

            if (field is null)
            {
                Console.WriteLine($"[WARN] '{name}' is not one of privacy, support, marketing. Ignored.");
                continue;
            }

            if (!fields.Contains(field))
                fields.Add(field);
        }

        return fields.Count > 0 ? fields : AllFields.ToList();
    }

    /// <summary>
    /// One language, up to two requests: the privacy policy lives on the App Information page and
    /// the other two on the version, and neither write can carry the other's field
    /// </summary>
    private async Task CopyToLocaleAsync(
        MetadataTarget target, string locale, List<string> fields, Dictionary<string, string?> values,
        bool overwrite, bool dryRun, bool verbose,
        List<string> updated, List<string> kept, List<string> failed)
    {
        var set = new List<string>();
        var left = new List<string>();

        var info = target.FindAppInfoLocalization(locale);
        var version = target.FindVersionLocalization(locale);

        string? privacy = null;
        string? support = null;
        string? marketing = null;

        if (fields.Contains(PrivacyField) && info is not null)
            Decide(PrivacyField, info.Attributes?.PrivacyPolicyUrl, ref privacy);

        if (fields.Contains(SupportField) && version is not null)
            Decide(SupportField, version.Attributes?.SupportUrl, ref support);

        if (fields.Contains(MarketingField) && version is not null)
            Decide(MarketingField, version.Attributes?.MarketingUrl, ref marketing);

        if (set.Count == 0)
        {
            if (left.Count > 0)
            {
                if (verbose)
                    Console.WriteLine($"      [KEEP] {locale,-10} {string.Join(", ", left)} already set");

                kept.AddRange(left.Select(f => $"{locale} {f}"));
            }
            else if (verbose)
            {
                Console.WriteLine($"      [SAME] {locale,-10} nothing to do");
            }

            return;
        }

        Console.WriteLine($"      [SET]  {locale,-10} {string.Join(", ", set)}");

        if (left.Count > 0)
            kept.AddRange(left.Select(f => $"{locale} {f}"));

        if (dryRun)
        {
            updated.AddRange(set.Select(f => $"{locale} {f}"));
            return;
        }

        if (privacy is not null && info is not null)
        {
            try
            {
                // the generated client serializes nulls and App Store Connect reads an explicit null
                // as "clear this field", so everything else on the page is resent as it is
                await new AppInfoLocalizationsApi(Service).AppInfoLocalizationsUpdateInstanceAsync(
                    info.Id,
                    new AppInfoLocalizationUpdateRequest(
                        new AppInfoLocalizationUpdateRequestData(
                            AppInfoLocalizationUpdateRequestData.TypeEnum.AppInfoLocalizations,
                            info.Id,
                            new AppInfoLocalizationUpdateRequestDataAttributes(
                                name: info.Attributes?.Name,
                                subtitle: info.Attributes?.Subtitle,
                                privacyPolicyUrl: privacy,
                                privacyChoicesUrl: info.Attributes?.PrivacyChoicesUrl,
                                privacyPolicyText: info.Attributes?.PrivacyPolicyText
                            )
                        )
                    )
                );

                updated.Add($"{locale} {PrivacyField}");
            }
            catch (Exception ex)
            {
                PrintApiError($"failed to set the privacy policy url of {locale}", ex);
                failed.Add($"{locale} {PrivacyField}");
            }
        }

        if ((support is not null || marketing is not null) && version is not null)
        {
            try
            {
                await new AppStoreVersionLocalizationsApi(Service).AppStoreVersionLocalizationsUpdateInstanceAsync(
                    version.Id,
                    new AppStoreVersionLocalizationUpdateRequest(
                        new AppStoreVersionLocalizationUpdateRequestData(
                            AppStoreVersionLocalizationUpdateRequestData.TypeEnum.AppStoreVersionLocalizations,
                            version.Id,
                            new AppStoreVersionLocalizationUpdateRequestDataAttributes(
                                description: version.Attributes?.Description,
                                keywords: version.Attributes?.Keywords,
                                marketingUrl: marketing ?? version.Attributes?.MarketingUrl,
                                promotionalText: version.Attributes?.PromotionalText,
                                supportUrl: support ?? version.Attributes?.SupportUrl,
                                whatsNew: version.Attributes?.WhatsNew
                            )
                        )
                    )
                );

                if (support is not null) updated.Add($"{locale} {SupportField}");
                if (marketing is not null) updated.Add($"{locale} {MarketingField}");
            }
            catch (Exception ex)
            {
                PrintApiError($"failed to set the version urls of {locale}", ex);
                if (support is not null) failed.Add($"{locale} {SupportField}");
                if (marketing is not null) failed.Add($"{locale} {MarketingField}");
            }
        }

        void Decide(string field, string? current, ref string? slot)
        {
            var wanted = values[field];

            if (string.Equals(current, wanted, StringComparison.Ordinal))
                return;

            if (!string.IsNullOrWhiteSpace(current) && !overwrite)
            {
                left.Add(field);
                return;
            }

            slot = wanted;
            set.Add(field);
        }
    }

    private void PrintSummary(MetadataTarget target, string source, List<string> updated, List<string> kept, List<string> failed)
    {
        Console.WriteLine();
        Console.WriteLine("summary:");
        Console.WriteLine($"   version: {target.VersionString}");
        Console.WriteLine($"   source:  {source}");

        Console.WriteLine($"   set:     {updated.Count}");
        foreach (var item in updated)
            Console.WriteLine($"      -> {item}");

        Console.WriteLine($"   kept:    {kept.Count} (the language has its own, --overwrite to replace)");
        foreach (var item in kept)
            Console.WriteLine($"      -> {item}");

        Console.WriteLine($"   failed:  {failed.Count}");
        foreach (var item in failed)
            Console.WriteLine($"      -> {item}");

        if (updated.Count == 0 && failed.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("nothing to copy, every language already has these urls.");
        }
    }
}
