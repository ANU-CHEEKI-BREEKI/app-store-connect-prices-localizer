using System.Text.Json.Nodes;

public class Command_List : CommandBase
{
    public class TerritoryPrice : Dictionary<string, JsonNode> { }

    protected override async Task InternalExecuteAsync()
    {
        try
        {
            var printPrices = Args.HasFlag("-p");
            var printLocalPrices = Args.HasFlag("-l");
            var verbose = Args.HasFlag("-v");

            if (verbose)
                Console.WriteLine("   -> receiving IAP list...");

            var appId = Config.AppId;

            var page = await Http.GetPagedAsync($"/v1/apps/{appId}/inAppPurchasesV2?limit=200");

            var iaps = FilterByIap(page.Data, p => (string?)p?["attributes"]?["productId"]);


            foreach (var iap in iaps)
                Console.WriteLine((string?)iap?["attributes"]?["productId"]);

            Console.WriteLine();

            if (!printPrices)
                return;

            var pricePoints = new Dictionary<JsonNode, InAppPriceData?>();
            var localPricePoints = new Dictionary<JsonNode, Dictionary<string, InAppPriceData>>();

            Console.WriteLine($"   -> fetching prices...");

            foreach (var iap in iaps)
            {
                pricePoints[iap!] = await GetBasePrice(iap!);

                if (printLocalPrices)
                    localPricePoints[iap!] = await GetAllLocalPricesAsync(iap!);
            }

            var stringPairs = new List<StringPairs>();

            foreach (var iap in iaps)
            {
                var price = pricePoints[iap!];

                stringPairs.Add(new StringPairs { A = (string?)iap?["attributes"]?["productId"], B = $"{(string?)price?.PricePoint?["attributes"]?["customerPrice"]} {price?.Currency}" });
                // Console.WriteLine($"{price?.TerritoryCode,5} {(string?)price?.PricePoint?["attributes"]?["customerPrice"],10} {price?.Currency,5} {(string?)iap?["attributes"]?["productId"],5}");

                if (printLocalPrices)
                {
                    var localPrices = localPricePoints[iap!];
                    foreach (var item in localPrices)
                        stringPairs.Add(new StringPairs { A = $"    {item.Key}", B = $"{(string?)item.Value.PricePoint["attributes"]?["customerPrice"]} {item.Value.Currency}" });
                    // Console.WriteLine($"{item.Key,5} : {(string?)item.Value.PricePoint["attributes"]?["customerPrice"],10} {item.Value.Currency,5}");
                }
            }

            var aMaxLength = stringPairs.Max(p => p.A.Length) + 4;
            var bMaxLength = stringPairs.Max(p => p.B.Length) + 4;

            Console.WriteLine();

            foreach (var item in stringPairs)
                Console.WriteLine($"{item.A.PadRight(aMaxLength, '.')}{item.B.PadLeft(bMaxLength, '.')}");

        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private class StringPairs
    {
        public string A;
        public string B;
    }

    public override string Name => "list";
    public override string Description => "Lists all One-time products in the project, and their prices for specified region.";

    public override void PrintHelp()
    {
        Console.WriteLine("list [-p] [-l] [--iap <id[,id...]>] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);

        Console.WriteLine();
        Console.WriteLine("options:");
        CommandLinesUtils.PrintOption(
            "-p",
            "Include pricing"
        );
        CommandLinesUtils.PrintOption(
            "-l",
            "Include local pricing for all regions. Only if -p is specified"
        );
        CommandLinesUtils.PrintOption(
            CommandLinesUtils.IapOptionName,
            CommandLinesUtils.IapOptionDescription
        );
        CommandLinesUtils.PrintOption(
            "-v",
            "Include detailed verbose output"
        );
    }

    public class InAppPriceData
    {
        public JsonNode Iap { get; set; }
        public JsonNode PricePoint { get; set; }
        public string TerritoryCode { get; set; }
        public string Currency { get; set; }
    }

    public async Task<Dictionary<string, InAppPriceData>> GetAllLocalPricesAsync(JsonNode iap)
    {
        var verbose = Args.HasFlag("-v");
        var results = new Dictionary<string, InAppPriceData>();

        Console.WriteLine($"   -> Getting full price list for {(string?)iap["attributes"]?["name"]}...");

        var scheduleResponse = await Http.GetAsync(
            $"/v2/inAppPurchases/{(string?)iap["id"]}/iapPriceSchedule"
            + "?include=manualPrices,automaticPrices,baseTerritory"
            + "&limit[manualPrices]=50&limit[automaticPrices]=50"
        );

        if (scheduleResponse["data"] is null)
        {
            if (verbose)
                Console.WriteLine("Error: Schedule Data is null.");
            return results;
        }

        var scheduleId = (string?)scheduleResponse["data"]?["id"];

        var manualResponse = await Http.GetAsync(
            $"/v1/inAppPurchasePriceSchedules/{scheduleId}/manualPrices?include=inAppPurchasePricePoint,territory&limit=200"
        );

        var manualPricesData = ParsePricesAndCurrencies(manualResponse, iap);

        foreach (var item in manualPricesData)
            results[item.Key] = item.Value;

        if (verbose)
            Console.WriteLine($"Loaded {results.Count} manual overrides.");

        string? basePricePointId = (string?)(await GetBasePrice(iap))?.PricePoint["id"];

        if (basePricePointId == null)
        {
            if (verbose)
                Console.WriteLine("Warning: Could not determine Base Price Point ID. Cannot fetch equalizations.");
            return results;
        }

        if (verbose)
            Console.WriteLine($"Fetching equalizations for Point ID: {basePricePointId}...");

        try
        {
            // the price point id is an opaque string that may carry characters a url path can not,
            // so it is escaped for transport and never decoded or rebuilt
            var equalizationsResponse = await Http.GetAsync(
                $"/v1/inAppPurchasePricePoints/{Uri.EscapeDataString(basePricePointId)}/equalizations?include=territory&limit=200"
            );

            if (equalizationsResponse["data"] is JsonArray equalizations && equalizationsResponse["included"] is JsonArray equalizationsIncluded)
            {
                var currencyMap = ExtractCurrencyMap(equalizationsIncluded);

                foreach (var pricePoint in equalizations)
                {
                    var territoryId = (string?)pricePoint?["relationships"]?["territory"]?["data"]?["id"];

                    if (territoryId != null)
                    {
                        if (!results.ContainsKey(territoryId))
                        {
                            currencyMap.TryGetValue(territoryId, out var currencyCode);

                            results[territoryId] = new InAppPriceData
                            {
                                Iap = iap,
                                PricePoint = pricePoint!,
                                TerritoryCode = territoryId,
                                Currency = currencyCode ?? "UNKNOWN"
                            };
                        }
                    }
                }
                if (verbose)
                    Console.WriteLine($"Added automatic prices. Total count: {results.Count}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching equalizations: {ex.Message}");
        }

        return results;
    }

    private Dictionary<string, InAppPriceData> ParsePricesAndCurrencies(JsonNode response, JsonNode iap)
    {
        var res = new Dictionary<string, InAppPriceData>();

        if (response["data"] is not JsonArray data || response["included"] is not JsonArray included)
            return res;

        var pointsMap = included
            .Where(i => (string?)i?["type"] == "inAppPurchasePricePoints")
            .ToDictionary(p => (string)p!["id"]!, p => p!);

        var currencyMap = ExtractCurrencyMap(included);

        foreach (var entry in data)
        {
            var territoryId = (string?)entry?["relationships"]?["territory"]?["data"]?["id"];
            var pointId = (string?)entry?["relationships"]?["inAppPurchasePricePoint"]?["data"]?["id"];

            if (territoryId != null && pointId != null && pointsMap.TryGetValue(pointId, out var point))
            {
                currencyMap.TryGetValue(territoryId, out var currency);

                res[territoryId] = new InAppPriceData
                {
                    Iap = iap,
                    PricePoint = point,
                    TerritoryCode = territoryId,
                    Currency = currency ?? "UNKNOWN"
                };
            }
        }
        return res;
    }

    private Dictionary<string, string> ExtractCurrencyMap(JsonArray includedList)
    {
        var map = new Dictionary<string, string>();

        foreach (var t in includedList)
        {
            if ((string?)t?["type"] != "territories")
                continue;

            var id = (string?)t?["id"];
            var currency = (string?)t?["attributes"]?["currency"];

            if (id != null && currency != null)
                map[id] = currency;
        }

        return map;
    }

    public async Task<InAppPriceData?> GetBasePrice(JsonNode iap)
    {
        var v = Args.HasFlag("-v");

        Console.WriteLine($"   -> Fetching prices for: {(string?)iap["attributes"]?["name"]}...");

        var baseTerritory = Config.DefaultRegion;

        var scheduleResponse = await Http.GetAsync($"/v2/inAppPurchases/{(string?)iap["id"]}/iapPriceSchedule");

        if (scheduleResponse["data"] is null)
        {
            if (v)
                Console.WriteLine("   -> No price schedule found.");
            return null;
        }

        var scheduleId = (string?)scheduleResponse["data"]?["id"];

        if (v)
            Console.WriteLine($"   -> Fetching prices for Schedule ID: {scheduleId}...");

        try
        {
            var pricesResponse = await Http.GetAsync(
                $"/v1/inAppPurchasePriceSchedules/{scheduleId}/manualPrices?filter[territory]={baseTerritory}&include=inAppPurchasePricePoint,territory"
            );

            if (pricesResponse["included"] is JsonArray included)
            {
                foreach (var item in included)
                {
                    if ((string?)item?["type"] == "inAppPurchasePricePoints")
                    {
                        var pricePoint = item!;

                        if (v)
                        {
                            var price = (string?)pricePoint["attributes"]?["customerPrice"];
                            var proceeds = (string?)pricePoint["attributes"]?["proceeds"];
                            Console.WriteLine($"   -> Price ({baseTerritory}): {price} (Proceeds: {proceeds})");
                        }

                        var currencies = ExtractCurrencyMap(included);

                        return new InAppPriceData
                        {
                            Iap = iap,
                            PricePoint = pricePoint,
                            TerritoryCode = baseTerritory,
                            Currency = currencies[baseTerritory]
                        };
                    }
                }
            }

            if (v)
                if ((pricesResponse["data"] as JsonArray)?.Count == 0)
                    Console.WriteLine("   -> Price not set manually (might be Free).");
        }
        catch (AscApiException ex)
        {
            if (v)
                Console.WriteLine($"   -> Price Fetch Error: {ex.Message}");
        }

        return null;
    }
}
