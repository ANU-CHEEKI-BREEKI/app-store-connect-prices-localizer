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

            // restore prices first
            var restorer = new Command_Restore();
            restorer.Initialize(ApiConfig, GlobalConfig, Args);
            await restorer.RestorePrices(appId, baseTerritory, basePrices, v);

            Console.WriteLine("   -> Localizing IAPs...");
            Console.WriteLine("   -> Receiving IAP list...");

            var appApi = new AppsApi(ApiConfig);
            var iaps = await appApi.AppsInAppPurchasesV2GetToManyRelatedAsync(appId);
            var iapsData = iaps.Data;

            // using to get local prices
            var listCommand = new Command_List();
            listCommand.Initialize(ApiConfig, GlobalConfig, Args);

            var pricesSetup = new List<IapPriceSetup>();

            foreach (var item in iapsData)
                await LocalizePrises(item, listCommand, pricesSetup, localPercentages, v);

            await restorer.SetPrices(pricesSetup, v);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task LocalizePrises(InAppPurchaseV2 iap, Command_List listCommand, List<IapPriceSetup> pricesSetup, IapLocalizedPercentages localPercentages, bool v)
    {
        var basePice = await listCommand.GetBasePrice(iap);
        var prices = await listCommand.GetAllLocalPricesAsync(iap);

        var priceSetup = new IapPriceSetup()
        {
            Iap = iap,
            BasePrice = double.Parse(basePice.PricePoint.Attributes.CustomerPrice, CultureInfo.InvariantCulture),
            BaseTerritoryCode = basePice.TerritoryCode,
            LocalPrices = new()
        };
        pricesSetup.Add(priceSetup);

        Console.WriteLine($"   -> Localizing iap: {iap.Attributes.ProductId}.");

        foreach (var pr in prices)
        {
            var multiplier = localPercentages.TryGetValue(pr.Value.TerritoryCode, out var percentage) ? percentage : 1;

            var newPrice = double.Parse(
                pr.Value.PricePoint.Attributes.CustomerPrice, CultureInfo.InvariantCulture
            ) * multiplier;
            priceSetup.LocalPrices[pr.Value.TerritoryCode] = newPrice;

            if (v)
            {
                Console.WriteLine($"Calculating price for {pr.Value.TerritoryCode}...");
                Console.WriteLine($"{pr.Value.PricePoint.Attributes.CustomerPrice} * {multiplier} = {newPrice:##.00}");
            }
        }
    }

    public override void PrintHelp()
    {
        Console.WriteLine("localize");
        Console.WriteLine("    usage: localize [--app-id {your-app-id}] [--base-territory {territory-code}] [--default-prices {path-to-prices.json}] [--local-percentages {local-percentages.json}] [-v]");
        Console.WriteLine("    for each iap (NOT subscriptions) set local prices as percentage of base territory price");
        Console.WriteLine("    --default-prices  path to json with default prices for base territory");
        Console.WriteLine("        used to run 'restore' command internally to reset all prices and get valid localized prices as 100% from base price");
        Console.WriteLine("    --local-percentages  path to json with percentages for each country");
        Console.WriteLine("        will set local prices as basePrice * percentage");
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
}