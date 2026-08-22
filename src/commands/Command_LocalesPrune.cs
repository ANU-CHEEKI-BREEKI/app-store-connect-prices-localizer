using AppStoreConnect.Net.Api;

/// <summary>
/// Deletes localizations, keeping only the languages you name.
///
/// The one command here that destroys work rather than making it. It exists because adding a
/// language is one flag and forty of them is one run, so deciding later that ten was the right
/// number should not mean clicking through the console for a week.
///
/// Nothing is deleted without --confirm. A dry run is the default on purpose: this cannot be undone
/// by running it again, only by re-importing a csv you still have.
/// </summary>
public class Command_LocalesPrune : GameCenterCommandBase
{
    protected override TextField[] Fields => Array.Empty<TextField>();

    public override string Name => "locales prune";

    public override string Description
        => "Deletes In-App Purchase and Game Center achievement localizations, keeping only the languages you name. Destructive, and needs --confirm.";

    public override void PrintHelp()
    {
        Console.WriteLine("locales prune --keep <code[,code...]> [--iaps] [--achievements] [--iap <id[,id...]>] [--confirm] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("Every language that is not in --keep is deleted, from every product and every achievement. Without any of --iaps / --achievements both are done.");
        CommandLinesUtils.PrintDescription("A dry run is the default: nothing is deleted until --confirm is passed. Read the plan first, because this is the one command here that a second run cannot undo - only re-importing a csv you still have can.");
        CommandLinesUtils.PrintDescription("App Store Connect refuses to delete the last localization of a product, so --keep must name a language that actually exists. The command checks that before it deletes anything.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption("--keep <code[,code...]>", "The languages to keep. Everything else is deleted. Required.");
        CommandLinesUtils.PrintOption("--iaps", "Prune the In-App Purchases only.");
        CommandLinesUtils.PrintOption("--achievements", "Prune the Game Center achievements only.");
        CommandLinesUtils.PrintOption(CommandLinesUtils.IapOptionName, CommandLinesUtils.IapOptionDescription);
        CommandLinesUtils.PrintOption("--confirm", "Actually delete. Without it the command only prints what it would delete.");
        CommandLinesUtils.PrintOption("-v", "Include additional verbose output");
    }

    /// <summary>one localization queued for deletion</summary>
    private record Doomed(string Owner, string Locale, string Id);

    protected override async Task InternalExecuteAsync()
    {
        var keep = ParseList("--keep");
        var confirm = Args.HasFlag("--confirm");

        var wantIaps = Args.HasFlag("--iaps");
        var wantAchievements = Args.HasFlag("--achievements");

        if (!wantIaps && !wantAchievements)
            wantIaps = wantAchievements = true;

        try
        {
            if (string.IsNullOrWhiteSpace(Config.AppId))
            {
                Console.WriteLine("[ERROR] no app id. specify it in config.json or with --app-id");
                return;
            }

            if (keep.Count == 0)
            {
                Console.WriteLine("[ERROR] --keep is required, and naming no language would delete everything.");
                Console.WriteLine("        for example: locales prune --keep en-US");
                return;
            }

            Console.WriteLine($"   -> keeping: {string.Join(", ", keep)}");

            if (!confirm)
                Console.WriteLine("   -> DRY RUN, nothing will be deleted. Pass --confirm to apply.");

            if (wantIaps)
                await PruneIapsAsync(keep, confirm);

            if (wantAchievements)
                await PruneAchievementsAsync(keep, confirm);
        }
        catch (Exception ex)
        {
            PrintApiError("failed to prune localizations", ex);
        }
    }

    private async Task PruneIapsAsync(List<string> keep, bool confirm)
    {
        Console.WriteLine();
        Console.WriteLine("   -> In-App Purchases...");

        var products = await GetIapsAsync(Verbose);
        products = FilterByIap(products, p => p.ProductId);

        var doomed = new List<Doomed>();
        var refused = new List<string>();

        foreach (var product in products)
        {
            var kept = product.Localizations
                .Where(l => keep.Contains(l.Attributes?.Locale ?? "", StringComparer.OrdinalIgnoreCase))
                .ToList();

            // deleting every localization of a product is not something App Store Connect allows,
            // and it is never what somebody pruning a language list meant to do
            if (kept.Count == 0)
            {
                refused.Add($"{product.ProductId} (has none of the kept languages)");
                continue;
            }

            foreach (var localization in product.Localizations.Except(kept))
                doomed.Add(new Doomed(product.ProductId, localization.Attributes?.Locale ?? "?", localization.Id));
        }

        PrintPlan(doomed, refused, "product");

        if (!confirm || doomed.Count == 0)
            return;

        var api = new InAppPurchaseLocalizationsApi(Service);
        await DeleteAllAsync(doomed, id => api.InAppPurchaseLocalizationsDeleteInstanceAsync(id));
    }

    private async Task PruneAchievementsAsync(List<string> keep, bool confirm)
    {
        Console.WriteLine();
        Console.WriteLine("   -> Game Center achievements...");

        var achievements = await GetAchievementsAsync(Verbose);
        if (achievements is null)
            return;

        var doomed = new List<Doomed>();
        var refused = new List<string>();

        foreach (var achievement in achievements)
        {
            var kept = achievement.Localizations
                .Where(l => keep.Contains(l.Attributes?.Locale ?? "", StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (kept.Count == 0)
            {
                refused.Add($"{achievement.VendorIdentifier} (has none of the kept languages)");
                continue;
            }

            foreach (var localization in achievement.Localizations.Except(kept))
                doomed.Add(new Doomed(achievement.VendorIdentifier, localization.Attributes?.Locale ?? "?", localization.Id));
        }

        PrintPlan(doomed, refused, "achievement");

        if (!confirm || doomed.Count == 0)
            return;

        var api = new GameCenterAchievementLocalizationsApi(Service);
        await DeleteAllAsync(doomed, id => api.GameCenterAchievementLocalizationsDeleteInstanceAsync(id));
    }

    /// <summary>
    /// what would go, grouped by language rather than by item: forty products losing the same
    /// language is one decision, not forty lines to read
    /// </summary>
    private static void PrintPlan(List<Doomed> doomed, List<string> refused, string noun)
    {
        foreach (var item in refused)
            Console.WriteLine($"      [SKIP] {item}");

        if (doomed.Count == 0)
        {
            Console.WriteLine($"      nothing to delete, every {noun} already has only the kept languages.");
            return;
        }

        var byLocale = doomed
            .GroupBy(d => d.Locale, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        Console.WriteLine($"      {doomed.Count} localization(s) to delete, across {doomed.Select(d => d.Owner).Distinct().Count()} {noun}(s):");

        foreach (var group in byLocale)
            Console.WriteLine($"         {group.Key,-10} {group.Count()} {noun}(s)");
    }

    private async Task DeleteAllAsync(List<Doomed> doomed, Func<string, Task> delete)
    {
        var deleted = 0;
        var failed = 0;

        foreach (var item in doomed)
        {
            try
            {
                await delete(item.Id);
                deleted++;

                if (Verbose)
                    Console.WriteLine($"      [GONE] {item.Owner} [{item.Locale}]");
                else if (deleted % 50 == 0)
                    Console.WriteLine($"      {deleted} of {doomed.Count} deleted...");
            }
            catch (Exception ex)
            {
                PrintApiError($"failed to delete {item.Owner} [{item.Locale}]", ex);
                failed++;
            }
        }

        Console.WriteLine($"      {deleted} deleted, {failed} failed.");
    }
}
