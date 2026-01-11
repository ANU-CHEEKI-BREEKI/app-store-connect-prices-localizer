
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

public class Command_List : CommandBase
{
    public class TerritoryPrice : Dictionary<string, InAppPurchasePricePoint> { }

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
            var baseTerritory = Config.DefaultRegion;

            var appApi = new AppsApi(Service);

            var iaps = await appApi.AppsInAppPurchasesV2GetToManyRelatedAsync(appId);

            var singeIap = Config.Iap;
            if (!string.IsNullOrEmpty(singeIap))
                iaps.Data = iaps.Data.Where(p => p.Attributes.ProductId == singeIap).ToList();


            foreach (var iap in iaps.Data)
                Console.WriteLine(iap.Attributes.ProductId);

            Console.WriteLine();

            if (!printPrices)
                return;

            var pricePoints = new Dictionary<InAppPurchaseV2, InAppPriceData?>();
            var localPricePoints = new Dictionary<InAppPurchaseV2, Dictionary<string, InAppPriceData>>();

            Console.WriteLine($"   -> fetching prices...");

            foreach (var iap in iaps.Data)
            {
                pricePoints[iap] = await GetBasePrice(iap);

                if (printLocalPrices)
                    localPricePoints[iap] = await GetAllLocalPricesAsync(iap);
            }

            var stringPairs = new List<StringPairs>();

            foreach (var iap in iaps.Data)
            {
                var price = pricePoints[iap];

                stringPairs.Add(new StringPairs { A = iap.Attributes.ProductId, B = $"{price?.PricePoint?.Attributes?.CustomerPrice} {price?.Currency}" });
                // Console.WriteLine($"{price?.TerritoryCode,5} {price?.PricePoint?.Attributes?.CustomerPrice,10} {price?.Currency,5} {iap.Attributes.ProductId,5}");

                if (printLocalPrices)
                {
                    var localPrices = localPricePoints[iap];
                    foreach (var item in localPrices)
                        stringPairs.Add(new StringPairs { A = $"    {item.Key}", B = $"{item.Value.PricePoint.Attributes.CustomerPrice} {item.Value.Currency}" });
                    // Console.WriteLine($"{item.Key,5} : {item.Value.PricePoint.Attributes.CustomerPrice,10} {item.Value.Currency,5}");
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
        Console.WriteLine("list [-l] [-v]");
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
            "-v",
            "Include detailed verbose output"
        );
    }

    public class InAppPriceData
    {
        public InAppPurchaseV2 Iap { get; set; }
        public InAppPurchasePricePoint PricePoint { get; set; }
        public string TerritoryCode { get; set; }
        public string Currency { get; set; }
    }

