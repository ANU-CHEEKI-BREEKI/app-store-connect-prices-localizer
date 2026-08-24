using System.Text.Json.Nodes;

/// <summary>
/// uploads a folder of localized screenshots back into the editable app store version, the other
/// half of 'export-screenshots'.
///
/// App Store Connect does not take an image in a single request. Every upload is a reservation:
/// creating an appScreenshot with the file name and size returns a list of upload operations,
/// each one a url plus the byte range of the file to PUT to it. Only after every chunk landed does
/// a final patch with 'uploaded: true' and the md5 of the source file hand the image over to apple,
/// which then validates it asynchronously
/// </summary>
public class Command_ImportScreenshots : AppScreenshotsCommandBase
{
    /// <summary>the two formats the App Store accepts</summary>
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

    /// <summary>apple throttles bursts, and a set has to be uploaded in order anyway</summary>
    private const int MaxParallelSets = 3;

    public override string Name => "import-screenshots";
    public override string Description => "Uploads a folder of localized screenshots back into the editable app store version, replacing what is there.";

    /// <summary>one local file, with the locale and display type recovered from its path</summary>
    private record LocalScreenshot(string Locale, string DisplayType, int Position, string Path)
    {
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    /// <summary>all local files of one locale + display type, in the order they should end up on the page</summary>
    private record UploadGroup(string Locale, string DisplayType, string LocalizationId, List<LocalScreenshot> Files)
    {
        public string? SetId { get; set; }
        public List<string> ExistingScreenshotIds { get; set; } = new();
    }

