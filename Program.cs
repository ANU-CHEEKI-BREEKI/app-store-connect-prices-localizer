using AppStoreConnect.Net.Client;

var commands = new CommandsCollection()
{
    new Command_List(),
    new Command_Localize(),
    new Command_Restore(),
    new Command_ExportIaps(),
    new Command_CreateIaps(),
    new Command_ExportMetadata(),
    new Command_ImportMetadata(),
    new Command_CopyPromoText(),
    new Command_CreateAllLocales(),
    new Command_ListScreenshots(),
    new Command_ExportScreenshots(),
    new Command_ImportScreenshots(),
    new Command_CopyUrls(),
    new Command_Config(),
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

var verbose = args.HasFlag("-v");

if (!command.NeedsConfig)
{
    command.Initialize(null, new Config(), args);
    await command.ExecuteAsync();
    return;
}

var resolvedPathGetter = new CommandLinesUtils.ResolvedPathGetter();

string configPath;
try
{
    configPath = Profiles.ResolveConfigPath(args, out var configSource);
    if (verbose)
        Console.WriteLine($"config: {configPath} ({configSource})");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"[ERROR] {ex.Message}");
    return;
}

if (!File.Exists(configPath) && !File.Exists(Path.Combine(configPath, "config.json")))
{
    Console.WriteLine($"[ERROR] config not found: {Path.GetFullPath(configPath)}");
    Console.WriteLine("        pass --config <path>, or register a profile once: config add <name> <path-to-config.json>");
    return;
}

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

config.ConfigDirectory = configDirectory;
config.PrivateKeyFilePath = Path.Combine(configDirectory, config.PrivateKeyFilePath);
config.DefaultPricesFilePath = Path.Combine(configDirectory, config.DefaultPricesFilePath);
config.LocalizedPricesTemplateFilePath = Path.Combine(configDirectory, config.LocalizedPricesTemplateFilePath);
config.ProductDefinitionsFilePath = Path.Combine(configDirectory, config.ProductDefinitionsFilePath);

// an unset metadata path has to stay unset, so the command can fall back to the config directory itself
if (!string.IsNullOrWhiteSpace(config.AppMetadataFilePath))
    config.AppMetadataFilePath = Path.Combine(configDirectory, config.AppMetadataFilePath);


// patch config with explicit command line options
config.AppId = args.TryGetOption("--app-id", config.AppId);
config.PrivateKeyFilePath = args.TryGetOption("--private-key", config.PrivateKeyFilePath);
config.DefaultPricesFilePath = args.TryGetOption("--prices", config.DefaultPricesFilePath);

config.LocalizedPricesTemplateFilePath = args.TryGetOption("--localized-template", config.LocalizedPricesTemplateFilePath);
config.ProductDefinitionsFilePath = args.TryGetOption("--products", config.ProductDefinitionsFilePath);
config.AppMetadataFilePath = args.TryGetOption("--metadata", config.AppMetadataFilePath);

config.DefaultRegion = args.TryGetOption("--region", config.DefaultRegion);
config.DefaultLocale = args.TryGetOption("--locale", config.DefaultLocale);
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
