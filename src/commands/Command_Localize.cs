using System.Globalization;
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

// FIXME: delete
// { DELETE THIS }

public class Command_Localize : CommandBase
{
    public override string CommandName => "localize";

    protected override async Task InternalExecuteAsync()
    {
        try
        {
            var v = Args.HasFlag("-v");

            var appId = CommandLinesUtils.GetParameter(Args, "--app-id", GlobalConfig.appId);
            var baseTerritory = Args.GetParameter("--base-territory", GlobalConfig.baseTerritory);
            var basePrices = await Args.LoadJson<IapBasePrices>("--default-prices", GlobalConfig.iapBasePricesConfigPath) ?? new();
            var localPercentages = await Args.LoadJson<IapLocalizedPercentages>("--local-percentages", GlobalConfig.localPricesPercentagesConfigPath) ?? new();

            var territories = await GetAllTerritoriesAsync();

            Console.WriteLine("receiving IAP list...");
            var appApi = new AppsApi(ApiConfig);
            var iaps = await appApi.AppsInAppPurchasesV2GetToManyRelatedAsync(appId);
            var iapsData = iaps.Data;

            //DELETE THIS
            iapsData = new() { iaps.Data[0] };
            territories = new() { new StoreTerritory("BGR", "") };
            //DELETE THIS



            // var lister = new Command_List();
            // lister.Initialize(ApiConfig, GlobalConfig, Args);
            // var prices = await lister.GetIapPrices(iaps.Data[0], verbose: v);
            // foreach (var pr in prices)
            //     Console.WriteLine($"{pr.Key} : {pr.Value.Attributes.CustomerPrice}");

            return;





            var pricesSetup = new List<IapPriceSetup>();

            // foreach iap
            // get all local prices
            // multiply on scaler
            // call 

            // var lister = new Command_List();
            // lister.Initialize(ApiConfig, GlobalConfig, Args);
            // lister.GetIapPrice();

            var restorer = new Command_Restore();
            restorer.Initialize(ApiConfig, GlobalConfig, Args);
            await restorer.SetPrices(pricesSetup, v);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public override void PrintHelp()
    {
        Console.WriteLine("localize");
        Console.WriteLine("    usage: localize [--app-id {your-app-id}] [--base-territory {territory-code}] [--default-prices {path-to-prices.json}] [--local-percentages.json] [-v]");
        Console.WriteLine("    for each iap (NOT subscriptions) set local prices as percentage of base territory price");
    }

    public record StoreTerritory(string Code, string Currency);

    //TODO: move to separate command Command_ListTerritories
    public async Task<List<StoreTerritory>> GetAllTerritoriesAsync()
    {
        var territoriesApi = new TerritoriesApi(ApiConfig);
        var allCodes = new List<StoreTerritory>();

        Console.WriteLine("loading supported territories ids...");

        //FIXME: what if more that 200?
        var response = await territoriesApi.TerritoriesGetCollectionAsync(limit: 200);

        foreach (var territory in response.Data)
        {
            // territory.Id - territory code (for example "USA", "UKR", "JPN")
            // territory.Attributes.Currency - currency code (for example "USD", "UAH", "JPY")

            allCodes.Add(new StoreTerritory
            (
                territory.Id,
                territory.Attributes.Currency
            ));
        }

        return allCodes;
    }

    private async Task LocalizeIap(InAppPurchaseV2 iap, string baseTerritory, IapBasePrices basePrices, IapLocalizedPercentages localPercentages, List<StoreTerritory> territories)
    {
        Console.WriteLine($"localizing iap: {iap.Attributes.ProductId}.");

        var manualPrices = new List<InAppPurchasePriceScheduleCreateRequestIncludedInner>();

        if (!basePrices.TryGetValue(iap.Attributes.ProductId, out var basePrice))
        {
            Console.WriteLine($"cant find base price for iap: {iap.Attributes.ProductId}. skipping.");
            return;
        }

        var basePoint = await GetClosestPricePointId(iap, baseTerritory, basePrice);
        manualPrices.Add(
            CreatePriceEntry(basePoint)
        );

        foreach (var territory in territories)
        {
            var territoryCode = territory.Code;
            var multiplier = localPercentages.TryGetValue(territoryCode, out var percentage) ? percentage : 1;

            Console.WriteLine($"Calculating price for {territoryCode} (Multiplier: {multiplier})...");

            var targetPrice = basePrice * multiplier;
            var localPoint = await GetClosestPricePointId(iap, territoryCode, targetPrice);

            if (localPoint != null)
            {
                manualPrices.Add(
                    CreatePriceEntry(localPoint)
                );

                if (Args.IsVerbose())
                    Console.WriteLine($" -> Set {territoryCode} to match ~{targetPrice}: CustomerPrice {localPoint?.Attributes?.CustomerPrice}");
            }
        }

        await PushNewSchedule(iap, baseTerritory, manualPrices);
    }

    public async Task<InAppPurchasePricePoint?> GetClosestPricePointId(InAppPurchaseV2 iap, string territory, double targetPrice)
    {
        var iapApi = new InAppPurchasesApi(ApiConfig);

        // Змінна для зберігання останньої перевіреної ціни (яка була < target)
        // Вона потрібна, щоб порівняти "попередню" і "поточну" ціну
        InAppPurchasePricePoint? lastLowerPoint = null;

        if (Args.IsVerbose())
            Console.WriteLine($"Starting search for closest price to {targetPrice} in {territory}...");

        // ========================================================================
        // 1. Перша сторінка (через SDK)
        // ========================================================================
        var response = await iapApi.InAppPurchasesV2PricePointsGetToManyRelatedAsync(
            iap.Id,
            filterTerritory: new List<string> { territory },
            limit: 200
        );

        // Перевіряємо першу сторінку
        var result = FindBestInPage(response.Data, targetPrice, lastLowerPoint);

        // Якщо знайшли ідеальний варіант (Result != null), повертаємо його
        if (result.FoundMatch != null)
        {
            if (Args.IsVerbose())
                Console.WriteLine($"Found: {result.FoundMatch.Attributes.CustomerPrice} (ID: {result.FoundMatch.Id})");
            return result.FoundMatch;
        }

        // Якщо не знайшли, запам'ятовуємо останню ціну цієї сторінки для наступної ітерації
        lastLowerPoint = result.LastSeen ?? lastLowerPoint;

        // Отримуємо посилання далі
        var nextHref = response.Links?.Next;
        var page = 1;

        // ========================================================================
        // 2. Цикл пагінації
        // ========================================================================
        while (!string.IsNullOrEmpty(nextHref))
        {
            page++;

            if (Args.IsVerbose())
                Console.WriteLine($"Fetching Page {page}...");

            try
            {
                var nextUri = new Uri(nextHref);
                var relativePath = nextUri.PathAndQuery;

                var requestOptions = new RequestOptions();

                // Вручну додаємо авторизацію для низькорівневого запиту
                if (!string.IsNullOrEmpty(iapApi.Configuration.AccessToken))
                {
                    requestOptions.HeaderParameters.Add("Authorization", "Bearer " + iapApi.Configuration.AccessToken);
                }

                var pageResponseWrapper = await iapApi.AsynchronousClient.GetAsync<InAppPurchasePricePointsResponse>(
                    relativePath,
                    requestOptions,
                    iapApi.Configuration
                );

                var pageResponse = pageResponseWrapper.Data;

                if (pageResponse?.Data != null)
                {
                    // Перевіряємо цю сторінку, передаючи останню ціну з попередньої
                    var pageResult = FindBestInPage(pageResponse.Data, targetPrice, lastLowerPoint);

                    if (pageResult.FoundMatch != null)
                    {
                        if (Args.IsVerbose())
                            Console.WriteLine($"Found on Page {page}: {pageResult.FoundMatch.Attributes.CustomerPrice} (ID: {pageResult.FoundMatch.Id})");
                        return pageResult.FoundMatch;
                    }

                    lastLowerPoint = pageResult.LastSeen ?? lastLowerPoint;
                }

                nextHref = pageResponse?.Links?.Next;
            }
            catch (Exception ex)
            {
                if (Args.IsVerbose())
                    Console.WriteLine($"Error fetching page {page}: {ex.Message}");
                break;
            }
        }

        // Якщо ми пройшли всі сторінки і не знайшли ціну >= target, 
        // значить target вище за найдорожчу ціну в App Store.
        // Повертаємо найдорожчу знайдену (lastLowerPoint).
        if (lastLowerPoint != null)
        {
            if (Args.IsVerbose())
                Console.WriteLine($"Target price is higher than max available. Returning max: {lastLowerPoint.Attributes.CustomerPrice}");
            return lastLowerPoint;
        }

        if (Args.IsVerbose())
            Console.WriteLine("Search finished. No price found.");
        return null;
    }

    // Допоміжний метод для пошуку всередині списку
    private (InAppPurchasePricePoint? FoundMatch, InAppPurchasePricePoint? LastSeen) FindBestInPage(
        List<InAppPurchasePricePoint> points,
        double target,
        InAppPurchasePricePoint? previousPageLastItem)
    {
        if (points == null || points.Count == 0)
            return (null, previousPageLastItem);

        InAppPurchasePricePoint? prev = previousPageLastItem;

        foreach (var current in points)
        {
            if (double.TryParse(current.Attributes.CustomerPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out double currentPrice))
            {
                // Якщо поточна ціна перевищила або дорівнює цілі -> ми знайшли точку перетину
                if (currentPrice >= target)
                {
                    // Якщо це найперший елемент взагалі (немає попереднього), то він і є найближчим
                    if (prev == null) return (current, current);

                    // Якщо є попередній, дивимось, хто ближче до цілі
                    if (double.TryParse(prev.Attributes.CustomerPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out double prevPrice))
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

    private InAppPurchasePriceScheduleCreateRequestIncludedInner CreatePriceEntry(InAppPurchasePricePoint pricePoint)
    {
        var pricePointRelData = new InAppPurchasePriceRelationshipsInAppPurchasePricePointData(
            type: InAppPurchasePriceRelationshipsInAppPurchasePricePointData.TypeEnum.InAppPurchasePricePoints,
            id: pricePoint.Id
        );
        var pricePointRel = new InAppPurchasePriceRelationshipsInAppPurchasePricePoint(
            data: pricePointRelData
        );
        var attributes = new InAppPurchasePriceInlineCreateAttributes(startDate: null);
        var relationships = new InAppPurchasePriceInlineCreateRelationships(
            inAppPurchasePricePoint: pricePointRel
        );
        var priceInlineCreate = new InAppPurchasePriceInlineCreate(
            type: InAppPurchasePriceInlineCreate.TypeEnum.InAppPurchasePrices,
            attributes: attributes,
            relationships: relationships
        )
        {
            Id = "${" + Guid.NewGuid().ToString() + "}"
        };
        return new InAppPurchasePriceScheduleCreateRequestIncludedInner(priceInlineCreate);
    }

    private async Task PushNewSchedule(InAppPurchaseV2 iap, string baseTerritoryId, List<InAppPurchasePriceScheduleCreateRequestIncludedInner> prices)
    {
        var schedulesApi = new InAppPurchasePriceSchedulesApi(ApiConfig);

        var relationships = new InAppPurchasePriceScheduleCreateRequestDataRelationships(
            inAppPurchase: new InAppPurchaseAppStoreReviewScreenshotCreateRequestDataRelationshipsInAppPurchaseV2(
                data: new(
                    id: iap.Id,
                    type: AppRelationshipsInAppPurchasesDataInner.TypeEnum.InAppPurchases
                )
            ),
            baseTerritory: new AppPriceScheduleCreateRequestDataRelationshipsBaseTerritory(
                data: new(
                    id: baseTerritoryId,
                    type: AppPricePointV3RelationshipsTerritoryData.TypeEnum.Territories
                )
            ),
            manualPrices: new InAppPurchasePriceScheduleCreateRequestDataRelationshipsManualPrices(
                data: prices.Select(p =>
                    new InAppPurchasePriceScheduleRelationshipsManualPricesDataInner(
                        type: InAppPurchasePriceScheduleRelationshipsManualPricesDataInner.TypeEnum.InAppPurchasePrices,
                        id: p.GetInAppPurchasePriceInlineCreate().Id
                    )
                ).ToList()
            )
        );

        // 2. Фінальний запит
        var request = new InAppPurchasePriceScheduleCreateRequest(
            data: new InAppPurchasePriceScheduleCreateRequestData(
                type: InAppPurchasePriceScheduleCreateRequestData.TypeEnum.InAppPurchasePriceSchedules,
                relationships: relationships
            ),
            included: prices
        );

        Console.WriteLine("Sending Create Schedule Request...");

        try
        {
            var response = await schedulesApi.InAppPurchasePriceSchedulesCreateInstanceAsync(request);

            if (Args.IsVerbose())
            {
                Console.WriteLine($"[SUCCESS] Schedule created successfully!");
                Console.WriteLine($"   -> New Schedule ID: {response.Data.Id}");
                Console.WriteLine($"   -> Link: {response.Data.Links.Self}");

                if (response.Included != null)
                    Console.WriteLine($"   -> Included items count: {response.Included.Count}");
            }
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[API ERROR] {ex.Message}");
            Console.WriteLine($"Status: {ex.ErrorCode}");
            Console.WriteLine($"Response Body: {ex.ErrorContent}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
        }
    }
}