    public override void PrintHelp()
    {
        Console.WriteLine("import-screenshots [<path-to-folder>] [--version <x.y.z>] [--locales <a,b,c>] [--display-types <a,b>] [--keep] [-n] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("Reads both layouts 'export-screenshots' can write: '<folder>/<locale>/<display-type>/<position>_<name>.png', and the flat '<locale>__<display-type>__<position>_<name>.png'. The numeric prefix decides the order on the product page, files without one are ordered by name.");
        CommandLinesUtils.PrintDescription("By default a locale + display type that has local files is replaced completely: the screenshots already in that set are deleted and the local ones uploaded in their place. Sets you have no files for are left untouched. Pass '--keep' to append instead of replacing.");
        CommandLinesUtils.PrintDescription("Writes to the editable version only, since a released one does not accept changes. A locale the version has no localization for is skipped with a warning, run 'create-all-locales' first if you need it.");
        CommandLinesUtils.PrintDescription("Apple validates every image asynchronously after the upload. A wrong resolution for the display type is reported in App Store Connect, not by this command, so check the version page afterwards.");
        CommandLinesUtils.PrintDescription("Run with '-n' first, it prints every deletion and upload without touching anything.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "<path-to-folder>",
            $"The folder to upload from. Defaults to '{Command_ExportScreenshots.DefaultFolderName}' next to your config.json."
        );
        CommandLinesUtils.PrintOption(
            "--in <path>",
            "Same as the positional path argument."
        );
        CommandLinesUtils.PrintOption(
            "--version <x.y.z>",
            "Upload into this exact app store version instead of the editable one."
        );
        CommandLinesUtils.PrintOption(
            "--locales <a,b,c>",
            "Only upload these locales, comma separated, e.g. 'en-US,de-DE'. Default is every locale found in the folder."
        );
        CommandLinesUtils.PrintOption(
            "--display-types <a,b>",
            "Only upload these display types, comma separated, e.g. 'APP_IPHONE_67'. Default is every one found in the folder."
        );
        CommandLinesUtils.PrintOption(
            "--keep",
            "Add the local screenshots after the existing ones instead of replacing them. Nothing is deleted."
        );
        CommandLinesUtils.PrintOption(
            "-n",
            "Dry run: print everything that would be deleted and uploaded, without writing."
        );
        CommandLinesUtils.PrintOption(
            "-v",
            "Include additional verbose output"
        );
    }

    protected override async Task InternalExecuteAsync()
    {
        var verbose = Args.HasFlag("-v");
        var dryRun = Args.HasFlag("-n");
        var keep = Args.HasFlag("--keep");

        try
        {
            Console.WriteLine("   -> Importing app screenshots...");

            var inputFolder = ResolveInputFolder();
            if (!Directory.Exists(inputFolder))
            {
                Console.WriteLine($"[ERROR] folder '{Path.GetFullPath(inputFolder)}' does not exist.");
                Console.WriteLine("        run 'export-screenshots' first, or pass the folder explicitly.");
                return;
            }

            var files = CollectLocalFiles(inputFolder, verbose);
            if (files.Count == 0)
            {
                Console.WriteLine($"[ERROR] no screenshots found in '{Path.GetFullPath(inputFolder)}'.");
                Console.WriteLine("        expected '<locale>/<display-type>/<name>.png' or '<locale>__<display-type>__<name>.png'.");
                return;
            }

            // uploading is a write, so it has to land on a version App Store Connect still lets you edit
            var target = await ResolveTargetAsync(requireEditable: true, verbose);
            if (target is null)
                return;

            var groups = BuildGroups(files, target);
            if (groups.Count == 0)
                return;

            Console.WriteLine();
            Console.WriteLine($"   -> {files.Count} local screenshots in {groups.Count} sets across {groups.Select(g => g.Locale).Distinct().Count()} locales.");

            await ResolveSetsAsync(groups, dryRun, verbose);

            PrintPlan(groups, keep);

            if (dryRun)
            {
                Console.WriteLine();
                Console.WriteLine("dry run, nothing was written. Re-run without -n to apply.");
                return;
            }

            var (uploaded, deleted, failed) = await ApplyAsync(groups, keep, verbose);

            Console.WriteLine();
            Console.WriteLine("summary:");
            Console.WriteLine($"   version:  {target.VersionString}");
            Console.WriteLine($"   locales:  {groups.Select(g => g.Locale).Distinct().Count()}");
            Console.WriteLine($"   sets:     {groups.Count}");
            Console.WriteLine($"   uploaded: {uploaded}");

            if (deleted > 0)
                Console.WriteLine($"   deleted:  {deleted} (replaced)");

            if (failed > 0)
                Console.WriteLine($"   failed:   {failed}");

            Console.WriteLine();
            Console.WriteLine("   apple validates the images asynchronously, check the version page in App Store Connect.");
        }
        catch (Exception ex)
        {
            PrintApiError("failed to import app screenshots", ex);
        }
    }

    /// <summary>
    /// walks the folder and recovers the locale and display type of every image.
    /// the nested layout carries them as two directory levels, the flat one as file name parts
    /// </summary>
    private List<LocalScreenshot> CollectLocalFiles(string inputFolder, bool verbose)
    {
        var localeFilter = ParseList("--locales");
        var displayTypeFilter = ParseList("--display-types");

        var found = new List<LocalScreenshot>();

        foreach (var path in Directory.EnumerateFiles(inputFolder, "*", SearchOption.AllDirectories))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                continue;

            var relative = Path.GetRelativePath(inputFolder, path);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string locale;
            string displayType;
            string name;

            if (segments.Length >= 3)
            {
                // '<locale>/<display-type>/<name>.png'
                locale = segments[0];
                displayType = segments[1];
                name = segments[^1];
            }
            else
            {
                // '<locale>__<display-type>__<name>.png'
                var parts = segments[^1].Split(Command_ExportScreenshots.FlatSeparator);
                if (parts.Length < 3)
                {
                    if (verbose)
                        Console.WriteLine($"[WARN] cannot tell locale and display type from '{relative}', skipped.");

                    continue;
                }

                locale = parts[0];
                displayType = parts[1];
                name = string.Join(Command_ExportScreenshots.FlatSeparator, parts.Skip(2));
            }

            if (localeFilter.Count > 0 && !localeFilter.Any(f => string.Equals(f, locale, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (displayTypeFilter.Count > 0 && !displayTypeFilter.Any(f => string.Equals(f, displayType, StringComparison.OrdinalIgnoreCase)))
                continue;

            found.Add(new LocalScreenshot(locale, displayType, ParsePosition(name), path));
        }

        return found;
    }

    /// <summary>the '01_' prefix 'export-screenshots' writes, so the product page order survives a round trip</summary>
    private static int ParsePosition(string fileName)
    {
        var underscore = fileName.IndexOf('_');
        if (underscore > 0 && int.TryParse(fileName[..underscore], out var position))
            return position;

        return int.MaxValue;
    }

    /// <summary>groups the files per locale + display type and drops the locales the version does not have</summary>
    private List<UploadGroup> BuildGroups(List<LocalScreenshot> files, MetadataTarget target)
    {
        var groups = new List<UploadGroup>();
        var missingLocales = new List<string>();

        foreach (var localeGroup in files.GroupBy(f => f.Locale, StringComparer.OrdinalIgnoreCase))
        {
            var localization = target.FindVersionLocalization(localeGroup.Key);
            if (localization is null)
            {
                missingLocales.Add(localeGroup.Key);
                continue;
            }

            foreach (var typeGroup in localeGroup.GroupBy(f => f.DisplayType, StringComparer.OrdinalIgnoreCase))
            {
                if (ParseDisplayType(typeGroup.Key) is null)
                {
                    Console.WriteLine($"[WARN] '{typeGroup.Key}' is not a screenshot display type App Store Connect knows, skipped. See 'list-screenshots --all'.");
                    continue;
                }

                var ordered = typeGroup
                    .OrderBy(f => f.Position)
                    .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                groups.Add(new UploadGroup(localeGroup.Key, typeGroup.Key, (string?)localization["id"] ?? "", ordered));
            }
        }

        if (missingLocales.Count > 0)
        {
            Console.WriteLine($"[WARN] the version has no localization for: {string.Join(", ", missingLocales.OrderBy(l => l, StringComparer.OrdinalIgnoreCase))}");
            Console.WriteLine("       run 'create-all-locales' to add them, those folders were skipped.");
        }

        if (groups.Count == 0)
            Console.WriteLine("[ERROR] nothing left to upload.");

        return groups
            .OrderBy(g => g.Locale, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.DisplayType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// finds the screenshot set of every group, creating the ones that do not exist yet,
    /// and remembers what is already in them so a replace knows what to delete
    /// </summary>
    private async Task ResolveSetsAsync(List<UploadGroup> groups, bool dryRun, bool verbose)
    {
        // one request per locale, not per group: all display types of a locale come back together
        foreach (var localeGroups in groups.GroupBy(g => g.LocalizationId))
        {
            var sets = await Http.GetPagedAsync(
                $"/v1/appStoreVersionLocalizations/{localeGroups.Key}/appScreenshotSets?limit=50"
            );

            foreach (var group in localeGroups)
            {
                var existing = sets.Data.FirstOrDefault(
                    s => string.Equals(DisplayTypeName(s?["attributes"]?["screenshotDisplayType"]), group.DisplayType, StringComparison.OrdinalIgnoreCase)
                );

                if (existing is not null)
                {
                    group.SetId = (string?)existing["id"];

                    var screenshots = await Http.GetPagedAsync(
                        $"/v1/appScreenshotSets/{(string?)existing["id"]}/appScreenshots?limit=50"
                    );

                    group.ExistingScreenshotIds = screenshots.Data
                        .Select(s => (string?)s?["id"])
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id!)
                        .ToList();

                    continue;
                }

                if (dryRun)
                {
                    // no set to create yet, the plan just says it would be created
                    continue;
                }

                group.SetId = await CreateSetAsync(group);
            }
        }
    }

    private async Task<string?> CreateSetAsync(UploadGroup group)
    {
        var displayType = ParseDisplayType(group.DisplayType);
        if (displayType is null)
            return null;

        try
        {
            var body = AscHttp.Body(
                "appScreenshotSets",
                new JsonObject
                {
                    ["appStoreVersionLocalization"] = AscHttp.Link("appStoreVersionLocalizations", group.LocalizationId),
                },
                new JsonObject { ["screenshotDisplayType"] = displayType }
            );

            var response = await Http.PostAsync("/v1/appScreenshotSets", body);

            Console.WriteLine($"   -> created screenshot set {group.Locale} / {group.DisplayType}");

            return (string?)response["data"]?["id"];
        }
        catch (Exception ex)
        {
            PrintApiError($"failed to create screenshot set {group.Locale} / {group.DisplayType}", ex);
            return null;
        }
    }

    private static void PrintPlan(List<UploadGroup> groups, bool keep)
    {
        Console.WriteLine();
        Console.WriteLine(keep ? "plan (append):" : "plan (replace):");
        Console.WriteLine();

        foreach (var group in groups)
        {
            var existing = group.ExistingScreenshotIds.Count;

            var action = group.SetId is null
                ? "new set"
                : keep || existing == 0
                    ? $"{existing} kept"
                    : $"{existing} deleted";

            Console.WriteLine($"   {group.Locale,-12} {group.DisplayType,-32} {group.Files.Count,3} uploaded, {action}");
        }
    }

    private async Task<(int uploaded, int deleted, int failed)> ApplyAsync(List<UploadGroup> groups, bool keep, bool verbose)
    {
        using var throttle = new SemaphoreSlim(MaxParallelSets);

        var uploaded = 0;
        var deleted = 0;
        var failed = 0;

        var tasks = groups.Select(async group =>
        {
            await throttle.WaitAsync();
            try
            {
                if (group.SetId is null)
                {
                    Console.WriteLine($"[WARN] {group.Locale} / {group.DisplayType} has no screenshot set, skipped.");
                    Interlocked.Add(ref failed, group.Files.Count);
                    return;
                }

                // deleting first keeps the order clean: the uploads then are the whole set, in file order
                if (!keep)
                {
                    foreach (var id in group.ExistingScreenshotIds)
                    {
                        try
                        {
                            await Http.DeleteAsync($"/v1/appScreenshots/{id}");
                            Interlocked.Increment(ref deleted);
                        }
                        catch (Exception ex)
                        {
                            PrintApiError($"failed to delete screenshot {id} of {group.Locale} / {group.DisplayType}", ex);
                        }
                    }
                }

                // sequential on purpose, App Store Connect keeps the screenshots in creation order
                foreach (var file in group.Files)
                {
                    try
                    {
                        await UploadAsync(group, file, verbose);
                        Interlocked.Increment(ref uploaded);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        PrintApiError($"failed to upload {group.Locale} / {group.DisplayType} / {file.FileName}", ex);
                    }
                }

                Console.WriteLine($"   -> {group.Locale,-12} {group.DisplayType,-32} {group.Files.Count} uploaded");
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);

        return (uploaded, deleted, failed);
    }

    /// <summary>reserve the asset, PUT every chunk apple asks for, then commit it with the checksum</summary>
    private async Task UploadAsync(UploadGroup group, LocalScreenshot file, bool verbose)
    {
        var setId = group.SetId ?? throw new InvalidOperationException("the screenshot set was not resolved.");

        var bytes = await File.ReadAllBytesAsync(file.Path);

        var createBody = AscHttp.Body(
            "appScreenshots",
            new JsonObject
            {
                ["appScreenshotSet"] = AscHttp.Link("appScreenshotSets", setId),
            },
            new JsonObject
            {
                ["fileSize"] = bytes.Length,
                ["fileName"] = file.FileName,
            }
        );

        var created = await Http.PostAsync("/v1/appScreenshots", createBody);

        var screenshotId = (string?)created["data"]?["id"];
        if (string.IsNullOrEmpty(screenshotId))
            throw new InvalidOperationException("App Store Connect did not return a screenshot id.");

        var chunks = await AscUpload.SendAllChunksAsync(created["data"]?["attributes"]?["uploadOperations"], bytes);

        var checksum = AscUpload.Checksum(bytes);

        await Http.PatchAsync(
            $"/v1/appScreenshots/{screenshotId}",
            AscHttp.BodyWithAttributes(
                "appScreenshots",
                screenshotId,
                new JsonObject
                {
                    ["sourceFileChecksum"] = checksum,
                    ["uploaded"] = true,
                }
            )
        );

        if (verbose)
            Console.WriteLine($"      {group.Locale,-12} {group.DisplayType,-30} {file.FileName} {bytes.Length / 1024} KB in {chunks} chunks");
    }

    /// <summary>the inverse of <see cref="AppScreenshotsCommandBase.DisplayTypeName"/></summary>
    private static string? ParseDisplayType(string name)
    {
        var upper = name.ToUpperInvariant();
        return KnownDisplayTypes.FirstOrDefault(t => t == upper);
    }

    /// <summary>an explicit argument wins, then the export folder next to config.json, then the desktop</summary>
    private string ResolveInputFolder()
    {
        var explicitPath = Args.TryGetOption("--in", GetPositionalPath());
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        if (!string.IsNullOrWhiteSpace(Config.ConfigDirectory) && Directory.Exists(Config.ConfigDirectory))
            return Path.Combine(Config.ConfigDirectory, Command_ExportScreenshots.DefaultFolderName);

        // ask the system where the desktop is: on Windows it can live under OneDrive or carry a localized name
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        return Path.Combine(desktop, Command_ExportScreenshots.DefaultFolderName);
    }
}
