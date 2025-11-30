
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
                    pricePoints[iap] = await PrintIapPrice(iap);

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
        Console.WriteLine("    usage: list [--app-id {your-app-id}] [--base-territory {territory-code}] [-v] [-p]");
        Console.WriteLine("    list all IAP in project (NOT subscriptions)");
        Console.WriteLine("    -p  print base prices");
        Console.WriteLine("    -v  verbose logs");
    }

    private async Task<InAppPurchasePricePoint?> PrintIapPrice(InAppPurchaseV2 iap)
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


    // public async Task<Dictionary<string, string>> GetAllLocalPricesAsync(InAppPurchaseV2 iap)
    // {
    //     var results = new Dictionary<string, string>(); // TerritoryCode -> CustomerPrice
    //     var iapApi = new InAppPurchasesApi(ApiConfig);
    //     var schedulesApi = new InAppPurchasePriceSchedulesApi(ApiConfig);
    //     var pointsApi = new InAppPurchasePricePointsApi(ApiConfig); // <--- Новий API для точок

    //     Console.WriteLine($"Getting full price list for {iap.Attributes.Name}...");

    //     // 1. Отримуємо Розклад + Manual Prices + Automatic Prices
    //     var scheduleResponse = await iapApi.InAppPurchasesV2IapPriceScheduleGetToOneRelatedAsync(
    //         iap.Id,
    //         include: new List<string> { "manualPrices", "automaticPrices", "automaticPrices.inAppPurchasePricePoint" }
    //     );

    //     if (scheduleResponse.Data == null) return results;

    //     var scheduleId = scheduleResponse.Data.Id;

    //     // --- КРОК 1: Збираємо Manual Prices (Винятки) ---
    //     // Нам треба знати територію для кожного manual price
    //     var manualResponse = await schedulesApi.InAppPurchasePriceSchedulesManualPricesGetToManyRelatedAsync(
    //         scheduleId,
    //         include: new List<string> { "inAppPurchasePricePoint", "territory" },
    //         limit: 200
    //     );

    //     var manualPricesMap = ParsePricesWithTerritory(manualResponse);

    //     // Додаємо ручні ціни в результат
    //     foreach (var kvp in manualPricesMap)
    //     {
    //         results[kvp.Key] = kvp.Value; // Key=TerritoryCode, Value=Price
    //     }

    //     Console.WriteLine($"Loaded {results.Count} manual overrides.");

    //     // --- КРОК 2: Знаходимо Базову Ціну для Автоматики ---
    //     string? basePricePointId = null;

    //     // Шукаємо в included
    //     if (scheduleResponse.Included != null)
    //     {
    //         var autoPrice = scheduleResponse.Included
    //             .Select(x => x.ActualInstance)
    //             .OfType<InAppPurchasePrice>()
    //             .FirstOrDefault(p => p.Type == InAppPurchasePrice.TypeEnum.InAppPurchasePrices); // Це саме об'єкт ціни, не точки

    //         if (autoPrice != null && autoPrice.Relationships?.InAppPurchasePricePoint?.Data != null)
    //         {
    //             basePricePointId = autoPrice.Relationships.InAppPurchasePricePoint.Data.Id;
    //         }
    //     }

    //     if (basePricePointId == null)
    //     {
    //         Console.WriteLine("Warning: No base automatic price found.");
    //         return results;
    //     }

    //     // --- КРОК 3: Завантажуємо Еквіваленти (Equalizations) ---
    //     // Це дасть нам ціни для ВСІХ інших країн, які відповідають цьому Tier
    //     Console.WriteLine($"Fetching equalizations for Base Price Point ID: {basePricePointId}...");

    //     var equalizationsResponse = await pointsApi.InAppPurchasePricePointsEqualizationsGetToManyRelatedAsync(
    //         basePricePointId,
    //         include: new List<string> { "territory" }, // Обов'язково включаємо територію, щоб знати код країни
    //         limit: 200 // Завантажить до 200 країн (цього достатньо для світу)
    //     );

    //     // Парсимо відповідь еквалайзерів
    //     // (Логіка парсингу така ж, як і для Manual Prices: є список PricePoints і список Territories в Included)

    //     // 1. Словник територій з Included: ID -> Code (напр. "id-usa" -> "USA")
    //     var territoriesMap = equalizationsResponse.Included?
    //         .Select(x => x.ActualInstance)
    //         .OfType<Territory>()
    //         .ToDictionary(t => t.Id, t => t.Id); // У Territory ID співпадає з кодом (USA = USA), але краще брати з об'єкта

    //     // 2. Проходимо по списку Price Points (Data)
    //     if (equalizationsResponse.Data != null && territoriesMap != null)
    //     {
    //         foreach (var pricePoint in equalizationsResponse.Data)
    //         {
    //             // У pricePoint має бути зв'язок з територією
    //             if (pricePoint.Relationships?.Territory?.Data != null)
    //             {
    //                 var territoryId = pricePoint.Relationships.Territory.Data.Id;
    //                 var price = pricePoint.Attributes.CustomerPrice;

    //                 // ДОДАЄМО ТІЛЬКИ ЯКЩО НЕМАЄ РУЧНОЇ ЦІНИ
    //                 // (Manual override має пріоритет)
    //                 if (!results.ContainsKey(territoryId))
    //                 {
    //                     results[territoryId] = price;
    //                 }
    //             }
    //         }
    //     }

    //     return results;
    // }

    // // Допоміжний метод для парсингу відповідей, де є Price + Included Territory + Included PricePoint
    // private Dictionary<string, string> ParsePricesWithTerritory(InAppPurchasePricesResponse response)
    // {
    //     var res = new Dictionary<string, string>();
    //     if (response.Data == null || response.Included == null) return res;

    //     // 1. Знаходимо самі цифри цін (PricePoints)
    //     var pointsMap = response.Included
    //         .Select(x => x.ActualInstance)
    //         .OfType<InAppPurchasePricePoint>()
    //         .ToDictionary(p => p.Id);

    //     // 2. Проходимо по зв'язках (Price Entries)
    //     foreach (var entry in response.Data)
    //     {
    //         var territoryId = entry.Relationships?.Territory?.Data?.Id;
    //         var pointId = entry.Relationships?.InAppPurchasePricePoint?.Data?.Id;

    //         if (territoryId != null && pointId != null && pointsMap.TryGetValue(pointId, out var point))
    //         {
    //             res[territoryId] = point.Attributes.CustomerPrice;
    //         }
    //     }
    //     return res;
    // }
}