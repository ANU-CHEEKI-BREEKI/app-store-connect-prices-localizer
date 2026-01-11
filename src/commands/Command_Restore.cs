using System.Globalization;
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

public class IapPriceSetup
{
    /// <summary>
    /// whole class here to not be confused what is iap id, product id, product name, etc
    /// get InAppPurchaseV2 instances from AppStoreConnect Api
    /// </summary>
    public InAppPurchaseV2 Iap;
    public double BasePrice;
    public string BaseTerritoryCode;
    public PricePerTerritory LocalPrices = new();
}

public class PricePerTerritory : Dictionary<string, double> { }

public class Command_Restore : CommandBase
{
    public override string Name => "restore";
    public override string Description => "Recalculates prices for all regions based on the default currency price provided in your JSON config.";

    public override void PrintHelp()
    {
        Console.WriteLine("restore [--prices <path-to-default-prices.json>] [-v] [-l]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);

        Console.WriteLine();
        Console.WriteLine("options:");

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
        await RestorePrices();

        // print what we set at the end
        var listCommand = new Command_List();
        listCommand.Initialize(Service, Config, Args);
        await listCommand.ExecuteAsync();
    }

    /// <summary>
    /// set default prices
    /// </summary>
    private async Task RestorePrices()
    {
        var basePrices = await CommandLinesUtils.LoadJson<ProductConfigs>(Config.DefaultPricesFilePath, "../default-prices-usd.json", Args.HasFlag("-v")) ?? new();

        var verbose = Args.HasFlag("-v");
        try
        {
            Console.WriteLine("   -> Restoring IAP Prices...");
            Console.WriteLine("   -> Receiving IAP list...");
            var appApi = new AppsApi(Service);
            var iaps = await appApi.AppsInAppPurchasesV2GetToManyRelatedAsync(Config.AppId);

            var singeIap = Config.Iap;

            if (!string.IsNullOrEmpty(singeIap))
                iaps.Data = iaps.Data.Where(p => p.Attributes.ProductId == singeIap).ToList();

            // for each iap on server - just set default price
            var iapPrices = new List<IapPriceSetup>();
            foreach (var iap in iaps.Data)
            {
                if (!basePrices.TryGetValue(iap.Attributes.ProductId, out var basePrice))
                    continue;

                // forcibly adjust price if it is a whole number
                // to make sure we have marketable price
                // AUTOMATICALLY how Google Play Console does
                if (basePrice == Math.Truncate(basePrice))
                    basePrice -= 0.01m;

                iapPrices.Add(new IapPriceSetup
                {
                    Iap = iap,
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
    }

    /// <summary>
    /// Set concrete prices
    /// </summary>
    public async Task SetPrices(List<IapPriceSetup> iapPrices, bool verbose)
    {
        Console.WriteLine("   -> Settings IAP Prices...");
        try
        {
            foreach (var iap in iapPrices)
                await SetPrices(iap, verbose);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task SetPrices(IapPriceSetup iapSettings, bool verbose)
    {
        Console.WriteLine($"   -> Prepare iap price for IAP: {iapSettings.Iap.Attributes.ProductId}.");

        var manualPrices = new List<InAppPurchasePriceScheduleCreateRequestIncludedInner>();

        Console.WriteLine($"   -> Prepare iap price for territory: {iapSettings.BaseTerritoryCode}.");

        var basePoint = await GetClosestPricePointId(iapSettings.Iap, iapSettings.BaseTerritoryCode, iapSettings.BasePrice, verbose);
        manualPrices.Add(
            CreatePriceEntry(basePoint)
        );

        foreach (var territory in iapSettings.LocalPrices)
        {
            // already set base price
            if (territory.Key == iapSettings.BaseTerritoryCode)
                continue;

            Console.WriteLine($"   -> Prepare iap price for territory: {territory.Key}.");

            var territoryCode = territory.Key;
            var targetPrice = territory.Value;

            var localPoint = await GetClosestPricePointId(iapSettings.Iap, territoryCode, targetPrice, verbose);

            if (localPoint != null)
            {
                manualPrices.Add(
                    CreatePriceEntry(localPoint)
                );

                if (verbose)
                    Console.WriteLine($" -> Set {territoryCode} to CustomerPrice: {localPoint?.Attributes?.CustomerPrice}");
            }
        }

        await PushNewSchedule(iapSettings.Iap, iapSettings.BaseTerritoryCode, manualPrices, verbose);
    }

    public async Task<InAppPurchasePricePoint?> GetClosestPricePointId(InAppPurchaseV2 iap, string territory, double targetPrice, bool verbose)
    {
        var iapApi = new InAppPurchasesApi(Service);

        InAppPurchasePricePoint? lastLowerPoint = null;

        if (verbose)
            Console.WriteLine($"Starting search for closest price to {targetPrice} in {territory}...");

        var response = await iapApi.InAppPurchasesV2PricePointsGetToManyRelatedAsync(
            iap.Id,
            filterTerritory: new List<string> { territory },
            limit: 200
        );

        var result = FindBestInPage(response.Data, targetPrice, lastLowerPoint);

        if (result.FoundMatch != null)
        {
            if (verbose)
                Console.WriteLine($"Found: {result.FoundMatch.Attributes.CustomerPrice} (ID: {result.FoundMatch.Id})");
            return result.FoundMatch;
        }

        lastLowerPoint = result.LastSeen ?? lastLowerPoint;

        var nextHref = response.Links?.Next;
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

                var requestOptions = new RequestOptions();

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
                    var pageResult = FindBestInPage(pageResponse.Data, targetPrice, lastLowerPoint);

                    if (pageResult.FoundMatch != null)
                    {
                        if (verbose)
                            Console.WriteLine($"Found on Page {page}: {pageResult.FoundMatch.Attributes.CustomerPrice} (ID: {pageResult.FoundMatch.Id})");
                        return pageResult.FoundMatch;
                    }

                    lastLowerPoint = pageResult.LastSeen ?? lastLowerPoint;
                }

                nextHref = pageResponse?.Links?.Next;
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
                Console.WriteLine($"Target price is higher than max available. Returning max: {lastLowerPoint.Attributes.CustomerPrice}");
            return lastLowerPoint;
        }

        if (verbose)
            Console.WriteLine("Search finished. No price found.");
        return null;
    }

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

    private async Task PushNewSchedule(InAppPurchaseV2 iap, string baseTerritoryId, List<InAppPurchasePriceScheduleCreateRequestIncludedInner> prices, bool verbose)
    {
        var schedulesApi = new InAppPurchasePriceSchedulesApi(Service);

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

        var request = new InAppPurchasePriceScheduleCreateRequest(
            data: new InAppPurchasePriceScheduleCreateRequestData(
                type: InAppPurchasePriceScheduleCreateRequestData.TypeEnum.InAppPurchasePriceSchedules,
                relationships: relationships
            ),
            included: prices
        );

        Console.WriteLine($"Sending Create Schedule Request for {iap.Attributes.ProductId} ...");

        try
        {
            var response = await schedulesApi.InAppPurchasePriceSchedulesCreateInstanceAsync(request);

            if (verbose)
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