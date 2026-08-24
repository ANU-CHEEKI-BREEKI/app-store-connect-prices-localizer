using System.Globalization;
using System.Text.Json.Nodes;

public class IapPriceSetup
{
    /// <summary>
    /// whole class here to not be confused what is iap id, product id, product name, etc
    /// Iap is the 'data' element of an In-App Purchase from the App Store Connect api
    /// </summary>
    public required JsonNode Iap;
    public double BasePrice;
    public required string BaseTerritoryCode;
    public PricePerTerritory LocalPrices = new();

    /// <summary>points already resolved for a territory; SetPrices uses them without a search</summary>
    public Dictionary<string, JsonNode> CandidatePoints = new(StringComparer.Ordinal);
}

public class PricePerTerritory : Dictionary<string, double> { }

public class Command_Restore : CommandBase
{
    public override string Name => "restore";
    public override string Description => "Recalculates prices for all regions based on the default_price column of the product definitions csv.";

    public override void PrintHelp()
    {
        Console.WriteLine("restore [--products <path-to-product-definitions.csv>] [--iap <id[,id...]>] [-v] [-l]");
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
            CommandLinesUtils.IapOptionName,
            CommandLinesUtils.IapOptionDescription
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

    protected override async Task InternalExecuteAsync()
    {
        if (!await RestorePrices())
            return;

        // print what we set at the end
        var listCommand = new Command_List();
        listCommand.Initialize(Auth, Config, Args);
        await listCommand.ExecuteAsync();
    }

    /// <summary>
    /// set default prices
    /// </summary>
    private async Task<bool> RestorePrices()
    {
        var basePrices = await CommandLinesUtils.LoadBasePrices(Config.ProductDefinitionsFilePath, Args.HasFlag("-v"));
        if (basePrices is null)
            return false;

        var verbose = Args.HasFlag("-v");
        try
        {
            Console.WriteLine("   -> Restoring IAP Prices...");
            Console.WriteLine("   -> Receiving IAP list...");
            var page = await Http.GetPagedAsync($"/v1/apps/{Config.AppId}/inAppPurchasesV2?limit=200");

            var iaps = FilterByIap(page.Data, p => (string?)p?["attributes"]?["productId"]);

            // for each iap on server - just set default price
            var iapPrices = new List<IapPriceSetup>();
            foreach (var iap in iaps)
            {
                var productId = (string?)iap?["attributes"]?["productId"];

                if (productId is null || !basePrices.TryGetValue(productId, out var basePrice))
                    continue;

                // forcibly adjust price if it is a whole number
                // to make sure we have marketable price
                // AUTOMATICALLY how Google Play Console does
                if (basePrice == Math.Truncate(basePrice))
                    basePrice -= 0.01m;

                iapPrices.Add(new IapPriceSetup
                {
                    Iap = iap!,
                    BasePrice = (double)basePrice,
                    BaseTerritoryCode = Config.DefaultRegion,
                });
            }

            await SetPrices(iapPrices, verbose);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }

        return true;
    }

    /// <summary>
    /// Set concrete prices
    /// </summary>
    public async Task SetPrices(List<IapPriceSetup> iapPrices, bool verbose)
    {
        Console.WriteLine("   -> Settings IAP Prices...");

        var failed = new List<string>();
        var gate = new SemaphoreSlim(ResolveParallelism(iapPrices.Count, verbose));

        // products are independent, so a few of them go at once, same as localize
        await Task.WhenAll(iapPrices.Select(async iap =>
        {
            await gate.WaitAsync();

            try
            {
                await SetPrices(iap, verbose);
            }
            catch (Exception ex)
            {
                lock (failed)
                {
                    Console.WriteLine($"[FAILED] {(string?)iap.Iap["attributes"]?["productId"]}: {ex.Message}");
                    failed.Add((string?)iap.Iap["attributes"]?["productId"] ?? "?");
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
            Console.WriteLine($"[RETRY] {failed.Count} product(s) failed. A product either got its whole price schedule or kept the old one. Run again for just them:");
            Console.WriteLine($"        dotnet run -- restore --iap {string.Join(",", failed.Distinct())}");
        }
    }

    public async Task SetPrices(IapPriceSetup iapSettings, bool verbose)
    {
        if (verbose)
            Console.WriteLine($"   -> Prepare iap price for IAP: {(string?)iapSettings.Iap["attributes"]?["productId"]}.");

        var manualPrices = new List<JsonObject>();

        if (verbose)
            Console.WriteLine($"   -> Prepare iap price for territory: {iapSettings.BaseTerritoryCode}.");

        var basePoint = iapSettings.CandidatePoints.GetValueOrDefault(iapSettings.BaseTerritoryCode)
            ?? await GetClosestPricePointId(iapSettings.Iap, iapSettings.BaseTerritoryCode, iapSettings.BasePrice, verbose);
        manualPrices.Add(
            CreatePriceEntry(basePoint!)
        );

        foreach (var territory in iapSettings.LocalPrices)
        {
            // already set base price
            if (territory.Key == iapSettings.BaseTerritoryCode)
                continue;

            if (verbose)
                Console.WriteLine($"   -> Prepare iap price for territory: {territory.Key}.");

            var territoryCode = territory.Key;
            var targetPrice = territory.Value;

            // a candidate is a point the caller already holds in hand for this territory,
            // so nothing needs to be searched for; the paged search is the fallback
            var localPoint = iapSettings.CandidatePoints.GetValueOrDefault(territoryCode)
                ?? await GetClosestPricePointId(iapSettings.Iap, territoryCode, targetPrice, verbose);

            if (localPoint != null)
            {
                manualPrices.Add(
                    CreatePriceEntry(localPoint)
                );

                if (verbose)
                    Console.WriteLine($" -> Set {territoryCode} to CustomerPrice: {(string?)localPoint?["attributes"]?["customerPrice"]}");
            }
        }

        Console.WriteLine($"      {(string?)iapSettings.Iap["attributes"]?["productId"],-52} {manualPrices.Count} territories prepared.");

        await PushNewSchedule(iapSettings.Iap, iapSettings.BaseTerritoryCode, manualPrices, verbose);
    }

    /// <summary>
    /// The same answer the paged search gives, from a grid already in hand: the point nearest to
    /// the target by absolute difference, the higher one on a tie, exactly like the ascending
    /// page walk did.
    /// </summary>
    public static JsonNode? FindClosestInGrid(IReadOnlyList<JsonNode> grid, double targetPrice)
    {
        JsonNode? best = null;
        var bestDiff = double.MaxValue;

        foreach (var point in grid)
        {
            if (!double.TryParse((string?)point["attributes"]?["customerPrice"], NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
                continue;

            var diff = Math.Abs(price - targetPrice);

            if (diff <= bestDiff)
            {
                best = point;
                bestDiff = diff;
            }
        }

        return best;
    }

    public async Task<JsonNode?> GetClosestPricePointId(JsonNode iap, string territory, double targetPrice, bool verbose)
    {
        JsonNode? lastLowerPoint = null;

        if (verbose)
            Console.WriteLine($"Starting search for closest price to {targetPrice} in {territory}...");

        var response = await Http.GetAsync(
            $"/v2/inAppPurchases/{(string?)iap["id"]}/pricePoints?filter[territory]={territory}&limit=200"
        );

        var result = FindBestInPage(response["data"] as JsonArray, targetPrice, lastLowerPoint);

        if (result.FoundMatch != null)
        {
            if (verbose)
                Console.WriteLine($"Found: {(string?)result.FoundMatch["attributes"]?["customerPrice"]} (ID: {(string?)result.FoundMatch["id"]})");
            return result.FoundMatch;
        }

        lastLowerPoint = result.LastSeen ?? lastLowerPoint;

        var nextHref = (string?)response["links"]?["next"];
        var page = 1;

        while (!string.IsNullOrEmpty(nextHref))
        {
            page++;

            if (verbose)
                Console.WriteLine($"Fetching Page {page}...");

            try
            {
                var nextUri = new Uri(nextHref);
                var relativePath = nextUri.PathAndQuery;

                var pageResponse = await Http.GetAsync(relativePath);

                if (pageResponse["data"] is JsonArray pageData)
                {
                    var pageResult = FindBestInPage(pageData, targetPrice, lastLowerPoint);

                    if (pageResult.FoundMatch != null)
                    {
                        if (verbose)
                            Console.WriteLine($"Found on Page {page}: {(string?)pageResult.FoundMatch["attributes"]?["customerPrice"]} (ID: {(string?)pageResult.FoundMatch["id"]})");
                        return pageResult.FoundMatch;
                    }

                    lastLowerPoint = pageResult.LastSeen ?? lastLowerPoint;
                }

                nextHref = (string?)pageResponse["links"]?["next"];
            }
            catch (Exception ex)
            {
                if (verbose)
                    Console.WriteLine($"Error fetching page {page}: {ex.Message}");
                break;
            }
        }

        if (lastLowerPoint != null)
        {
            if (verbose)
                Console.WriteLine($"Target price is higher than max available. Returning max: {(string?)lastLowerPoint["attributes"]?["customerPrice"]}");
            return lastLowerPoint;
        }

        if (verbose)
            Console.WriteLine("Search finished. No price found.");
        return null;
    }

    private (JsonNode? FoundMatch, JsonNode? LastSeen) FindBestInPage(
        JsonArray? points,
        double target,
        JsonNode? previousPageLastItem)
    {
        if (points == null || points.Count == 0)
            return (null, previousPageLastItem);

        JsonNode? prev = previousPageLastItem;

        foreach (var current in points)
        {
            if (double.TryParse((string?)current?["attributes"]?["customerPrice"], NumberStyles.Any, CultureInfo.InvariantCulture, out double currentPrice))
            {
                // Якщо поточна ціна перевищила або дорівнює цілі -> ми знайшли точку перетину
                if (currentPrice >= target)
                {
                    // Якщо це найперший елемент взагалі (немає попереднього), то він і є найближчим
                    if (prev == null) return (current, current);

                    // Якщо є попередній, дивимось, хто ближче до цілі
                    if (double.TryParse((string?)prev["attributes"]?["customerPrice"], NumberStyles.Any, CultureInfo.InvariantCulture, out double prevPrice))
                    {
                        double diffPrev = Math.Abs(target - prevPrice);    // Наприклад |10 - 9| = 1
                        double diffCurr = Math.Abs(currentPrice - target); // Наприклад |12 - 10| = 2

                        // Повертаємо того, у кого різниця менша
                        return (diffPrev < diffCurr ? prev : current, current);
                    }

                    return (current, current);
                }
            }
            // Оновлюємо "попередній", бо ми йдемо далі
            prev = current;
        }

        // Якщо ми дійшли сюди, значить на цій сторінці всі ціни менші за target.
        // Повертаємо match = null, але оновлюємо LastSeen
        return (null, prev);
    }

    /// <summary>
    /// the inline 'inAppPurchasePrices' entry of the schedule create request. The '${guid}' id is
    /// the temporary client-side id the api resolves between 'relationships' and 'included'
    /// </summary>
    private JsonObject CreatePriceEntry(JsonNode pricePoint)
        => new()
        {
            ["type"] = "inAppPurchasePrices",
            ["id"] = "${" + Guid.NewGuid().ToString() + "}",
            ["attributes"] = new JsonObject { ["startDate"] = null },
            ["relationships"] = new JsonObject
            {
                // the price point id is opaque, it goes back to the api exactly as it came
                ["inAppPurchasePricePoint"] = AscHttp.Link("inAppPurchasePricePoints", (string)pricePoint["id"]!),
            },
        };

    private async Task PushNewSchedule(JsonNode iap, string baseTerritoryId, List<JsonObject> prices, bool verbose)
    {
        var request = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "inAppPurchasePriceSchedules",
                ["relationships"] = new JsonObject
                {
                    ["inAppPurchase"] = AscHttp.Link("inAppPurchases", (string)iap["id"]!),
                    ["baseTerritory"] = AscHttp.Link("territories", baseTerritoryId),
                    ["manualPrices"] = AscHttp.LinkMany("inAppPurchasePrices", prices.Select(p => (string)p["id"]!).ToList()),
                },
            },
            ["included"] = new JsonArray(prices.Select(p => (JsonNode)p).ToArray()),
        };

        if (verbose)
            Console.WriteLine($"Sending Create Schedule Request for {(string?)iap["attributes"]?["productId"]} ...");

        try
        {
            var response = await Http.PostAsync("/v1/inAppPurchasePriceSchedules", request);

            if (verbose)
            {
                Console.WriteLine($"[SUCCESS] Schedule created successfully!");
                Console.WriteLine($"   -> New Schedule ID: {(string?)response["data"]?["id"]}");
                Console.WriteLine($"   -> Link: {(string?)response["data"]?["links"]?["self"]}");

                if (response["included"] is JsonArray included)
                    Console.WriteLine($"   -> Included items count: {included.Count}");
            }
        }
        catch (AscApiException ex)
        {
            Console.WriteLine($"[API ERROR] {ex.Message}");
            Console.WriteLine($"Status: {ex.StatusCode}");
            Console.WriteLine($"Response Body: {ex.ResponseBody}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
        }
    }
}
