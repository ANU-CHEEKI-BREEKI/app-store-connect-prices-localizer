using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Model;

/// <summary>
/// App Store Connect copies almost every text into a newly created version, but not the Promotional Text.
/// doing that by hand for 40 locales is the kind of thing this tool exists for
/// </summary>
public class Command_CopyPromoText : AppMetadataCommandBase
{
    public override string Name => "copy-promo";
    public override string Description => "Copies the Promotional Text of every locale from the previous app store version into the current, editable one. App Store Connect does not carry that field over when a new version is created.";

    public override void PrintHelp()
    {
        Console.WriteLine("copy-promo [--from <x.y.z>] [--version <x.y.z>] [-n] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("The source is the newest version that is not the target and actually has a Promotional Text, the target is the editable version. Both can be pinned explicitly.");
        CommandLinesUtils.PrintDescription("Locales that already have the same text are left alone, and a locale that does not exist on the target version yet is reported instead of being created, use 'import-metadata' to add languages.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--from <x.y.z>",
            "Copy from this exact app store version instead of the previous one."
        );
        CommandLinesUtils.PrintOption(
            "--version <x.y.z>",
            "Copy into this exact app store version instead of the editable one."
        );
        CommandLinesUtils.PrintOption(
            "-n",
            "Dry run: print everything that would be copied, without sending a single write request."
        );
        CommandLinesUtils.PrintOption(
            "-v",
            "Include additional verbose output"
        );
    }

    protected override async Task InternalExecuteAsync()
    {
        var verbose = Args.HasFlag("-v");
        var dryRun = Args.HasFlag("-n") || Args.HasFlag("--dry-run");

        try
        {
            Console.WriteLine("   -> Copying Promotional Text from the previous version...");

            if (dryRun)
                Console.WriteLine("   -> DRY RUN, nothing will be written.");

            var versions = await GetVersionsAsync(verbose);
            if (versions.Count == 0)
            {
                Console.WriteLine($"[ERROR] app '{Config.AppId}' has no app store versions.");
                return;
            }

            var target = PickVersion(versions, requireEditable: true, verbose);
            if (target is null)
                return;

            Console.WriteLine($"   -> target version {target.Attributes?.VersionString} ({target.Attributes?.AppVersionState}).");

            var source = await ResolveSourceAsync(versions, target, verbose);
            if (source is null)
                return;

            var targetLocalizations = await GetVersionLocalizationsAsync(target.Id, verbose);

            var copied = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();

            foreach (var sourceLocalization in source.Localizations)
            {
                var locale = sourceLocalization.Attributes?.Locale;
                var text = sourceLocalization.Attributes?.PromotionalText;

                if (string.IsNullOrWhiteSpace(locale) || string.IsNullOrWhiteSpace(text))
                    continue;

                var targetLocalization = targetLocalizations.FirstOrDefault(
                    l => string.Equals(l.Attributes?.Locale, locale, StringComparison.OrdinalIgnoreCase)
                );

                if (targetLocalization is null)
                {
                    Console.WriteLine($"      [SKIP] {locale} does not exist on version {target.Attributes?.VersionString}.");
                    skipped.Add(locale);
                    continue;
                }

                if (string.Equals(targetLocalization.Attributes?.PromotionalText, text, StringComparison.Ordinal))
                {
                    if (verbose)
                        Console.WriteLine($"      [SAME] {locale} already has this Promotional Text.");
                    continue;
                }

                Console.WriteLine($"      [SET] {locale}: {Preview(text)}");

                if (dryRun)
                {
                    copied.Add(locale);
                    continue;
                }

                try
                {
                    var request = new AppStoreVersionLocalizationUpdateRequest(
                        data: new AppStoreVersionLocalizationUpdateRequestData(
                            type: AppStoreVersionLocalizationUpdateRequestData.TypeEnum.AppStoreVersionLocalizations,
                            id: targetLocalization.Id,
                            attributes: BuildVersionAttributes(targetLocalization, promotionalText: text)
                        )
                    );

                    await new AppStoreVersionLocalizationsApi(Service)
                        .AppStoreVersionLocalizationsUpdateInstanceAsync(targetLocalization.Id, request);

                    copied.Add(locale);
                }
                catch (Exception ex)
                {
                    PrintApiError($"failed to set Promotional Text for {locale}", ex);
                    failed.Add(locale);
                }
            }

            PrintSummary(source.Version, target, copied, skipped, failed);
        }
        catch (Exception ex)
        {
            PrintApiError("failed to copy Promotional Text", ex);
        }
    }

    private record SourceVersion(AppStoreVersion Version, List<AppStoreVersionLocalization> Localizations);

    /// <summary>
    /// finds the version to copy from. without '--from' it walks back through the older versions
    /// until it finds one that actually has a Promotional Text somewhere
    /// </summary>
    private async Task<SourceVersion?> ResolveSourceAsync(List<AppStoreVersion> versions, AppStoreVersion target, bool verbose)
    {
        var requested = Args.TryGetOption("--from", "");

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var explicitVersion = versions.FirstOrDefault(
                v => string.Equals(v.Attributes?.VersionString, requested, StringComparison.OrdinalIgnoreCase)
            );

            if (explicitVersion is null)
            {
                Console.WriteLine($"[ERROR] no app store version '{requested}' found. Available: {string.Join(", ", versions.Select(v => v.Attributes?.VersionString))}");
                return null;
            }

            var localizations = await GetVersionLocalizationsAsync(explicitVersion.Id, verbose);
            return new SourceVersion(explicitVersion, localizations);
        }

        // versions come back newest first, so the first older one is the previous version
        var candidates = versions.Where(v => v.Id != target.Id).ToList();

        foreach (var candidate in candidates)
        {
            var localizations = await GetVersionLocalizationsAsync(candidate.Id, verbose);

            var hasText = localizations.Any(l => !string.IsNullOrWhiteSpace(l.Attributes?.PromotionalText));
            if (hasText)
            {
                Console.WriteLine($"   -> source version {candidate.Attributes?.VersionString} ({candidate.Attributes?.AppVersionState}).");
                return new SourceVersion(candidate, localizations);
            }

            Console.WriteLine($"   -> version {candidate.Attributes?.VersionString} has no Promotional Text, looking further back...");
        }

        Console.WriteLine("[ERROR] no previous version with a Promotional Text found, nothing to copy.");
        return null;
    }

    private static string Preview(string value)
        => value.Length <= 60 ? value : value.Substring(0, 60) + "...";

    private void PrintSummary(AppStoreVersion source, AppStoreVersion target, List<string> copied, List<string> skipped, List<string> failed)
    {
        Console.WriteLine();
        Console.WriteLine("summary:");
        Console.WriteLine($"   {source.Attributes?.VersionString} -> {target.Attributes?.VersionString}");

        Console.WriteLine($"   copied:  {copied.Count}");
        foreach (var item in copied)
            Console.WriteLine($"      -> {item}");

        Console.WriteLine($"   skipped: {skipped.Count} (locale is missing on the target version)");
        foreach (var item in skipped)
            Console.WriteLine($"      -> {item}");

        Console.WriteLine($"   failed:  {failed.Count}");
        foreach (var item in failed)
            Console.WriteLine($"      -> {item}");
    }
}
