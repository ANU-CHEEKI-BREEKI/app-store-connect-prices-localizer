
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

public class Command_List : CommandBase
{
    public class TerritoryPrice : Dictionary<string, InAppPurchasePricePoint> { }

    public override string CommandName => "list";

    protected override async Task InternalExecuteAsync()
    {
        try
        {
            var verbose = Args.HasFlag("-v");

            Console.WriteLine("receiving IAP list...");

            var appId = CommandLinesUtils.GetParameter(Args, "--app-id", GlobalConfig.appId);
            var baseTerritory = Args.GetParameter("--base-territory", GlobalConfig.baseTerritory);

            var appApi = new AppsApi(ApiConfig);

            var pricePoints = new Dictionary<InAppPurchaseV2, InAppPurchasePricePoint?>();
            var iaps = await appApi.AppsInAppPurchasesV2GetToManyRelatedAsync(appId);

            if (Args.HasFlag("-p"))
            {
                Console.WriteLine($"   -> Fetching prices...");

                foreach (var iap in iaps.Data)
                    pricePoints[iap] = await GetBasePrice(iap);

                Console.WriteLine($"Customer Prices for {baseTerritory}:");

                foreach (var iap in iaps.Data)
                {
                    var price = pricePoints[iap];
                    Console.WriteLine($"{iap.Attributes.ProductId}: {price?.Attributes?.CustomerPrice}");
                }
            }
            else
            {
                foreach (var iap in iaps.Data)
                    Console.WriteLine($"{iap.Attributes.ProductId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public override void PrintHelp()
    {
        Console.WriteLine("list");
        Console.WriteLine("    usage: list [--app-id {your-app-id}] [--base-territory {territory-code}] [-v] [-p] [-l]");
        Console.WriteLine("    list all IAP in project (NOT subscriptions)");
        Console.WriteLine("    -p  print base prices");
        Console.WriteLine("    -p  print localized prices");
        Console.WriteLine("    -v  verbose logs");
    }

    private async Task<InAppPurchasePricePoint?> GetBasePrice(InAppPurchaseV2 iap)
    {
        var v = Args.HasFlag("-v");

        if (v)
            Console.WriteLine($"   -> Fetching prices for: {iap.Attributes.Name}...");

        var iapApi = new InAppPurchasesApi(ApiConfig);
        var baseTerritory = Args.GetParameter("--base-territory", GlobalConfig.baseTerritory);

        var scheduleResponse = await iapApi.InAppPurchasesV2IapPriceScheduleGetToOneRelatedAsync(iap.Id);

        if (scheduleResponse.Data == null)
        {
            if (v)
                Console.WriteLine("   -> No price schedule found.");
            return null;
        }

        var scheduleId = scheduleResponse.Data.Id;

        var schedulesApi = new InAppPurchasePriceSchedulesApi(ApiConfig);

        if (v)
            Console.WriteLine($"   -> Fetching prices for Schedule ID: {scheduleId}...");

        try
        {
            var pricesResponse = await schedulesApi.InAppPurchasePriceSchedulesManualPricesGetToManyRelatedAsync(
                scheduleId,
                filterTerritory: new List<string> { baseTerritory },
                include: new List<string> { "inAppPurchasePricePoint" }
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

                        return pricePoint;
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

    public async Task<Dictionary<string, InAppPurchasePricePoint>> GetAllLocalPricesAsync(InAppPurchaseV2 iap)
    {
        var results = new Dictionary<string, InAppPurchasePricePoint>();
        var iapApi = new InAppPurchasesApi(ApiConfig);
        var pointsApi = new InAppPurchasePricePointsApi(ApiConfig);

        Console.WriteLine($"Getting full price list for {iap.Attributes.Name}...");

        var scheduleResponse = await iapApi.InAppPurchasesV2IapPriceScheduleGetToOneRelatedAsync(
            iap.Id,
            include: new List<string> { "manualPrices", "automaticPrices", "baseTerritory" }
        );

        if (scheduleResponse.Data == null)
        {
            Console.WriteLine("Error: Schedule Data is null.");
            return results;
        }

        var schedule = scheduleResponse.Data;

        var manualResponse = await new InAppPurchasePriceSchedulesApi(ApiConfig).InAppPurchasePriceSchedulesManualPricesGetToManyRelatedAsync(
            schedule.Id,
            include: new List<string> { "inAppPurchasePricePoint", "territory" },
            limit: 200
        );

        var manualPricesMap = ParsePricesWithTerritory(manualResponse);
        foreach (var kvp in manualPricesMap)
            results[kvp.Key] = kvp.Value;

        Console.WriteLine($"Loaded {results.Count} manual overrides.");


        string? basePricePointId = (await GetBasePrice(iap)).Id;

        if (basePricePointId == null)
        {
            Console.WriteLine("Warning: Could not determine Base Price Point ID. Cannot fetch equalizations.");
            return results;
        }

        Console.WriteLine($"Fetching equalizations (world prices) for Point ID: {basePricePointId}...");

        try
        {
            var equalizationsResponse = await pointsApi.InAppPurchasePricePointsEqualizationsGetToManyRelatedAsync(
                basePricePointId,
                include: new List<string> { "territory" },
                limit: 200
            );

            if (equalizationsResponse.Data != null && equalizationsResponse.Included != null)
            {
                foreach (var pricePoint in equalizationsResponse.Data)
                {
                    var territoryId = pricePoint.Relationships?.Territory?.Data?.Id;
                    var price = pricePoint.Attributes.CustomerPrice;

                    if (territoryId != null)
                    {
                        if (!results.ContainsKey(territoryId))
                            results[territoryId] = pricePoint;
                    }
                }
                Console.WriteLine($"Added {equalizationsResponse.Data.Count} automatic prices.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching equalizations: {ex.Message}");
        }

        return results;
    }

    private Dictionary<string, InAppPurchasePricePoint> ParsePricesWithTerritory(InAppPurchasePricesResponse response)
    {
        var res = new Dictionary<string, InAppPurchasePricePoint>();
        if (response.Data == null || response.Included == null) return res;

        var pointsMap = response.Included
            .Select(x => x.ActualInstance)
            .OfType<InAppPurchasePricePoint>()
            .ToDictionary(p => p.Id);

        foreach (var entry in response.Data)
        {
            var territoryId = entry.Relationships?.Territory?.Data?.Id;
            var pointId = entry.Relationships?.InAppPurchasePricePoint?.Data?.Id;

            if (territoryId != null && pointId != null && pointsMap.TryGetValue(pointId, out var point))
                res[territoryId] = point;
        }
        return res;
    }
}