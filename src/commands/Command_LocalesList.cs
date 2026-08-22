using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Model;

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

        var appsApi = new AppsApi(Service);

        var products = await FetchAllPagesAsync<InAppPurchasesV2Response, InAppPurchaseV2>(
            appsApi.AsynchronousClient,
            appsApi.Configuration,
            () => appsApi.AppsInAppPurchasesV2GetToManyRelatedAsync(Config.AppId, limit: 200),
            r => r.Data,
            r => r.Links?.Next,
            verbose
        );

        products = FilterByIap(products, p => p.Attributes?.ProductId);

        var iapApi = new InAppPurchasesApi(Service);
        var locales = new List<string>();

        foreach (var product in products)
        {
            try
            {
                var response = await iapApi.InAppPurchasesV2InAppPurchaseLocalizationsGetToManyRelatedAsync(product.Id, limit: 200);

                foreach (var localization in response?.Data ?? new())
                {
                    var locale = localization.Attributes?.Locale;
                    if (!string.IsNullOrWhiteSpace(locale) && !locales.Contains(locale, StringComparer.OrdinalIgnoreCase))
                        locales.Add(locale);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: could not read the languages of {product.Attributes?.ProductId}: {ex.Message}");
            }
        }

        return new Section("in-app purchases", locales, $"{products.Count} product(s)");
    }

    private async Task<Section> AchievementLocalesAsync(bool verbose)
    {
        // the achievement plumbing lives on GameCenterCommandBase, and this command already
        // inherits the metadata one. Borrowing an instance is cheaper than a third base class
        var reader = new AchievementLocaleReader();
        reader.Initialize(Service, Config, Args);

        return await reader.ReadAsync(verbose);
    }

    /// <summary>the achievement half of the listing, as its own command so it gets the Game Center plumbing</summary>
    private class AchievementLocaleReader : GameCenterCommandBase
    {
        protected override TextField[] Fields => Array.Empty<TextField>();

        public override string Name => "locales list";
        public override string Description => "";
        public override void PrintHelp() { }

        protected override Task InternalExecuteAsync() => Task.CompletedTask;

        public async Task<Section> ReadAsync(bool verbose)
        {
            var achievements = await GetAchievementsAsync(verbose);

            if (achievements is null)
                return new Section("game center achievements", new List<string>(), "no Game Center");

            var locales = achievements
                .SelectMany(a => a.Locales)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var withoutImage = achievements.Sum(a => a.Localizations.Count(l => a.ImageOf(l) is null));

            var note = withoutImage == 0
                ? $"{achievements.Count} achievement(s)"
                : $"{achievements.Count} achievement(s), {withoutImage} language(s) with no image";

            return new Section("game center achievements", locales, note);
        }
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
