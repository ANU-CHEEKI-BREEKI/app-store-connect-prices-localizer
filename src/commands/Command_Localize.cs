
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

public class Command_Localize : CommandBase
{
    public override string CommandName => "localize";

    protected override async Task InternalExecuteAsync()
    {
        try
        {
            var v = Args.HasFlag("-v");

            Console.WriteLine("receiving IAP list...");

            var appId = CommandLinesUtils.GetParameter(Args, "--app-id", GlobalConfig.appId);
            var baseTerritory = Args.GetParameter("--base-territory", GlobalConfig.baseTerritory);
            var basePrices = await Args.LoadJson<IapBasePrices>("--default-prices", GlobalConfig.iapBasePricesConfigPath);
            var localPercentages = await Args.LoadJson<IapLocalizedPercentages>("--local-percentages", GlobalConfig.localPricesPercentagesConfigPath);

            var appApi = new AppsApi(ApiConfig);

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
}