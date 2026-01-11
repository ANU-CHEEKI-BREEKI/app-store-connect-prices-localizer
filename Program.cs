using AppStoreConnect.Net.Client;

var commands = new CommandsCollection()
{
    new Command_List(),
    // new Command_Localize(),
    new Command_Restore(),
};

if (commands.TryPrintHelp(args))
    return;

var command = commands.FirstOrDefault(c => Array.IndexOf(args, c.Name) == 0);
if (command is null)
{
    Console.WriteLine("no command fount for passed parameters");
    return;
}

if (args.HasFlag("-h")
    || args.HasFlag("--help"))
{
    Console.WriteLine();
    Console.WriteLine();
    command.PrintHelp();
    Console.WriteLine();
    Console.WriteLine();
    return;
}

var resolvedPathGetter = new CommandLinesUtils.ResolvedPathGetter();
var configPath = args.TryGetOption("--config", "../config.json");

var verbose = args.HasFlag("-v");

var config = await CommandLinesUtils.LoadJson<Config>(
    configPath,
    Path.Combine(configPath, "config.json"),
    verbose,
    resolvedPathGetter
);

if (config is null)
    config = new();


// patch paths to be relative to config file
var absoluteConfigPath = Path.GetFullPath(resolvedPathGetter.ResolvedPath);
var configDirectory = Path.GetDirectoryName(absoluteConfigPath);

config.PrivateKeyFilePath = Path.Combine(configDirectory, config.PrivateKeyFilePath);
config.DefaultPricesFilePath = Path.Combine(configDirectory, config.DefaultPricesFilePath);
config.LocalizedPricesTemplateFilePath = Path.Combine(configDirectory, config.LocalizedPricesTemplateFilePath);


// patch config with explicit command line options
config.AppId = args.TryGetOption("--app-id", config.AppId);
config.PrivateKeyFilePath = args.TryGetOption("--private-key", config.PrivateKeyFilePath);
config.DefaultPricesFilePath = args.TryGetOption("--prices", config.DefaultPricesFilePath);

config.LocalizedPricesTemplateFilePath = args.TryGetOption("--localized-template", config.LocalizedPricesTemplateFilePath);

config.DefaultRegion = args.TryGetOption("--region", config.DefaultRegion);
config.Iap = args.TryGetOption("--iap", config.Iap);
// config.DefaultCurrency = args.TryGetOption("--currency", config.DefaultCurrency);


var service = new AppStoreConnectConfiguration(
    config.KeyId,
    config.IssuerId,
    await File.ReadAllTextAsync(config.PrivateKeyFilePath)
);

Console.WriteLine();

command.Initialize(service, config, args);
await command.ExecuteAsync();

Console.WriteLine();
Console.WriteLine("done.");

// baseTerritory: "USA",
// iapBasePricesConfigPath: "../default-prices-usd.json",
// localPricesPercentagesConfigPath: "./configs/localized-prices-template.json"