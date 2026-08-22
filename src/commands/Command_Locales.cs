/// <summary>
/// Everything about translating the things that are not the store page lives under one command,
/// because they all share one 'SourceLocales' and one csv layout. 'locales' on its own shows which
/// languages exist where, 'locales export ...' pulls the text out, 'locales import ...' puts it back.
///
/// Only a router: each subcommand is a CommandBase of its own, so it keeps its own help, its own
/// options and its own answer to whether it needs a config at all.
/// </summary>
public class Command_Locales : CommandBase
{
    /// <summary>
    /// the subcommand the args select, or the listing when they select nothing.
    /// Resolved off Args, which Program hands over before it reads NeedsConfig
    /// </summary>
    private CommandBase Sub => _sub ??= Resolve();
    private CommandBase? _sub;

    private CommandBase Resolve()
    {
        var name = Arg(1);
        var target = Arg(2);

        return (name, target) switch
        {
            ("" or "list", _) => new Command_LocalesList(),

            ("export", "achievements") => new Command_LocalesExportAchievements(),
            ("export", "iaps") => new Command_LocalesExportIaps(),

            ("import", "achievements") => new Command_LocalesImportAchievements(),
            ("import", "iaps") => new Command_LocalesImportIaps(),

            ("sync", "achievement-images") => new Command_LocalesSyncAchievementImages(),

            ("submit", _) => new Command_LocalesSubmit(),

            _ => new Unknown(name, target),
        };
    }

    private string Arg(int index)
        => Args.Length > index && !Args[index].StartsWith('-') ? Args[index] : "";

    public override bool NeedsConfig => Sub.NeedsConfig;

    protected override Task InternalExecuteAsync()
    {
        Sub.Initialize(Service, Config, Args);
        return Sub.ExecuteAsync();
    }

    public override string Name => "locales";

    public override string Description
        => "Translating everything that is not the store page: Game Center achievements and In-App Purchase texts, out to a csv and back. Run 'locales' on its own for the listing.";

    public override void PrintHelp()
    {
        // 'locales export iaps --help' should explain that subcommand, not this router
        if (Sub is not Unknown && Sub is not Command_LocalesList)
        {
            Sub.PrintHelp();
            return;
        }

        Console.WriteLine("locales [list]");
        Console.WriteLine("locales export achievements [options]");
        Console.WriteLine("locales export iaps [options]");
        Console.WriteLine("locales import achievements [options]");
        Console.WriteLine("locales import iaps [options]");
        Console.WriteLine("locales sync achievement-images [options]");
        Console.WriteLine("locales submit [options]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("App Store Connect translates nothing for you. The store page at least has 'export-metadata', but achievement texts and In-App Purchase names stay in whatever language they were typed in, in every country - and the purchase name is what the payment sheet shows at the moment somebody pays.");
        CommandLinesUtils.PrintDescription("Both exports write the same table 'export-metadata' does: a 'Key' column and one column per language, named like 'English (United States)(en-US)'. The imports read it back. Adding a language is adding a column.");
        CommandLinesUtils.PrintDescription("An empty cell means 'not translated yet' and never erases anything. A value App Store Connect already has is not re-sent, so re-running an unchanged csv writes nothing.");

        Console.WriteLine();
        Console.WriteLine("subcommands:");

        CommandLinesUtils.PrintOption("list", "Show which languages the achievements, the In-App Purchases and the store page have, and what is missing from where. This is the default when no subcommand is given.");
        CommandLinesUtils.PrintOption("export achievements", "Write every Game Center achievement title and both descriptions into a translatable csv.");
        CommandLinesUtils.PrintOption("export iaps", "Write every In-App Purchase display name and description into a translatable csv.");
        CommandLinesUtils.PrintOption("import achievements", "Write a translated achievements csv back, giving every new language the image of the primary one.");
        CommandLinesUtils.PrintOption("import iaps", "Write a translated products csv back into the In-App Purchase localizations. Prices are never part of the request.");
        CommandLinesUtils.PrintOption("sync achievement-images", "Give every achievement localization the image of the primary language, without touching any text.");
        CommandLinesUtils.PrintOption("submit", "Send everything that is waiting to App Store Connect review: In-App Purchases, achievements, the app store version.");

        Console.WriteLine();
        Console.WriteLine("Run 'locales <subcommand> --help' for the options of one subcommand.");
        Console.WriteLine();

        Console.WriteLine("examples:");
        Console.WriteLine();
        CommandLinesUtils.PrintDescription("locales                                    # what exists where", 4);
        CommandLinesUtils.PrintDescription("locales export achievements                # every achievement out to a csv", 4);
        CommandLinesUtils.PrintDescription("locales export iaps --iap pack_one         # one product only", 4);
        CommandLinesUtils.PrintDescription("locales export iaps --locales en-US,uk     # two columns, nothing else", 4);
        CommandLinesUtils.PrintDescription("locales import achievements -n             # what the csv would change, sent nowhere", 4);
        CommandLinesUtils.PrintDescription("locales import iaps --submit               # the translated names go live", 4);
        CommandLinesUtils.PrintDescription("locales submit -n                          # what is waiting for review", 4);
    }

    /// <summary>a subcommand that does not exist, kept as a CommandBase so the router stays uniform</summary>
    private class Unknown(string name, string target) : CommandBase
    {
        public override bool NeedsConfig => false;

        protected override Task InternalExecuteAsync()
        {
            var typed = string.IsNullOrEmpty(target) ? name : $"{name} {target}";
            Console.WriteLine($"unknown subcommand '{typed}'. see 'locales --help'.");
            return Task.CompletedTask;
        }

        public override string Name => "locales";
        public override string Description => "";
        public override void PrintHelp() { }
    }
}
