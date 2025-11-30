using Newtonsoft.Json;

public static class CommandLinesUtils
{
    public static async Task<T?> LoadJson<T>(this string[] args, bool logToConsole, string arg, string defaultPath)
    {
        var pathToPricesTemplate = GetParameter(args, arg, defaultPath);

        var json = await File.ReadAllTextAsync(pathToPricesTemplate);
        if (logToConsole)
            Console.WriteLine($"loaded json: {json}");

        var pricesTemplate = JsonConvert.DeserializeObject<T>(json);
        return pricesTemplate;
    }

    public static string GetParameter(this string[] args, string arg, string defaultArgValue)
        => TryGetParameter(args, arg, out var v) ? v : defaultArgValue;

    public static bool TryGetParameter(this string[] args, string arg, out string argValue)
    {
        argValue = "";

        var index = Array.IndexOf(args, arg);
        if (index < 0)
            return false;

        if (index + 1 >= args.Length)
            return false;

        argValue = args[index + 1];
        return true;
    }

    public static bool HasFlag(this string[] args, string arg)
        => Array.IndexOf(args, arg) >= 0;
    
    public static bool Begins(this string[] args, string arg)
        => Array.IndexOf(args, arg) == 0;
}

