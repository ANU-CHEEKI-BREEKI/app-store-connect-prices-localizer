using System.Text.Json.Nodes;

/// <summary>
/// Creates all 50 App Store Connect supported localizations on the editable version and app info page.
/// Uses existing primary locale texts as initial values so that every language exists on App Store Connect,
/// allowing 'export-metadata' to produce a full table with columns for all supported languages.
/// </summary>
public class Command_CreateAllLocales : AppMetadataCommandBase
{
    public override string Name => "create-all-locales";
    public override string Description => "Creates all 50 App Store Connect supported localizations for the app store version and app information page using existing texts as template.";

    public override void PrintHelp()
    {
        Console.WriteLine("create-all-locales [--version <x.y.z>] [-n] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("Populates missing localizations across all 50 supported App Store Connect locales using existing primary locale texts.");
        CommandLinesUtils.PrintDescription("After running this command, 'export-metadata' will output columns for all 50 languages supported by the App Store.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--version <x.y.z>",
            "Create localizations on this exact app store version instead of the editable one."
        );
        CommandLinesUtils.PrintOption(
            "-n",
            "Dry run: print all localizations that would be created, without writing to App Store Connect."
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
            Console.WriteLine("   -> Creating all App Store Connect localizations...");

            if (dryRun)
                Console.WriteLine("   -> DRY RUN, nothing will be written.");

            var target = await ResolveTargetAsync(requireEditable: true, verbose: verbose);
            if (target is null)
                return;

            // template text sources (prefer en-US, or first available localization)
            var primaryAppInfo = target.AppInfoLocalizations.FirstOrDefault(l => string.Equals((string?)l["attributes"]?["locale"], "en-US", StringComparison.OrdinalIgnoreCase))
                                 ?? target.AppInfoLocalizations.FirstOrDefault();

            var primaryVersion = target.VersionLocalizations.FirstOrDefault(l => string.Equals((string?)l["attributes"]?["locale"], "en-US", StringComparison.OrdinalIgnoreCase))
                                 ?? target.VersionLocalizations.FirstOrDefault();

            var defaultName = (string?)primaryAppInfo?["attributes"]?["name"] ?? "App";
            var defaultSubtitle = (string?)primaryAppInfo?["attributes"]?["subtitle"];

            var defaultDescription = (string?)primaryVersion?["attributes"]?["description"] ?? "App description";
            var defaultKeywords = (string?)primaryVersion?["attributes"]?["keywords"];
            var defaultPromotionalText = (string?)primaryVersion?["attributes"]?["promotionalText"];
            var defaultWhatsNew = (string?)primaryVersion?["attributes"]?["whatsNew"];

            var createdAppInfo = new List<string>();
            var createdVersion = new List<string>();
            var skippedAppInfo = new List<string>();
            var skippedVersion = new List<string>();
            var failed = new List<string>();

            foreach (var locale in AppStoreLocales.Supported)
            {
                // App Information localization
                if (target.AppInfo is not null)
                {
                    var existingInfo = target.FindAppInfoLocalization(locale);
                    if (existingInfo is not null)
                    {
                        if (verbose)
                            Console.WriteLine($"      [EXISTS] App Info localization for {locale}");
                        skippedAppInfo.Add(locale);
                    }
                    else
                    {
                        Console.WriteLine($"      [NEW] App Info localization for {locale}");

                        if (dryRun)
                        {
                            createdAppInfo.Add(locale);
                        }
                        else
                        {
                            try
                            {
                                var request = AscHttp.Body(
                                    "appInfoLocalizations",
                                    new JsonObject
                                    {
                                        ["appInfo"] = AscHttp.Link("appInfos", (string?)target.AppInfo["id"] ?? ""),
                                    },
                                    new JsonObject
                                    {
                                        ["locale"] = locale,
                                        ["name"] = defaultName,
                                        ["subtitle"] = defaultSubtitle,
                                    }
                                );

                                var response = await Http.PostAsync("/v1/appInfoLocalizations", request);
                                target.AppInfoLocalizations.Add(response["data"]!);
                                createdAppInfo.Add(locale);
                            }
                            catch (AscApiException apiEx) when (apiEx.StatusCode == 409 || apiEx.ResponseBody.Contains("DUPLICATE"))
                            {
                                Console.WriteLine($"      [EXISTS] App Info localization for {locale}");
                                skippedAppInfo.Add(locale);
                            }
                            catch (Exception ex)
                            {
                                PrintApiError($"failed to create app info localization for {locale}", ex);
                                failed.Add($"{locale} (app info)");
                            }
                        }
                    }
                }

                // Version localization
                var existingVer = target.FindVersionLocalization(locale);
                if (existingVer is not null)
                {
                    if (verbose)
                        Console.WriteLine($"      [EXISTS] Version localization for {locale}");
                    skippedVersion.Add(locale);
                }
                else
                {
                    Console.WriteLine($"      [NEW] Version localization for {locale}");

                    if (dryRun)
                    {
                        createdVersion.Add(locale);
                    }
                    else
                    {
                        try
                        {
                            var request = AscHttp.Body(
                                "appStoreVersionLocalizations",
                                new JsonObject
                                {
                                    ["appStoreVersion"] = AscHttp.Link("appStoreVersions", (string?)target.Version["id"] ?? ""),
                                },
                                new JsonObject
                                {
                                    ["description"] = defaultDescription,
                                    ["locale"] = locale,
                                    ["keywords"] = defaultKeywords,
                                    ["promotionalText"] = defaultPromotionalText,
                                    ["whatsNew"] = defaultWhatsNew,
                                }
                            );

                            var response = await Http.PostAsync("/v1/appStoreVersionLocalizations", request);
                            target.VersionLocalizations.Add(response["data"]!);
                            createdVersion.Add(locale);
                        }
                        catch (AscApiException apiEx) when (apiEx.StatusCode == 409 || apiEx.ResponseBody.Contains("DUPLICATE"))
                        {
                            Console.WriteLine($"      [EXISTS] Version localization for {locale}");
                            skippedVersion.Add(locale);
                        }
                        catch (Exception ex)
                        {
                            PrintApiError($"failed to create version localization for {locale}", ex);
                            failed.Add($"{locale} (version)");
                        }
                    }
                }
            }

            PrintSummary(createdAppInfo, createdVersion, skippedAppInfo, skippedVersion, failed);
        }
        catch (Exception ex)
        {
            PrintApiError("failed to create all localizations", ex);
        }
    }

    private void PrintSummary(List<string> createdAppInfo, List<string> createdVersion, List<string> skippedAppInfo, List<string> skippedVersion, List<string> failed)
    {
        Console.WriteLine();
        Console.WriteLine("summary:");
        Console.WriteLine($"   created app info localizations: {createdAppInfo.Count}");
        Console.WriteLine($"   created version localizations:  {createdVersion.Count}");
        Console.WriteLine($"   already existing app info:     {skippedAppInfo.Count}");
        Console.WriteLine($"   already existing version:      {skippedVersion.Count}");
        Console.WriteLine($"   failed:                        {failed.Count}");
        foreach (var f in failed)
            Console.WriteLine($"      -> {f}");
    }
}
