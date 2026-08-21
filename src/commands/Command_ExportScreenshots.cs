using AppStoreConnect.Net.Model;

/// <summary>
/// downloads every product page screenshot of the app into per locale folders, so they can be
/// localized offline and uploaded back afterwards.
///
/// App Store Connect never hands back the file that was uploaded. What the api exposes instead is an
/// 'imageAsset' template url pointing at apple's image service:
///     https://is1-ssl.mzstatic.com/image/thumb/.../{w}x{h}bb.{f}
/// asking that url for the asset's own width/height means no rescaling happens, and 'png' is a
/// lossless container, so the download is pixel identical to what was uploaded. it is still a
/// re encoded file though: the exact bytes, the file size and any embedded metadata are gone
/// </summary>
public class Command_ExportScreenshots : AppScreenshotsCommandBase
{
    public const string DefaultFolderName = "Screenshots";
    public const string ManifestFileName = "screenshots.csv";

    /// <summary>
    /// what separates the parts of a flat layout file name: 'de-DE__APP_IPHONE_67__01_home.png'.
    /// a double underscore is unambiguous, since a display type only ever uses single ones
    /// </summary>
    public const string FlatSeparator = "__";

    /// <summary>an app with many locales easily reaches several hundred images, but apple throttles bursts</summary>
    private const int MaxParallelDownloads = 6;

    public override string Name => "export-screenshots";
    public override string Description => "Downloads all app store product page screenshots into one folder per locale, ready to be localized and uploaded back.";

    /// <summary>a screenshot resolved to a url and a destination, so the download loop needs no api calls</summary>
    private record PlannedDownload(ScreenshotEntry Entry, string Url, string Path)
    {
        public int Width => Entry.Asset?.Width ?? 0;
        public int Height => Entry.Asset?.Height ?? 0;
    }

