/// <summary>
/// prints what screenshots the app store version actually has, per locale and per device size.
///
/// this is the lookup command for the '--locales' and '--display-types' filters of
/// 'export-screenshots': both of them expect apple's own codes, and there is no way to guess
/// which ones a given app uses. '--all' additionally prints every code the App Store supports
/// </summary>
public class Command_ListScreenshots : AppScreenshotsCommandBase
{
    public override string Name => "list-screenshots";
    public override string Description => "Lists the locales and screenshot display types (device sizes) the app store version has, with how many screenshots each one holds.";

    public override void PrintHelp()
    {
        Console.WriteLine("list-screenshots [--version <x.y.z>] [--all] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("The codes printed here are exactly what 'export-screenshots --locales' and 'export-screenshots --display-types' expect.");
        CommandLinesUtils.PrintDescription("A locale with 0 screenshots exists as a localization but has no images yet. A display type appears only once at least one locale uses it.");
        CommandLinesUtils.PrintDescription("'--all' also prints every locale the App Store supports and every display type the API knows, including the ones this app does not use.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--version <x.y.z>",
            "List this exact app store version instead of the editable one."
        );
        CommandLinesUtils.PrintOption(
            "--all",
            "Also print every App Store locale and every screenshot display type that exists, not just the ones this app uses."
        );
        CommandLinesUtils.PrintOption(
            "--locales <a,b,c>",
            "Only look at these locales, comma separated. Default is every locale the version has."
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
            Console.WriteLine("   -> Listing app screenshots...");

            // listing is read only, so a frozen live version is a perfectly valid source
            var target = await ResolveTargetAsync(requireEditable: false, verbose);
            if (target is null)
                return;

            var locales = FilterLocales(target, announce: false);
            if (locales.Count == 0)
                return;

            var entries = await ScanAsync(target, locales, ParseList("--display-types"), verbose);

            PrintLocales(locales, entries);
            PrintDisplayTypes(entries);

            if (Args.HasFlag("--all"))
            {
                PrintAllLocales(locales);
                PrintAllDisplayTypes(entries);
            }

            Console.WriteLine();
            Console.WriteLine("summary:");
            Console.WriteLine($"   version:       {target.VersionString}");
            Console.WriteLine($"   locales:       {locales.Count}");
            Console.WriteLine($"   display types: {entries.Select(e => e.DisplayType).Distinct().Count()}");
            Console.WriteLine($"   screenshots:   {entries.Count}");

            if (!Args.HasFlag("--all"))
            {
                Console.WriteLine();
                Console.WriteLine("   pass --all to also see the locales and display types this app does not use yet.");
            }
        }
        catch (Exception ex)
        {
            PrintApiError("failed to list app screenshots", ex);
        }
    }

    private static void PrintLocales(List<string> locales, List<ScreenshotEntry> entries)
    {
        Console.WriteLine();
        Console.WriteLine($"locales of this version ({locales.Count}):");
        Console.WriteLine();
        Console.WriteLine($"   {"locale",-12} {"shots",5}  display types");

        foreach (var locale in locales)
        {
            var forLocale = entries.Where(e => e.Locale == locale).ToList();

            var displayTypes = forLocale
                .Select(e => e.DisplayType)
                .Distinct()
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var types = displayTypes.Count > 0 ? string.Join(", ", displayTypes) : "-";

            Console.WriteLine($"   {locale,-12} {forLocale.Count,5}  {types}");
        }
    }

    private static void PrintDisplayTypes(List<ScreenshotEntry> entries)
    {
        var used = entries
            .GroupBy(e => e.DisplayType)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"display types in use ({used.Count}):");
        Console.WriteLine();
        Console.WriteLine($"   {"display type",-32} {"shots",5} {"locales",8}  resolution");

        foreach (var group in used)
        {
            var localeCount = group.Select(e => e.Locale).Distinct().Count();

            // every image of a set has the same size, the first one that finished uploading answers for all
            var asset = group.Select(e => e.Asset).FirstOrDefault(a => a is not null);
            var resolution = asset is null ? "-" : $"{asset.Width}x{asset.Height}";

            Console.WriteLine($"   {group.Key,-32} {group.Count(),5} {localeCount,8}  {resolution}");
        }
    }

    /// <summary>every locale a product page can be localized into, so missing ones are visible too</summary>
    private static void PrintAllLocales(List<string> present)
    {
        Console.WriteLine();
        Console.WriteLine($"all App Store locales ({AppStoreLocales.Supported.Length}), '*' marks the ones this version has:");
        Console.WriteLine();

        var line = new List<string>();
        foreach (var locale in AppStoreLocales.Supported)
        {
            var mark = present.Any(p => string.Equals(p, locale, StringComparison.OrdinalIgnoreCase)) ? "*" : " ";
            line.Add($"{mark}{locale,-9}");

            if (line.Count == 5)
            {
                Console.WriteLine($"   {string.Join(" ", line)}");
                line.Clear();
            }
        }

        if (line.Count > 0)
            Console.WriteLine($"   {string.Join(" ", line)}");
    }

    /// <summary>every device size the api knows, including the iMessage app ones most apps never use</summary>
    private static void PrintAllDisplayTypes(List<ScreenshotEntry> entries)
    {
        var used = entries.Select(e => e.DisplayType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var all = AllDisplayTypes();

        Console.WriteLine();
        Console.WriteLine($"all screenshot display types ({all.Count}), '*' marks the ones this version uses:");
        Console.WriteLine();

        foreach (var displayType in all)
        {
            var mark = used.Any(u => string.Equals(u, displayType, StringComparison.OrdinalIgnoreCase)) ? "*" : " ";
            Console.WriteLine($"   {mark}{displayType}");
        }
    }
}