    public async Task<Dictionary<string, InAppPriceData>> GetAllLocalPricesAsync(InAppPurchaseV2 iap)
    {
        var verbose = Args.HasFlag("-v");
        var results = new Dictionary<string, InAppPriceData>();

        var iapApi = new InAppPurchasesApi(Service);
        var pointsApi = new InAppPurchasePricePointsApi(Service);
        var schedulesApi = new InAppPurchasePriceSchedulesApi(Service);

        Console.WriteLine($"   -> Getting full price list for {iap.Attributes.Name}...");

        var scheduleResponse = await iapApi.InAppPurchasesV2IapPriceScheduleGetToOneRelatedAsync(
            iap.Id,
            include: new List<string> { "manualPrices", "automaticPrices", "baseTerritory" },
            limitAutomaticPrices: 50,
            limitManualPrices: 50
        );

        if (scheduleResponse.Data == null)
        {
            if (verbose)
                Console.WriteLine("Error: Schedule Data is null.");
            return results;
        }

        var schedule = scheduleResponse.Data;

        var manualResponse = await schedulesApi.InAppPurchasePriceSchedulesManualPricesGetToManyRelatedAsync(
            schedule.Id,
            include: new List<string> { "inAppPurchasePricePoint", "territory" },
            limit: 200
        );

        var manualPricesData = ParsePricesAndCurrencies(manualResponse, iap);

        foreach (var item in manualPricesData)
            results[item.Key] = item.Value;

        if (verbose)
            Console.WriteLine($"Loaded {results.Count} manual overrides.");

        string? basePricePointId = (await GetBasePrice(iap))?.PricePoint.Id;

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
            var equalizationsResponse = await pointsApi.InAppPurchasePricePointsEqualizationsGetToManyRelatedAsync(
                basePricePointId,
                include: new List<string> { "territory" },
                limit: 200
            );

            if (equalizationsResponse.Data != null && equalizationsResponse.Included != null)
            {
                var currencyMap = ExtractCurrencyMap(equalizationsResponse.Included);

                foreach (var pricePoint in equalizationsResponse.Data)
                {
                    var territoryId = pricePoint.Relationships?.Territory?.Data?.Id;

                    if (territoryId != null)
                    {
                        if (!results.ContainsKey(territoryId))
                        {
                            currencyMap.TryGetValue(territoryId, out var currencyCode);

                            results[territoryId] = new InAppPriceData
                            {
                                Iap = iap,
                                PricePoint = pricePoint,
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

    private Dictionary<string, InAppPriceData> ParsePricesAndCurrencies(InAppPurchasePricesResponse response, InAppPurchaseV2 iap)
    {
        var res = new Dictionary<string, InAppPriceData>();

        if (response.Data == null || response.Included == null)
            return res;

        var pointsMap = response.Included
            .Select(x => x.ActualInstance)
            .OfType<InAppPurchasePricePoint>()
            .ToDictionary(p => p.Id);

        var currencyMap = ExtractCurrencyMap(response.Included);

        foreach (var entry in response.Data)
        {
            var territoryId = entry.Relationships?.Territory?.Data?.Id;
            var pointId = entry.Relationships?.InAppPurchasePricePoint?.Data?.Id;

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

    private Dictionary<string, string> ExtractCurrencyMap(List<Territory> includedList)
    {
        var map = new Dictionary<string, string>();

        var territories = includedList
            .OfType<Territory>();

        foreach (var t in territories)
            if (t.Id != null && t.Attributes?.Currency != null)
                map[t.Id] = t.Attributes.Currency;

        return map;
    }

    private Dictionary<string, string> ExtractCurrencyMap(List<InAppPurchasePricesResponseIncludedInner> includedList)
    {
        var map = new Dictionary<string, string>();
        var territories = includedList
            .Select(x => x.ActualInstance)
            .OfType<Territory>();

        foreach (var t in territories)
            if (t.Id != null && t.Attributes?.Currency != null)
                map[t.Id] = t.Attributes.Currency;

        return map;
    }

    public async Task<InAppPriceData?> GetBasePrice(InAppPurchaseV2 iap)
    {
        var v = Args.HasFlag("-v");

        Console.WriteLine($"   -> Fetching prices for: {iap.Attributes.Name}...");

        var iapApi = new InAppPurchasesApi(Service);
        var baseTerritory = Config.DefaultRegion;

        var scheduleResponse = await iapApi.InAppPurchasesV2IapPriceScheduleGetToOneRelatedAsync(iap.Id);

        if (scheduleResponse.Data == null)
        {
            if (v)
                Console.WriteLine("   -> No price schedule found.");
            return null;
        }

        var scheduleId = scheduleResponse.Data.Id;

        var schedulesApi = new InAppPurchasePriceSchedulesApi(Service);

        if (v)
            Console.WriteLine($"   -> Fetching prices for Schedule ID: {scheduleId}...");

        try
        {
            var pricesResponse = await schedulesApi.InAppPurchasePriceSchedulesManualPricesGetToManyRelatedAsync(
                scheduleId,
                filterTerritory: new List<string> { baseTerritory },
                include: new List<string> { "inAppPurchasePricePoint", "territory" }
            );

            if (pricesResponse.Included != null)
            {
                foreach (var item in pricesResponse.Included)
                {
                    if (item.ActualInstance is InAppPurchasePricePoint pricePoint)
                    {
                        if (v)
                        {
                            var price = pricePoint.Attributes.CustomerPrice;
                            var proceeds = pricePoint.Attributes.Proceeds;
                            Console.WriteLine($"   -> Price ({baseTerritory}): {price} (Proceeds: {proceeds})");
                        }

                        var currencies = ExtractCurrencyMap(pricesResponse.Included);

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
                if (pricesResponse.Data.Count == 0)
                    Console.WriteLine("   -> Price not set manually (might be Free).");
        }
        catch (ApiException ex)
        {
            if (v)
                Console.WriteLine($"   -> Price Fetch Error: {ex.Message}");
        }

        return null;
    }
}