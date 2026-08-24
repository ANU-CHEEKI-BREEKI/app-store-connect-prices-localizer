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
            var basePrices = await CommandLinesUtils.LoadBasePrices(Config.ProductDefinitionsFilePath, Args.HasFlag("-v"));
            if (basePrices is null)
                return;
            var localPercentages = await CommandLinesUtils.LoadJson<LocalizedPricesPercentagesConfigs>(Config.LocalizedPricesTemplateFilePath, "./configs/localized-prices-template.json", Args.HasFlag("-v")) ?? new();

            // no restore pre-step: the base price comes straight from the json config, so
            // there is nothing to write before the one real write at the end
            var restorer = new Command_Restore();
            restorer.Initialize(Auth, Config, Args);

            Console.WriteLine("   -> Localizing IAPs...");
            Console.WriteLine("   -> Receiving IAP list...");

            var page = await Http.GetPagedAsync($"/v1/apps/{appId}/inAppPurchasesV2?limit=200");

            var iaps = FilterByIap(page.Data, p => (string?)p?["attributes"]?["productId"]);

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
                    await LocalizePrises(item!, basePrices, pricesSetup, localPercentages, baseTerritory, v);
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
            var listCommand = new Command_List();
            listCommand.Initialize(Auth, Config, Args);
            await listCommand.ExecuteAsync();

            Console.WriteLine($"   -> {AscHttp.RequestCount} request(s) to App Store Connect this run.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task LocalizePrises(JsonNode iap, ProductConfigs basePrices, List<IapPriceSetup> pricesSetup, LocalizedPricesPercentagesConfigs localPercentages, string baseTerritory, bool v)
    {
        var productId = (string?)iap["attributes"]?["productId"] ?? "";

        if (!basePrices.TryGetValue(productId, out var configuredPrice))
        {
            Console.WriteLine($"[SKIP] {productId}: no default_price in the product definitions csv, nothing to localize from.");
            return;
        }

        // the marketing tweak restore always applied: 5.00 becomes 4.99
        if (configuredPrice == Math.Truncate(configuredPrice))
            configuredPrice -= 0.01m;

        // the base territory's grid, the only grid this command ever pages through
        var gridPage = await Http.GetPagedAsync(
            $"/v2/inAppPurchases/{(string?)iap["id"]}/pricePoints?filter[territory]={baseTerritory}&limit=200"
        );
        var grid = gridPage.Data.Where(g => g is not null).Select(g => g!).ToList();

        var baseAnchor = Command_Restore.FindClosestInGrid(grid, (double)configuredPrice);

        if (baseAnchor is null)
        {
            Console.WriteLine($"[SKIP] {productId}: no price point near {configuredPrice} in {baseTerritory}.");
            return;
        }

        Console.WriteLine($"   -> Localizing iap: {productId}.");

        // one equalizations call per distinct multiplier: apple answers with its matching
        // point of every territory at once, already in that territory's own currency
        var multipliers = localPercentages.Values.Append(1m).Distinct().ToList();
        var byMultiplier = new Dictionary<decimal, Dictionary<string, JsonNode>>();

        foreach (var multiplier in multipliers)
        {
            var target = configuredPrice * multiplier;
            if (target == Math.Truncate(target))
                target -= 0.01m;

            var anchor = multiplier == 1m ? baseAnchor : Command_Restore.FindClosestInGrid(grid, (double)target);
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

        var priceSetup = new IapPriceSetup()
        {
            Iap = iap,
            BasePrice = (double)configuredPrice,
            BaseTerritoryCode = baseTerritory,
            LocalPrices = new()
        };

        // every territory the base equalizes into gets the point of its own multiplier
        foreach (var (territory, basePoint) in byMultiplier[1m])
        {
            var multiplier = localPercentages.TryGetValue(territory, out var percentage) ? percentage : 1m;

            if (!byMultiplier.TryGetValue(multiplier, out var perTerritory)
                || !perTerritory.TryGetValue(territory, out var point))
                continue;

            priceSetup.CandidatePoints[territory] = point;
            priceSetup.LocalPrices[territory] = double.Parse((string)point["attributes"]!["customerPrice"]!, CultureInfo.InvariantCulture);

            if (v)
                Console.WriteLine($"Calculating price for {territory}: {(string?)basePoint["attributes"]?["customerPrice"],10} * {multiplier,3} = {priceSetup.LocalPrices[territory],10:##.00}");
        }

        lock (pricesSetup)
            pricesSetup.Add(priceSetup);
    }

    public override string Name => "localize";
    public override string Description => "Recalculates prices for all regions based on the default_price column of the product definitions csv and the localized prices template.";

    public override void PrintHelp()
    {
        Console.WriteLine("localize [--products <path-to-product-definitions.csv>] [--localized-template <path-to-localized-template.json>] [--iap <id[,id...]>] [--parallel <n>] [-v] [-l]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--products <path>",
            "Specifies path to the product definitions csv the base prices are read from. If not specified, used path from global config json ('ProductDefinitionsFilePath')."
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
