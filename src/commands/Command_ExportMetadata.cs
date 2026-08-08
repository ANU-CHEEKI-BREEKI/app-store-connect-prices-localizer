public class Command_ExportMetadata : AppMetadataCommandBase
{
    public const string DefaultFileName = "AppMetadata.csv";

    public override string Name => "export-metadata";
    public override string Description => "Collects every localizable text of the app store product page, for all locales the app already has, into a csv table ready to be sent to a translation tool.";

    public override void PrintHelp()
    {
        Console.WriteLine("export-metadata [<path-to-output.csv>] [--version <x.y.z>] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription($"Exported fields: {string.Join(", ", Fields.Select(f => f.Title))}.");
        CommandLinesUtils.PrintDescription("'Name' and 'Subtitle' are taken from the App Information page, the rest from the app store version page. By default the editable version is used, so the exported table matches exactly what 'import-metadata' will write back.");
        CommandLinesUtils.PrintDescription($"Every locale becomes a column named like 'English (United States)(en-US)'. The locale code in the trailing parentheses is what 'import-metadata' reads, so new languages can be added just by adding a column.");
        CommandLinesUtils.PrintDescription($"If no path is given, the table is written next to your config.json as '{DefaultFileName}', or to the Desktop when there is no config directory.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "<path-to-output.csv>",
            $"Where to write the table. A directory is also accepted, then '{DefaultFileName}' is created inside it."
        );
        CommandLinesUtils.PrintOption(
            "--out <path>",
            "Same as the positional path argument."
        );
        CommandLinesUtils.PrintOption(
            "--version <x.y.z>",
            "Export from this exact app store version instead of the editable one."
        );
        CommandLinesUtils.PrintOption(
            "-v",
            "Include additional verbose output"
        );
    }

    protected override async Task InternalExecuteAsync()
    {
        var verbose = Args.HasFlag("-v");

        try
        {
            Console.WriteLine("   -> Exporting app metadata...");

            // exporting is read only, so a frozen live version is a perfectly valid source
            var target = await ResolveTargetAsync(requireEditable: false, verbose);
            if (target is null)
                return;

            var locales = target.Locales;
            if (locales.Count == 0)
            {
                Console.WriteLine("[ERROR] the app has no localizations to export.");
                return;
            }

            Console.WriteLine($"   -> found {locales.Count} locales: {string.Join(", ", locales)}");

            var headers = new List<string> { KeyColumn, IdColumn, CommentsColumn };
            headers.AddRange(locales.Select(LocaleColumnName));

            var rows = new List<List<string>>();
            foreach (var field in Fields)
            {
                // the Id column is what the translation tooling uses to keep rows stable between exports
                var row = new List<string> { field.Key, field.Key, field.Comment };

                foreach (var locale in locales)
                {
                    var value = GetValue(
                        field,
                        target.FindAppInfoLocalization(locale),
                        target.FindVersionLocalization(locale)
                    );

                    row.Add(value ?? "");
                }

                rows.Add(row);

                if (verbose)
                {
                    var filled = row.Skip(3).Count(c => !string.IsNullOrWhiteSpace(c));
                    Console.WriteLine($"      {field.Key,-20} filled for {filled}/{locales.Count} locales");
                }
            }

            var path = ResolveMetadataPath(
                Args.TryGetOption("--out", GetPositionalPath()),
                DefaultFileName
            );

            await CommandLinesUtils.SaveCsv(path, headers, rows);

            Console.WriteLine();
            Console.WriteLine("summary:");
            Console.WriteLine($"   version: {target.VersionString}");
            Console.WriteLine($"   fields:  {Fields.Length}");
            Console.WriteLine($"   locales: {locales.Count}");
            Console.WriteLine($"   written: {Path.GetFullPath(path)}");
        }
        catch (Exception ex)
        {
            PrintApiError("failed to export app metadata", ex);
        }
    }
}
