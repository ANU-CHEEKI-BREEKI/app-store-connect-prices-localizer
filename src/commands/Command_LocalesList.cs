using System.Text.Json.Nodes;

/// <summary>
/// Shows which languages exist where. App Store Connect keeps three independent language lists for
/// one app - the product page, the In-App Purchases and the Game Center achievements - and nothing
/// keeps them in sync. A language you added to the store page is not a language your purchase names
/// or your achievements have, and there is no screen that says so.
/// </summary>
public class Command_LocalesList : AppMetadataCommandBase
{
    public override string Name => "locales list";

    public override string Description
        => "Shows which languages the app store page, the In-App Purchases and the Game Center achievements have, and what is missing from where.";

    public override void PrintHelp()
    {
        Console.WriteLine("locales [list] [--version <x.y.z>] [--iap <id[,id...]>] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("Read only, it writes nothing.");
        CommandLinesUtils.PrintDescription("There is no 'locales add' on purpose: for all three of them a language exists because text for it exists, so adding a language and writing its text are the same thing. Add a column to the csv and import it.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption("--version <x.y.z>", "Read the store page languages from this exact app store version instead of the editable one.");
        CommandLinesUtils.PrintOption(CommandLinesUtils.IapOptionName, CommandLinesUtils.IapOptionDescription);
        CommandLinesUtils.PrintOption("-v", "Include additional verbose output");
    }

    protected override async Task InternalExecuteAsync()
    {
        var verbose = Args.HasFlag("-v");

        try
        {
            if (string.IsNullOrWhiteSpace(Config.AppId))
            {
                Console.WriteLine("[ERROR] no app id. specify it in config.json or with --app-id");
                return;
            }

            var page = await StorePageLocalesAsync(verbose);
            var iaps = await IapLocalesAsync(verbose);
            var achievements = await AchievementLocalesAsync(verbose);

            Console.WriteLine();
            Print("app store page", page.Locales, page.Note);
            Print("in-app purchases", iaps.Locales, iaps.Note);
            Print("game center achievements", achievements.Locales, achievements.Note);

            PrintGaps(page, iaps, achievements);
        }
        catch (Exception ex)
        {
            PrintApiError("failed to list the languages", ex);
        }
    }

    private record Section(string Title, List<string> Locales, string Note);

    private async Task<Section> StorePageLocalesAsync(bool verbose)
    {
        var target = await ResolveTargetAsync(requireEditable: false, verbose);

        if (target is null)
            return new Section("app store page", new List<string>(), "no app store version");

        return new Section("app store page", target.Locales, $"version {target.VersionString}");
    }

    private async Task<Section> IapLocalesAsync(bool verbose)
    {
        Console.WriteLine("   -> Receiving In-App Purchases...");

        var page = await Http.GetPagedAsync($"/v1/apps/{Config.AppId}/inAppPurchasesV2?limit=200");

        var products = FilterByIap(page.Data, p => (string?)p?["attributes"]?["productId"]);

        var locales = new List<string>();

        foreach (var product in products)
        {
            var productId = (string?)product?["attributes"]?["productId"];

            try
            {
                var response = await Http.GetAsync($"/v2/inAppPurchases/{(string?)product?["id"]}/inAppPurchaseLocalizations?limit=200");
                var localizations = response["data"] as JsonArray ?? new JsonArray();

                foreach (var localization in localizations)
                {
                    var locale = (string?)localization?["attributes"]?["locale"];
                    if (!string.IsNullOrWhiteSpace(locale) && !locales.Contains(locale, StringComparer.OrdinalIgnoreCase))
                        locales.Add(locale);
                }

                // the product state says nothing about a language added to an approved product:
                // that one carries its own state, and this is the only place it shows up
                if (verbose)
                {
                    var states = localizations
                        .GroupBy(l => StateName(l?["attributes"]?["state"]) ?? "?")
                        .Select(g => $"{g.Count()} {g.Key}");

                    Console.WriteLine($"      {productId,-48} {StateName(product?["attributes"]?["state"])}: {string.Join(", ", states)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: could not read the languages of {productId}: {ex.Message}");
            }
        }

        return new Section("in-app purchases", locales, $"{products.Count} product(s)");
    }

    /// <summary>the state the way the generated client printed it: the enum names had no underscores</summary>
    private static string? StateName(JsonNode? state)
        => ((string?)state)?.Replace("_", "");

    private async Task<Section> AchievementLocalesAsync(bool verbose)
    {
        Console.WriteLine("   -> Receiving Game Center details...");

        JsonNode? detail;

        try
        {
            var response = await Http.GetAsync($"/v1/apps/{Config.AppId}/gameCenterDetail");
            detail = response["data"];
        }
        catch (AscApiException ex) when (ex.StatusCode == 404)
        {
            detail = null;
        }

        if (detail is null)
        {
            Console.WriteLine("[ERROR] this app has no Game Center configuration.");
            Console.WriteLine("        turn Game Center on in App Store Connect first.");
            return new Section("game center achievements", new List<string>(), "no Game Center");
        }

        // the fork: an app in a game center group shares the group's achievements
        // and its own detail has none
        var groupId = (string?)detail["relationships"]?["gameCenterGroup"]?["data"]?["id"];

        var achievementsPage = string.IsNullOrWhiteSpace(groupId)
            ? await Http.GetPagedAsync($"/v1/gameCenterDetails/{(string?)detail["id"]}/gameCenterAchievements?limit=200")
            : await Http.GetPagedAsync($"/v1/gameCenterGroups/{groupId}/gameCenterAchievements?limit=200");

        if (!string.IsNullOrWhiteSpace(groupId))
            Console.WriteLine($"   -> this app is in a Game Center group, its achievements are shared.");

        var achievements = achievementsPage.Data
            .Where(a => !string.IsNullOrWhiteSpace((string?)a?["attributes"]?["vendorIdentifier"]))
            .OrderBy(a => (string?)a?["attributes"]?["referenceName"], StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"   -> {achievements.Count} achievement(s), receiving their languages...");

        var locales = new List<string>();
        var withoutImage = 0;

        foreach (var achievement in achievements)
        {
            var vendorId = (string?)achievement?["attributes"]?["vendorIdentifier"] ?? "";

            try
            {
                // the image rides along in the same request: a localization without one can never
                // go live, so it is never just extra data
                var response = await Http.GetAsync(
                    $"/v1/gameCenterAchievements/{(string?)achievement?["id"]}/localizations?limit=200&include=gameCenterAchievementImage"
                );

                var localizations = response["data"] as JsonArray ?? new JsonArray();

                // 'included' is a flat list, so an image is tied back to its language through the
                // relationship the localization carries
                var imageIds = (response["included"] as JsonArray ?? new JsonArray())
                    .Where(i => (string?)i?["type"] == "gameCenterAchievementImages")
                    .Select(i => (string?)i?["id"])
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .ToHashSet(StringComparer.Ordinal);

                var images = 0;

                foreach (var localization in localizations)
                {
                    var locale = (string?)localization?["attributes"]?["locale"];
                    if (!string.IsNullOrWhiteSpace(locale) && !locales.Contains(locale, StringComparer.OrdinalIgnoreCase))
                        locales.Add(locale);

                    var imageId = (string?)localization?["relationships"]?["gameCenterAchievementImage"]?["data"]?["id"];
                    if (!string.IsNullOrWhiteSpace(imageId) && imageIds.Contains(imageId))
                        images++;
                    else
                        withoutImage++;
                }

                if (verbose)
                    Console.WriteLine($"      {vendorId,-32} {localizations.Count} language(s), {images} image(s)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: could not read the languages of {vendorId}: {ex.Message}");
            }
        }

        var note = withoutImage == 0
            ? $"{achievements.Count} achievement(s)"
            : $"{achievements.Count} achievement(s), {withoutImage} language(s) with no image";

        return new Section("game center achievements", locales, note);
    }

    private static void Print(string title, List<string> locales, string note)
    {
        Console.WriteLine($"{title} ({locales.Count})   {note}:");

        if (locales.Count == 0)
        {
            Console.WriteLine("        none");
            Console.WriteLine();
            return;
        }

        foreach (var locale in locales.OrderBy(l => l, StringComparer.Ordinal))
            Console.WriteLine($"        {locale}");

        Console.WriteLine();
    }

    /// <summary>
    /// The point of the whole listing: a language that is in one place and not the others. That is
    /// the work nobody sees, because no screen in App Store Connect puts the three lists together.
    /// </summary>
    private static void PrintGaps(Section page, Section iaps, Section achievements)
    {
        var sections = new[] { page, iaps, achievements };

        var all = sections
            .SelectMany(s => s.Locales)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

        var gaps = new List<string>();

        foreach (var locale in all)
        {
            var missing = sections
                .Where(s => !s.Locales.Contains(locale, StringComparer.OrdinalIgnoreCase))
                .Select(s => s.Title)
                .ToList();

            if (missing.Count > 0)
                gaps.Add($"        {locale,-10} missing from: {string.Join(", ", missing)}");
        }

        if (gaps.Count == 0)
        {
            Console.WriteLine("every language is everywhere.");
            return;
        }

        Console.WriteLine($"not everywhere ({gaps.Count}):");
        foreach (var gap in gaps)
            Console.WriteLine(gap);
    }
}
