using AppStoreConnect.Net.Client;

var commands = new CommandsCollection()
{
    new Command_List(),
};

if (commands.TryPrintHelp(args))
    return;

var command = commands.Values.FirstOrDefault(c => args.Begins(c.CommandName));
if (command is null)
{
    Console.WriteLine("no command fount for passed parameters");
    return;
}

var appSettings = await CommandLinesUtils.LoadJson<Credentials>(args, false, "--credentials", "../config.json");
if (appSettings is null)
{
    Console.WriteLine("Error: Failed to deserialize configuration from JSON.");
    return;
}

if (!File.Exists(appSettings.PrivateKeyFilePath))
{
    Console.WriteLine($"Error: Private key file '{appSettings.PrivateKeyFilePath}' not found.");
    return;
}

var privateKeyContent = await File.ReadAllTextAsync(appSettings.PrivateKeyFilePath);
var config = new AppStoreConnectConfiguration(
    appSettings.KeyId,
    appSettings.IssuerId,
    privateKeyContent
);

var globalConfig = await CommandLinesUtils.LoadJson<GlobalConfig>(args, false, "--global-config", "../global-config.json");

command.Initialize(config, globalConfig ?? new GlobalConfig("", "USA", ""), args);
await command.ExecuteAsync();

Console.WriteLine("done.");