    public override void PrintHelp()
    {
        Console.WriteLine("export-screenshots [<path-to-output-folder>] [--version <x.y.z>] [--locales <a,b,c>] [--display-types <a,b>] [--layout folders|flat] [--format png|jpg] [--overwrite] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("By default every locale gets its own folder: '<output>/<locale>/<display-type>/<position>_<original-name>', for example 'Screenshots/de-DE/APP_IPHONE_67/01_home.png'. The numeric prefix preserves the order the screenshots have in App Store Connect.");
        CommandLinesUtils.PrintDescription($"'--layout flat' puts everything in one folder instead, encoding the same information in the file name: 'de-DE{FlatSeparator}APP_IPHONE_67{FlatSeparator}01_home.png'. Handy when a batch image editor can not walk subfolders. 'import-screenshots' reads both layouts.");
        CommandLinesUtils.PrintDescription($"A '{ManifestFileName}' table is written next to the folders. It maps every file back to its locale, display type, position and screenshot id, which is what an upload back to App Store Connect needs.");
        CommandLinesUtils.PrintDescription("Apple does not return the uploaded file itself, only a re-encoded copy from its image service. Requested at the asset's own resolution as png the pixels are identical, but the file bytes and any embedded metadata are not.");
        CommandLinesUtils.PrintDescription("Run 'list-screenshots' first to see which locales and display types this app actually has.");
        CommandLinesUtils.PrintDescription($"If no path is given, the folders are written next to your config.json as '{DefaultFolderName}', or to the Desktop when there is no config directory.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "<path-to-output-folder>",
            $"Where to write the screenshots. Created if missing. Defaults to '{DefaultFolderName}' next to your config.json."
        );
        CommandLinesUtils.PrintOption(
            "--out <path>",
            "Same as the positional path argument."
        );
        CommandLinesUtils.PrintOption(
            "--version <x.y.z>",
            "Export from this exact app store version instead of the editable one."
        );
        CommandLinesUtils.PrintOption(
            "--locales <a,b,c>",
            "Only export these locales, comma separated, e.g. 'en-US,de-DE'. Default is every locale the version has. See 'list-screenshots'."
        );
        CommandLinesUtils.PrintOption(
            "--display-types <a,b>",
            "Only export these screenshot display types, comma separated, e.g. 'APP_IPHONE_67,APP_IPAD_PRO_3GEN_129'. Default is all of them. See 'list-screenshots'."
        );
        CommandLinesUtils.PrintOption(
            "--layout <folders|flat>",
            "'folders' (default) gives every locale its own folder. 'flat' writes all images into the output folder, with the locale and display type encoded in the file name."
        );
        CommandLinesUtils.PrintOption(
            "--format <png|jpg>",
            "Image format to request. Default is 'png', which is lossless. 'jpg' produces smaller but re-compressed files."
        );
        CommandLinesUtils.PrintOption(
            "--overwrite",
            "Re-download files that already exist on disk. By default they are skipped, so an interrupted export can just be run again."
        );
        CommandLinesUtils.PrintOption(
            "-v",
            "Include additional verbose output"
        );
    }

    protected override async Task InternalExecuteAsync()
    {
        var verbose = Args.HasFlag("-v");

        try
        {
            Console.WriteLine("   -> Exporting app screenshots...");

            var format = Args.TryGetOption("--format", "png").TrimStart('.').ToLowerInvariant();
            if (format != "png" && format != "jpg" && format != "jpeg")
            {
                Console.WriteLine($"[ERROR] unsupported --format '{format}'. Use 'png' (lossless) or 'jpg'.");
                return;
            }

            var layout = Args.TryGetOption("--layout", "folders").ToLowerInvariant();
            if (layout != "folders" && layout != "flat")
            {
                Console.WriteLine($"[ERROR] unsupported --layout '{layout}'. Use 'folders' or 'flat'.");
                return;
            }

            // downloading is read only, so a frozen live version is a perfectly valid source
            var target = await ResolveTargetAsync(requireEditable: false, verbose);
            if (target is null)
                return;

            var locales = FilterLocales(target);
            if (locales.Count == 0)
                return;

            var entries = await ScanAsync(target, locales, ParseList("--display-types"), verbose);
            var outputFolder = ResolveOutputFolder();
            var planned = Plan(entries, format, outputFolder, layout == "flat");

            if (planned.Count == 0)
            {
                Console.WriteLine("[ERROR] no downloadable screenshots found for the selected locales.");
                Console.WriteLine("        run 'list-screenshots' to see what this version has.");
                return;
            }

            Console.WriteLine($"   -> {planned.Count} screenshots to fetch.");

            var (downloaded, skipped, failed) = await DownloadAllAsync(planned, Args.HasFlag("--overwrite"), verbose);

            await WriteManifestAsync(outputFolder, planned);

            Console.WriteLine();
            Console.WriteLine("summary:");
            Console.WriteLine($"   version:     {target.VersionString}");
            Console.WriteLine($"   locales:     {locales.Count}");
            Console.WriteLine($"   screenshots: {planned.Count}");
            Console.WriteLine($"   downloaded:  {downloaded}");

            if (skipped > 0)
                Console.WriteLine($"   skipped:     {skipped} (already on disk, pass --overwrite to refetch)");

            if (failed > 0)
                Console.WriteLine($"   failed:      {failed}");

            Console.WriteLine($"   written:     {Path.GetFullPath(outputFolder)}");
            Console.WriteLine($"   manifest:    {Path.GetFullPath(Path.Combine(outputFolder, ManifestFileName))}");
        }
        catch (Exception ex)
        {
            PrintApiError("failed to export app screenshots", ex);
        }
    }

    /// <summary>turns the scanned screenshots into urls and destination paths</summary>
    private static List<PlannedDownload> Plan(List<ScreenshotEntry> entries, string format, string outputFolder, bool flat)
    {
        var planned = new List<PlannedDownload>();

        foreach (var entry in entries)
        {
            if (!entry.IsDownloadable)
            {
                Console.WriteLine($"[WARN] {entry.Locale} / {entry.DisplayType} #{entry.Position:00} has no downloadable image (state {entry.State}), skipped.");
                continue;
            }

            var fileName = $"{entry.Position:00}_{BuildFileName(entry.SourceFileName, format)}";

            var path = flat
                ? Path.Combine(outputFolder, SanitizeSegment($"{entry.Locale}{FlatSeparator}{entry.DisplayType}{FlatSeparator}{fileName}"))
                : Path.Combine(outputFolder, SanitizeSegment(entry.Locale), SanitizeSegment(entry.DisplayType), fileName);

            planned.Add(new PlannedDownload(entry, BuildImageUrl(entry.Asset!, format), path));
        }

        return planned;
    }

    private static async Task<(int downloaded, int skipped, int failed)> DownloadAllAsync(List<PlannedDownload> planned, bool overwrite, bool verbose)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var throttle = new SemaphoreSlim(MaxParallelDownloads);

        var downloaded = 0;
        var skipped = 0;
        var failed = 0;

        var tasks = planned.Select(async item =>
        {
            await throttle.WaitAsync();
            try
            {
                if (!overwrite && File.Exists(item.Path))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);

                var bytes = await http.GetByteArrayAsync(item.Url);
                await File.WriteAllBytesAsync(item.Path, bytes);

                Interlocked.Increment(ref downloaded);

                if (verbose)
                    Console.WriteLine($"      {item.Entry.Locale,-12} {item.Entry.DisplayType,-30} #{item.Entry.Position:00} {item.Width}x{item.Height} {bytes.Length / 1024} KB");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Console.WriteLine($"[WARN] failed to download {item.Entry.Locale} / {item.Entry.DisplayType} #{item.Entry.Position:00}: {ex.Message}");
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);

        return (downloaded, skipped, failed);
    }

    /// <summary>
    /// the table that maps the files back to App Store Connect. an upload back needs the locale, the
    /// display type and the position, and the screenshot id makes it possible to replace an existing image
    /// </summary>
    private static async Task WriteManifestAsync(string outputFolder, List<PlannedDownload> planned)
    {
        var headers = new List<string> { "Locale", "DisplayType", "Position", "File", "SourceFileName", "ScreenshotId", "ScreenshotSetId", "Width", "Height" };

        var rows = planned
            .OrderBy(p => p.Entry.Locale, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Entry.DisplayType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Entry.Position)
            .Select(p => new List<string>
            {
                p.Entry.Locale,
                p.Entry.DisplayType,
                p.Entry.Position.ToString(),
                Path.GetRelativePath(outputFolder, p.Path),
                p.Entry.SourceFileName,
                p.Entry.ScreenshotId,
                p.Entry.SetId,
                p.Width.ToString(),
                p.Height.ToString(),
            })
            .ToList();

        Directory.CreateDirectory(outputFolder);
        await CommandLinesUtils.SaveCsv(Path.Combine(outputFolder, ManifestFileName), headers, rows);
    }

    /// <summary>
    /// apple's image service builds the file on request, the template carries the placeholders.
    /// asking for the asset's own width and height means it is served without any rescaling
    /// </summary>
    private static string BuildImageUrl(ImageAsset asset, string format)
        => asset.TemplateUrl
            .Replace("{w}", asset.Width.ToString())
            .Replace("{h}", asset.Height.ToString())
            .Replace("{f}", format);

    private static string BuildFileName(string sourceFileName, string format)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFileName);
        if (string.IsNullOrWhiteSpace(name))
            name = "screenshot";

        return SanitizeSegment($"{name}.{format}");
    }

    /// <summary>an explicit argument wins, then a folder next to config.json, then the desktop</summary>
    private string ResolveOutputFolder()
    {
        var explicitPath = Args.TryGetOption("--out", GetPositionalPath());
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        if (!string.IsNullOrWhiteSpace(Config.ConfigDirectory) && Directory.Exists(Config.ConfigDirectory))
            return Path.Combine(Config.ConfigDirectory, DefaultFolderName);

        // ask the system where the desktop is: on Windows it can live under OneDrive or carry a localized name
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        return Path.Combine(desktop, DefaultFolderName);
    }
}
