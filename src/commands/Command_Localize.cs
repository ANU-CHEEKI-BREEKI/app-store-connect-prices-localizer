using System.Globalization;
using System.Text.Json.Nodes;

public class Command_Localize : CommandBase
{
    protected override async Task InternalExecuteAsync()
    {
        try
        {
            var v = Args.HasFlag("-v");

            var appId = Config.AppId;
            var baseTerritory = Config.DefaultRegion;
            var basePrices = await CommandLinesUtils.LoadJson<ProductConfigs>(Config.DefaultPricesFilePath, "../default-prices-usd.json", Args.HasFlag("-v")) ?? new();
            var localPercentages = await CommandLinesUtils.LoadJson<LocalizedPricesPercentagesConfigs>(Config.LocalizedPricesTemplateFilePath, "./configs/localized-prices-template.json", Args.HasFlag("-v")) ?? new();

            // restore prices first
            var restorer = new Command_Restore();
            restorer.Initialize(Auth, Config, Args);
            await restorer.ExecuteAsync();

            Console.WriteLine("   -> Localizing IAPs...");
            Console.WriteLine("   -> Receiving IAP list...");

            var page = await Http.GetPagedAsync($"/v1/apps/{appId}/inAppPurchasesV2?limit=200");

            var iaps = FilterByIap(page.Data, p => (string?)p?["attributes"]?["productId"]);

            // using to get local prices
            var listCommand = new Command_List();
            listCommand.Initialize(Auth, Config, Args);

            var pricesSetup = new List<IapPriceSetup>();
            var failed = new List<string>();

            var parallelism = ResolveParallelism(iaps.Count, v);
            var gate = new SemaphoreSlim(parallelism);

            // products are independent, so a few of them go at once; how many is decided
            // above from the quota the api reports. The lists are shared, hence the locks
            await Task.WhenAll(iaps.Select(async item =>
            {
                await gate.WaitAsync();

                try
                {
                    await LocalizePrises(item!, listCommand, pricesSetup, localPercentages, baseTerritory, v);
                }
                catch (Exception ex)
                {
                    lock (failed)
                    {
                        Console.WriteLine($"[FAILED] {(string?)item?["attributes"]?["productId"]}: {ex.Message}");
                        failed.Add((string?)item?["attributes"]?["productId"] ?? "?");
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));

            await Task.WhenAll(pricesSetup.Select(async setup =>
            {
                await gate.WaitAsync();

                try
                {
                    await restorer.SetPrices(setup, v);
                }
                catch (Exception ex)
                {
                    lock (failed)
                    {
                        Console.WriteLine($"[FAILED] {(string?)setup.Iap["attributes"]?["productId"]}: {ex.Message}");
                        failed.Add((string?)setup.Iap["attributes"]?["productId"] ?? "?");
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));

            if (failed.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"[RETRY] {failed.Count} product(s) failed. Nothing was half-written: a product either got its whole new price schedule or kept the old one. Run again for just them:");
                Console.WriteLine($"        dotnet run -- localize --iap {string.Join(",", failed.Distinct())}");
            }

            // print what we set at the end
            await listCommand.ExecuteAsync();

            Console.WriteLine($"   -> {AscHttp.RequestCount} request(s) to App Store Connect this run.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task LocalizePrises(JsonNode iap, Command_List listCommand, List<IapPriceSetup> pricesSetup, LocalizedPricesPercentagesConfigs localPercentages, string baseTerritory, bool v)
    {
        var basePice = await listCommand.GetBasePrice(iap);
        var prices = await listCommand.GetAllLocalPricesAsync(iap);

        var priceSetup = new IapPriceSetup()
        {
            Iap = iap,
            BasePrice = double.Parse((string)basePice.PricePoint["attributes"]!["customerPrice"]!, CultureInfo.InvariantCulture),
            BaseTerritoryCode = basePice.TerritoryCode,
            LocalPrices = new()
        };
        pricesSetup.Add(priceSetup);

        Console.WriteLine($"   -> Localizing iap: {(string?)iap["attributes"]?["productId"]}.");

        foreach (var pr in prices)
        {
            var multiplier = localPercentages.TryGetValue(pr.Value.TerritoryCode, out var percentage) ? percentage : 1m;

            var newPrice = decimal.Parse(
                (string)pr.Value.PricePoint["attributes"]!["customerPrice"]!, CultureInfo.InvariantCulture
            ) * multiplier;

            // make more like marketing price 5.00 -> 4.99 and hope it will be rounded as price point 4.99
            if (Math.Truncate(newPrice) == newPrice)
                newPrice -= 0.01m;

            priceSetup.LocalPrices[pr.Value.TerritoryCode] = (double)newPrice;

            if (v)
                Console.WriteLine($"Calculating price for {pr.Value.TerritoryCode}: {(string?)pr.Value.PricePoint["attributes"]?["customerPrice"],10} * {multiplier,3} - 0.01 = {newPrice,10:##.00}");
        }

        // the targets above are what the anchors resolve against, so this comes last
        await ResolveCandidatePointsAsync(iap, priceSetup, localPercentages, baseTerritory, v);
    }

    /// <summary>
    /// Resolves the exact price point of almost every territory without searching its grid.
    ///
    /// The trick: the template has only a handful of distinct multipliers. For each one the base
    /// territory's grid (fetched once) gives an anchor point priced at base*multiplier, and one
    /// 'equalizations' call on that anchor hands back Apple's matching point in every territory at
    /// once. A request per multiplier instead of a request per territory - it is the request count
    /// that runs into the api quota, not the latency.
    /// </summary>
    private async Task ResolveCandidatePointsAsync(JsonNode iap, IapPriceSetup priceSetup, LocalizedPricesPercentagesConfigs localPercentages, string baseTerritory, bool v)
    {
        // the base territory's full grid: the only grid this command ever pages through
        var gridPage = await Http.GetPagedAsync(
            $"/v2/inAppPurchases/{(string?)iap["id"]}/pricePoints?filter[territory]={baseTerritory}&limit=200"
        );
        var grid = gridPage.Data.Where(p => p is not null).Select(p => p!).ToList();

        if (grid.Count == 0)
            return;

        var multipliers = priceSetup.LocalPrices.Keys
            .Select(t => localPercentages.TryGetValue(t, out var m) ? m : 1m)
            .Distinct()
            .ToList();

        if (v)
            Console.WriteLine($"Resolving anchors for {multipliers.Count} multiplier(s) over the {baseTerritory} grid of {grid.Count} point(s)...");

        var byMultiplier = new Dictionary<decimal, Dictionary<string, JsonNode>>();

        foreach (var multiplier in multipliers)
        {
            var target = (decimal)priceSetup.BasePrice * multiplier;
            if (Math.Truncate(target) == target)
                target -= 0.01m;

            var anchor = Command_Restore.FindClosestInGrid(grid, (double)target);
            if (anchor is null)
                continue;

            var perTerritory = new Dictionary<string, JsonNode>(StringComparer.Ordinal);

            var equalizations = await Http.GetPagedAsync(
                $"/v1/inAppPurchasePricePoints/{Uri.EscapeDataString((string)anchor["id"]!)}/equalizations?include=territory&limit=200"
            );

            foreach (var point in equalizations.Data)
            {
                var territoryId = (string?)point?["relationships"]?["territory"]?["data"]?["id"];
                if (territoryId is not null)
                    perTerritory[territoryId] = point!;
            }

            // the anchor itself is the base territory's answer; equalizations do not echo it back
            perTerritory[baseTerritory] = anchor;

            byMultiplier[multiplier] = perTerritory;

            if (v)
                Console.WriteLine($"Anchor x{multiplier}: {(string?)anchor["attributes"]?["customerPrice"]} {baseTerritory}, equalized into {perTerritory.Count} territories.");
        }

        foreach (var territory in priceSetup.LocalPrices.Keys)
        {
            var multiplier = localPercentages.TryGetValue(territory, out var m) ? m : 1m;

            if (byMultiplier.TryGetValue(multiplier, out var perTerritory)
                && perTerritory.TryGetValue(territory, out var point))
                priceSetup.CandidatePoints[territory] = point;
        }
    }

    /// <summary>
    /// How many products go at once. An explicit --parallel wins; otherwise the quota the api
    /// reports decides: when what is left would not even cover this run, everything goes one by
    /// one and lets the retry-on-429 pacing do its job.
    /// </summary>
    private int ResolveParallelism(int productCount, bool v)
    {
        var option = Args.TryGetOption("--parallel", "");

        if (int.TryParse(option, out var parsed))
            return Math.Clamp(parsed, 1, 8);

        var estimate = productCount * 35;
        var remaining = AscHttp.HourRemaining;

        var chosen = remaining is { } rem && rem < estimate + 100 ? 1 : 4;

        if (v)
            Console.WriteLine($"Parallelism: {chosen} (quota remaining: {remaining?.ToString() ?? "unknown"}, this run needs ~{estimate}).");

        return chosen;
    }

    public override string Name => "localize";
    public override string Description => "Recalculates prices for all regions based on the default currency price provided in your JSON config and localized prices template.";

    public override void PrintHelp()
    {
        Console.WriteLine("localize [--prices <path-to-default-prices.json>] [--localized-template <path-to-localized-template.json>] [--iap <id[,id...]>] [--parallel <n>] [-v] [-l]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--prices <path>",
            "Specifies path to json with default prices in default currency. If not specified, used path from global config json."
        );
        CommandLinesUtils.PrintOption(
            "--localized-template <path>",
            "Specifies path to json with percentages for each region that needs to be localized. Default path is: ./configs/localized-prices-template.json"
        );

        CommandLinesUtils.PrintOption(
            CommandLinesUtils.IapOptionName,
            CommandLinesUtils.IapOptionDescription
        );
        CommandLinesUtils.PrintOption(
            "--parallel <n>",
            "How many products to localize at once, 1 to 8. Without it the tool decides from the quota the api reports, normally 4."
        );
        CommandLinesUtils.PrintOption(
            "-v",
            "Include additional verbose output"
        );
        CommandLinesUtils.PrintOption(
            "-l",
            "Include local pricing for all regions"
        );
    }

    public record StoreTerritory(string Code, string Currency);

    //TODO: move to separate command Command_ListTerritories
    public async Task<List<StoreTerritory>> GetAllTerritoriesAsync()
    {
        var allCodes = new List<StoreTerritory>();

        Console.WriteLine("loading supported territories ids...");

        //FIXME: what if more that 200?
        var response = await Http.GetAsync("/v1/territories?limit=200");

        foreach (var territory in response["data"] as JsonArray ?? new JsonArray())
        {
            // territory["id"] - territory code (for example "USA", "UKR", "JPN")
            // territory["attributes"]["currency"] - currency code (for example "USD", "UAH", "JPY")

            allCodes.Add(new StoreTerritory
            (
                (string)territory!["id"]!,
                (string)territory["attributes"]!["currency"]!
            ));
        }

        return allCodes;
    }
}
