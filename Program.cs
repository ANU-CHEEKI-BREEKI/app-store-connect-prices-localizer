using System.Text.Json;
using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;

var commands = new CommandsCollection()
{
    new Command_List(),
};

if (commands.TryPrintHelp(args))
    return;

var command = commands.FirstOrDefault(c => c.IsMatches(args));
if (command is null)
{
    Console.WriteLine("no command fount for passed parameters");
    return;
}

var configPath = "../config.json";
if (!File.Exists(configPath))
{
    Console.WriteLine($"Error: {configPath} file not found.");
    return;
}

string jsonString = await File.ReadAllTextAsync(configPath);
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var appSettings = JsonSerializer.Deserialize<AppConfig>(jsonString, options);

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

command.Initialize(config, args);
await command.ExecuteAsync();

Console.WriteLine("done.");

