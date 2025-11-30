
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

public class Command_List : CommandBase
{
    public override string CommandName => "list";

    protected override async Task InternalExecuteAsync()
    {
        try
        {
            var v = Args.HasFlag("-v");

            Console.WriteLine("receiving IAP list...");

            var appId = CommandLinesUtils.GetParameter(Args, "--app-id", GlobalConfig.appId);
            var appApi = new AppsApi(ApiConfig);

            var pricePoints = new Dictionary<InAppPurchaseV2, InAppPurchasePricePoint?>();
            var iaps = await appApi.AppsInAppPurchasesV2GetToManyRelatedAsync(appId);

            Console.WriteLine($"   -> Fetching prices...");

            foreach (var iap in iaps.Data)
                pricePoints[iap] = await PrintIapPrice(iap);

            var baseTerritory = Args.GetParameter("--base-territory", GlobalConfig.baseTerritory) ?? "USA";
            Console.WriteLine($"Customer Prices for {baseTerritory}:");

            foreach (var iap in iaps.Data)
            {
                var price = pricePoints[iap];
                Console.WriteLine($"{iap.Attributes.ProductId}: {price?.Attributes?.CustomerPrice}");
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
        Console.WriteLine("    usage: list [--app-id {your-app-id}] [--base-territory {territory-code}] [-v]");
        Console.WriteLine("    list all IAP in project (NOT subscriptions)");
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
}