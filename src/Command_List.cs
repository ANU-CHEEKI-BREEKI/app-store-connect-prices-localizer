
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

public class Command_List : CommandBase
{
    protected override async Task InternalExecuteAsync()
    {
        try
        {
            var printLocalPrices = Args.Contains("-l");

            Console.WriteLine("receiving IAP list...");

            var appId = Args[Array.IndexOf(Args, "--app-id") + 1];
            var appApi = new AppsApi(Config);

            var iaps = await appApi.AppsInAppPurchasesV2GetToManyRelatedAsync(appId);

            await PrintIapPrice(iaps.Data[0]);

            // foreach (var iap in iaps.Data)
            // {
            //     Console.WriteLine($"IAP Name: {iap.Attributes.Name}");
            //     Console.WriteLine($"Product ID: {iap.Attributes.ProductId}");
            //     Console.WriteLine($"Status: {iap.Attributes.State}");
            //     Console.WriteLine($"Status: {iap.Attributes.ContentHosting}");
            // }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public override bool IsMatches(string[] args) => args.Contains("--list");
    public override void PrintHelp()
    {
        Console.WriteLine("list");
        Console.WriteLine("    usage: --list --app-id <your app id> [-l]");
        Console.WriteLine("    list all IAP in project (NOT subscriptions)");
        Console.WriteLine("    -l  print local prices");
    }

    private async Task PrintIapPrice(InAppPurchaseV2 iap)
    {
        var iapApi = new InAppPurchasesApi(Config);
        Console.WriteLine($"Getting price for {iap.Attributes.Name}...");
        var scheduleResponse = await iapApi.InAppPurchasesV2IapPriceScheduleGetToOneRelatedAsync(iap.Id);

        if (scheduleResponse.Data == null)
        {
            Console.WriteLine("   -> No price schedule found.");
            return;
        }

        var scheduleId = scheduleResponse.Data.Id;

        var schedulesApi = new InAppPurchasePriceSchedulesApi(Config);

        Console.WriteLine($"   -> Fetching prices for Schedule ID: {scheduleId}...");

        try
        {
            var pricesResponse = await schedulesApi.InAppPurchasePriceSchedulesManualPricesGetToManyRelatedAsync(
                scheduleId,
                filterTerritory: new List<string> { "USA" },
                include: new List<string> { "inAppPurchasePricePoint" }
            );

            if (pricesResponse.Included != null)
            {
                foreach (var item in pricesResponse.Included)
                {
                    if (item.ActualInstance is InAppPurchasePricePoint pricePoint)
                    {
                        var price = pricePoint.Attributes.CustomerPrice;
                        var proceeds = pricePoint.Attributes.Proceeds;

                        Console.WriteLine($"   -> Price (USA): {price} (Proceeds: {proceeds})");
                    }
                }
            }

            if (pricesResponse.Data.Count == 0)
                Console.WriteLine("   -> Price not set manually (might be Free).");
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"   -> Price Fetch Error: {ex.Message}");
        }
    }
}