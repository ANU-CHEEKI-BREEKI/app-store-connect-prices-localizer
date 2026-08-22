/// <summary>
/// Gives every achievement language the image of the primary one, without touching any text.
///
/// 'locales import achievements' already does this for the languages it writes, so this exists for
/// the ones that were added some other way - by hand in the console, or by an import that ran with
/// --no-images - and for replacing an image that has since been redrawn.
/// </summary>
public class Command_LocalesSyncAchievementImages : GameCenterCommandBase
{
    protected override TextField[] Fields => Array.Empty<TextField>();

    public override string Name => "locales sync achievement-images";

    public override string Description
        => "Copies the image of the primary language onto every other language of every Game Center achievement. Texts are never touched.";

    public override void PrintHelp()
    {
        Console.WriteLine("locales sync achievement-images [--from <locale>] [--overwrite] [-n] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("App Store Connect keeps a separate image per language, and will store a language with text and no image quite happily - it just stays 'Prepare for Submission' forever and blocks the release with a message that does not mention images.");
        CommandLinesUtils.PrintDescription("By default only the languages that have no image at all are filled in. A language that already has its own image is left alone, since it may well be deliberate. Pass --overwrite to make every language match the source instead.");
        CommandLinesUtils.PrintDescription("The source is the locale configured as 'DefaultLocale', falling back to whichever language of that achievement has an image. There is no api for pointing two languages at one image, so the bytes are downloaded once per achievement and uploaded to each language.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption("--from <locale>", "Copy from this language instead of the configured 'DefaultLocale'.");
        CommandLinesUtils.PrintOption("--overwrite", "Replace the image of every language, not just the ones that have none.");
        CommandLinesUtils.PrintOption("-n|--dry-run", "Print everything that would be copied, without sending a single write request.");
        CommandLinesUtils.PrintOption("-v", "Include additional verbose output");
    }

    protected override async Task InternalExecuteAsync()
    {
        var overwrite = Args.HasFlag("--overwrite");
        var from = Args.TryGetOption("--from", "");

        try
        {
            Console.WriteLine("   -> Syncing Game Center achievement images...");

            if (DryRun)
                Console.WriteLine("   -> DRY RUN, nothing will be written.");

            var achievements = await GetAchievementsAsync(Verbose);
            if (achievements is null)
                return;

            if (achievements.Count == 0)
            {
                Console.WriteLine("   -> this app has no achievements.");
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

            var copied = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();

            foreach (var achievement in achievements)
            {
                var source = string.IsNullOrWhiteSpace(from)
                    ? FindImageSource(achievement)
                    : achievement.Find(from);

                if (source is null || achievement.ImageOf(source) is null)
                {
                    var where = string.IsNullOrWhiteSpace(from) ? "any language" : from;
                    Console.WriteLine($"      [SKIP] {achievement.VendorIdentifier} has no image in {where} to copy from.");
                    skipped.Add($"{achievement.VendorIdentifier} (no source image)");
                    continue;
                }

                var targets = achievement.Localizations
                    .Where(l => l.Id != source.Id)
                    .Where(l => overwrite || achievement.ImageOf(l) is null)
                    .ToList();

                if (targets.Count == 0)
                {
                    if (Verbose)
                        Console.WriteLine($"      [SAME] {achievement.VendorIdentifier}: every language already has an image.");
                    continue;
                }

                Console.WriteLine($"      {achievement.VendorIdentifier}: {targets.Count} language(s) from {source.Attributes?.Locale}");

                if (DryRun)
                {
                    foreach (var target in targets)
                    {
                        var current = achievement.ImageOf(target)?.Attributes?.FileName;
                        var wanted = achievement.ImageOf(source)?.Attributes?.FileName;

                        Console.WriteLine($"         {target.Attributes?.Locale,-10} {current ?? "(none)"} -> {wanted}");
                        copied.Add($"{achievement.VendorIdentifier} [{target.Attributes?.Locale}]");
                    }

                    continue;
                }

                var downloaded = await DownloadImageAsync(http, achievement.ImageOf(source)!);

                if (downloaded is null)
                {
                    Console.WriteLine($"      [SKIP] {achievement.VendorIdentifier}: the source image is not downloadable yet.");
                    skipped.Add($"{achievement.VendorIdentifier} (source not ready)");
                    continue;
                }

                foreach (var target in targets)
                {
                    var locale = target.Attributes?.Locale;

                    try
                    {
                        await CopyImageAsync(http, achievement, target, downloaded.Value.Bytes, downloaded.Value.FileName, Verbose);
                        copied.Add($"{achievement.VendorIdentifier} [{locale}]");
                    }
                    catch (Exception ex)
                    {
                        PrintApiError($"failed to copy the image to {achievement.VendorIdentifier} [{locale}]", ex);
                        failed.Add($"{achievement.VendorIdentifier} [{locale}]");
                    }
                }
            }

            PrintSummary(copied, new List<string>(), skipped, failed);

            if (copied.Count > 0 && !DryRun)
            {
                Console.WriteLine();
                Console.WriteLine("run 'locales submit --achievements' to release them.");
            }
        }
        catch (Exception ex)
        {
            PrintApiError("failed to sync achievement images", ex);
        }
    }
}
