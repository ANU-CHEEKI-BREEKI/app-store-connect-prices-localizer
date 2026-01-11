using System.Globalization;
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Model;

public class Command_Localize : CommandBase
{
    protected override async Task InternalExecuteAsync()
    {
        try
        {
            var v = Args.HasFlag("-v");

            var appId = Config.AppId;
            var baseTerritory = Config.DefaultRegion;
            var basePrices = await CommandLinesUtils.LoadJson<ProductConfigs>(Config.DefaultPricesFilePath, "../default-prices-usd.json", Args.HasFlag("-v")) ?? new();
            var localPercentages = await CommandLinesUtils.LoadJson<LocalizedPricesPercentagesConfigs>(Config.LocalizedPricesTemplateFilePath, "./configs/localized-prices-template.json", Args.HasFlag("-v")) ?? new();

            // restore prices first
            var restorer = new Command_Restore();
            restorer.Initialize(Service, Config, Args);
            await restorer.ExecuteAsync();

            Console.WriteLine("   -> Localizing IAPs...");
            Console.WriteLine("   -> Receiving IAP list...");

            var appApi = new AppsApi(Service);
            var iaps = await appApi.AppsInAppPurchasesV2GetToManyRelatedAsync(appId);

            var singeIap = Config.Iap;
            if (!string.IsNullOrEmpty(singeIap))
                iaps.Data = iaps.Data.Where(p => p.Attributes.ProductId == singeIap).ToList();

            // using to get local prices
            var listCommand = new Command_List();
            listCommand.Initialize(Service, Config, Args);

            var pricesSetup = new List<IapPriceSetup>();

            foreach (var item in iaps.Data)
                await LocalizePrises(item, listCommand, pricesSetup, localPercentages, v);

            await restorer.SetPrices(pricesSetup, v);

            // print what we set at the end        
            await listCommand.ExecuteAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task LocalizePrises(InAppPurchaseV2 iap, Command_List listCommand, List<IapPriceSetup> pricesSetup, LocalizedPricesPercentagesConfigs localPercentages, bool v)
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
            var multiplier = localPercentages.TryGetValue(pr.Value.TerritoryCode, out var percentage) ? percentage : 1m;

            var newPrice = decimal.Parse(
                pr.Value.PricePoint.Attributes.CustomerPrice, CultureInfo.InvariantCulture
            ) * multiplier;

            // make more like marketing price 5.00 -> 4.99 and hope it will be rounded as price point 4.99
            if (Math.Truncate(newPrice) == newPrice)
                newPrice -= 0.01m;

            priceSetup.LocalPrices[pr.Value.TerritoryCode] = (double)newPrice;

            if (v)
                Console.WriteLine($"Calculating price for {pr.Value.TerritoryCode}: {pr.Value.PricePoint.Attributes.CustomerPrice,10} * {multiplier,3} - 0.01 = {newPrice,10:##.00}");
        }
    }

    public override string Name => "localize";
    public override string Description => "Recalculates prices for all regions based on the default currency price provided in your JSON config and localized prices template.";

    public override void PrintHelp()
    {
        Console.WriteLine("localize [--prices <path-to-default-prices.json>] [--localized-template <path-to-localized-template.json>] [-v] [-l]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--prices <path>",
            "Specifies path to json with default prices in default currency. If not specified, used path from global config json."
        );
        CommandLinesUtils.PrintOption(
            "--localized-template <path>",
            "Specifies path to json with percentages for each region that needs to be localized. Default path is: ./configs/localized-prices-template.json"
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

    public record StoreTerritory(string Code, string Currency);

    //TODO: move to separate command Command_ListTerritories
    public async Task<List<StoreTerritory>> GetAllTerritoriesAsync()
    {
        var territoriesApi = new TerritoriesApi(Service);
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