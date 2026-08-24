using System.Text.Json.Nodes;

public class Command_ImportMetadata : AppMetadataCommandBase
{
    public const string DefaultFileName = "translated_AppMetadata.csv";

    public override string Name => "import-metadata";
    public override string Description => "Fills every localizable text of the app store product page from a translations csv, for all locales present in that table.";

    /// <summary>the values of a single locale column, already split by where they have to be written</summary>
    private class LocaleValues
    {
        public string Locale = "";

        /// <summary>the code as it was written in the csv, kept for messages after the locale is mapped</summary>
        public string SourceLocale = "";

        public Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);

        public bool HasAnyOf(MetadataScope scope)
            => Fields.Any(f => f.Scope == scope && Values.ContainsKey(f.Key));

        public string? Get(string key)
            => Values.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>a value App Store Connect would reject, found before anything is sent</summary>
    private record ValidationError(string Locale, string SourceLocale, string Field, string Problem);

    /// <summary>"locale|field" of every value that failed validation, so the send pass can leave them out</summary>
    private HashSet<string> invalidValues = new(StringComparer.OrdinalIgnoreCase);

    public override void PrintHelp()
    {
        Console.WriteLine("import-metadata [<path-to-translations.csv>] [--version <x.y.z>] [--force] [--no-create] [-n] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription($"Imported fields: {string.Join(", ", Fields.Select(f => f.Title))}.");
        CommandLinesUtils.PrintDescription("Rows are matched by the 'Key' column, locales by the code in the trailing parentheses of the language column, so the table produced by 'export-metadata' can be imported back as is.");
        CommandLinesUtils.PrintDescription("The whole table is validated against the App Store Connect limits first, and nothing at all is sent when something would be rejected. Pass --force to send everything that is valid and skip only the offending values.");
        CommandLinesUtils.PrintDescription("Empty cells are left alone, they never wipe a text that is already in App Store Connect. Values that are already identical are not re-sent, so a re-run after a partial failure is cheap.");
        CommandLinesUtils.PrintDescription("A locale that does not exist on the app yet is created, unless --no-create is passed. Adding a language is therefore just adding a column.");
        CommandLinesUtils.PrintDescription("'Name' and 'Subtitle' go to the App Information page, the rest to the editable app store version. A version that is already released can not be edited, so a new version has to exist first.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "<path-to-translations.csv>",
            $"The table with translations. A directory is also accepted, then '{DefaultFileName}' is read from it."
        );
        CommandLinesUtils.PrintOption(
            "--metadata <path>",
            "Same as the positional path argument."
        );
        CommandLinesUtils.PrintOption(
            "--version <x.y.z>",
            "Write to this exact app store version instead of the editable one."
        );
        CommandLinesUtils.PrintOption(
            "--force",
            "Run even though validation found problems: every value that would be rejected is skipped, everything else is sent."
        );
        CommandLinesUtils.PrintOption(
            "--no-create",
            "Do not create localizations for locales that the app does not have yet, skip them instead."
        );
        CommandLinesUtils.PrintOption(
            "-n",
            "Dry run: print everything that would be changed, without sending a single write request."
        );
        CommandLinesUtils.PrintOption(
            "-v",
            "Include additional verbose output"
        );
    }

    protected override async Task InternalExecuteAsync()
    {
        var verbose = Args.HasFlag("-v");
        var dryRun = Args.HasFlag("-n") || Args.HasFlag("--dry-run");
        var canCreate = !Args.HasFlag("--no-create");
        var force = Args.HasFlag("--force");

        invalidValues.Clear();

        try
        {
            Console.WriteLine("   -> Importing app metadata...");

            if (dryRun)
                Console.WriteLine("   -> DRY RUN, nothing will be written.");

            var path = ResolveMetadataPath(
                Args.TryGetOption("--metadata", GetPositionalPath()),
                DefaultFileName
            );

            if (!File.Exists(path))
            {
                Console.WriteLine($"[ERROR] translations csv not found: '{path}'");
                Console.WriteLine("        pass the path as 'import-metadata <path-to-translations.csv>'");
                return;
            }

            var localeValues = await LoadTranslations(path, verbose);
            if (localeValues is null)
                return;

            // writing needs a version that App Store Connect still accepts edits for
            var target = await ResolveTargetAsync(requireEditable: true, verbose);
            if (target is null)
                return;

            var updated = new List<string>();
            var created = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();

            localeValues = ResolveLocales(localeValues, target, skipped);

            // everything is checked up front: a run that dies halfway leaves the product page
            // half translated, which is worse than not having started it
            if (!Validate(localeValues, target, force, dryRun, skipped))
                return;

            // the App Information pass has to run first and on its own: adding a language there is
            // what makes App Store Connect accept it at all, and it answers by creating the matching
            // version localization itself. going locale by locale would race against that
            Console.WriteLine("   -> App Information (name, subtitle)...");

            var addedLanguages = false;
            foreach (var locale in localeValues)
                addedLanguages |= await ImportAppInfoLocalization(target, locale, canCreate, dryRun, verbose, updated, created, skipped, failed);

            if (addedLanguages && !dryRun)
            {
                Console.WriteLine("   -> re-reading version localizations, the new languages brought their own...");
                target.VersionLocalizations = await GetVersionLocalizationsAsync((string?)target.Version["id"] ?? "", verbose);
            }

            Console.WriteLine("   -> App store version (promotional text, description, what's new, keywords)...");

            foreach (var locale in localeValues)
                await ImportVersionLocalization(target, locale, canCreate, dryRun, verbose, updated, created, skipped, failed);

            PrintSummary(target, updated, created, skipped, failed);
        }
        catch (Exception ex)
        {
            PrintApiError("failed to import app metadata", ex);
        }
    }

    /// <summary>turns the csv into one entry per language column, keeping only the cells that have something in them</summary>
    private async Task<List<LocaleValues>?> LoadTranslations(string path, bool verbose)
    {
        var table = await CommandLinesUtils.LoadCsvTable(path, path, verbose);

        if (table.Rows.Count == 0)
        {
            Console.WriteLine($"[ERROR] '{path}' has no data rows.");
            return null;
        }

        var localeColumns = table.Headers
            .Select(h => new { Header = h, Locale = ExtractLocale(h) })
            .Where(c => c.Locale is not null)
            .ToList();

        if (localeColumns.Count == 0)
        {
            Console.WriteLine($"[ERROR] '{path}' has no language columns.");
            Console.WriteLine("        a language column has to end with the locale code in parentheses, like 'English (United States)(en-US)'");
            return null;
        }

        var result = new List<LocaleValues>();

        foreach (var column in localeColumns)
        {
            var values = new LocaleValues { Locale = column.Locale! };

            foreach (var field in Fields)
            {
                var row = FindRow(table.Rows, field.Key);
                if (row is null)
                    continue;

                var value = row.TryGetValue(column.Header, out var cell) ? cell : "";

                // an empty cell means "not translated", it must not erase what is already published
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                values.Values[field.Key] = value;
            }

            if (values.Values.Count == 0)
            {
                if (verbose)
                    Console.WriteLine($"      skipping empty column '{column.Header}'");
                continue;
            }

            result.Add(values);
        }

        var unknownKeys = table.Rows
            .Select(r => r.TryGetValue(KeyColumn, out var k) ? k : "")
            .Where(k => !string.IsNullOrWhiteSpace(k) && !Fields.Any(f => string.Equals(f.Key, k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var key in unknownKeys)
            Console.WriteLine($"[WARN] row '{key}' is not a known metadata field, ignored.");

        Console.WriteLine($"   -> loaded translations for {result.Count} locales: {string.Join(", ", result.Select(r => r.Locale))}");

        return result;
    }

    /// <summary>
    /// maps the locale codes of the table onto the ones App Store Connect accepts, and drops the
    /// languages the App Store has no product page for, before a single request goes out
    /// </summary>
    private List<LocaleValues> ResolveLocales(List<LocaleValues> localeValues, MetadataTarget target, List<string> skipped)
    {
        var existing = target.Locales;
        var result = new List<LocaleValues>();

        foreach (var values in localeValues)
        {
            values.SourceLocale = values.Locale;

            if (!AppStoreLocales.TryResolve(values.Locale, existing, out var resolved, out var note))
            {
                Console.WriteLine($"[WARN] the App Store has no product page language for '{values.Locale}', skipped.");
                skipped.Add($"{values.Locale} (language the App Store does not support)");
                continue;
            }

            if (!string.Equals(resolved, values.Locale, StringComparison.Ordinal))
                Console.WriteLine($"   -> '{values.Locale}' -> '{resolved}' ({note})");

            var duplicate = result.FirstOrDefault(r => string.Equals(r.Locale, resolved, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
            {
                Console.WriteLine($"[WARN] '{values.Locale}' also means '{resolved}', which column '{duplicate.SourceLocale}' already fills. Skipped.");
                skipped.Add($"{values.Locale} (same locale as {duplicate.SourceLocale})");
                continue;
            }

            values.Locale = resolved;
            result.Add(values);
        }

        Console.WriteLine($"   -> writing {result.Count} locales: {string.Join(", ", result.Select(r => r.Locale))}");

        return result;
    }

    /// <summary>
    /// Checks every value of every locale against what App Store Connect accepts, before the first
    /// write goes out. Returns false when the run must not start.
    ///
    /// Sending first and finding out later is the bad option here: the writes are not transactional,
    /// so a table with one too long keyword line leaves some languages updated and some not,
    /// and the second attempt has to be reasoned about instead of just re-run
    /// </summary>
    private bool Validate(List<LocaleValues> localeValues, MetadataTarget target, bool force, bool dryRun, List<string> skipped)
    {
        var errors = new List<ValidationError>();

        foreach (var locale in localeValues)
        {
            foreach (var field in Fields)
            {
                var value = locale.Get(field.Key);
                if (value is null)
                    continue;

                if (value.Length > field.MaxLength)
                    errors.Add(new ValidationError(
                        locale.Locale, locale.SourceLocale, field.Key,
                        $"{value.Length} characters, the limit is {field.MaxLength}"
                    ));
            }

            // App Store Connect refuses to create a localization for a new language without a name
            if (locale.HasAnyOf(MetadataScope.AppInfo)
                && target.FindAppInfoLocalization(locale.Locale) is null
                && string.IsNullOrWhiteSpace(locale.Get("name")))
            {
                errors.Add(new ValidationError(
                    locale.Locale, locale.SourceLocale, "name",
                    "empty, and a localization for a new language can not be created without it"
                ));
            }
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("   -> validation passed.");
            return true;
        }

        Console.WriteLine();
        Console.WriteLine($"[VALIDATION] {errors.Count} values would be rejected by App Store Connect:");
        Console.WriteLine();

        foreach (var error in errors.OrderBy(e => e.Field).ThenBy(e => e.Locale))
        {
            var column = string.Equals(error.SourceLocale, error.Locale, StringComparison.OrdinalIgnoreCase)
                ? error.Locale
                : $"{error.Locale} (column '{error.SourceLocale}')";

            Console.WriteLine($"   {error.Field,-18} {column,-28} {error.Problem}");
        }

        Console.WriteLine();

        foreach (var error in errors)
        {
            invalidValues.Add($"{error.Locale}|{error.Field}");
            skipped.Add($"{error.Locale} {error.Field} ({error.Problem})");
        }

        if (force)
        {
            Console.WriteLine("   -> --force: those values are skipped, everything else is sent.");
            Console.WriteLine();
            return true;
        }

        if (dryRun)
        {
            Console.WriteLine("   -> dry run, continuing anyway to show the rest. A real run would stop here.");
            Console.WriteLine();
            return true;
        }

        Console.WriteLine("nothing was sent. Fix the table and run again,");
        Console.WriteLine("or pass --force to send everything that is valid and skip the values listed above.");
        Console.WriteLine();

        return false;
    }

    /// <summary>a row is identified by its 'Key' cell, the 'Id' column is accepted as a fallback</summary>
    private static Dictionary<string, string>? FindRow(List<Dictionary<string, string>> rows, string key)
        => rows.FirstOrDefault(r => Matches(r, KeyColumn, key)) ?? rows.FirstOrDefault(r => Matches(r, IdColumn, key));

    private static bool Matches(Dictionary<string, string> row, string column, string key)
        => row.TryGetValue(column, out var value) && string.Equals(value, key, StringComparison.OrdinalIgnoreCase);

    /// <summary>writes 'name' and 'subtitle'. returns true when it added a language the app did not have</summary>
    private async Task<bool> ImportAppInfoLocalization(
        MetadataTarget target, LocaleValues locale, bool canCreate, bool dryRun, bool verbose,
        List<string> updated, List<string> created, List<string> skipped, List<string> failed)
    {
        if (!locale.HasAnyOf(MetadataScope.AppInfo))
            return false;

        if (target.AppInfo is null)
        {
            Console.WriteLine($"[WARN] {locale.Locale}: no editable app info, 'name' and 'subtitle' skipped.");
            skipped.Add($"{locale.Locale} name/subtitle");
            return false;
        }

        var name = Take(locale, "name", verbose);
        var subtitle = Take(locale, "subtitle", verbose);

        var existing = target.FindAppInfoLocalization(locale.Locale);

        if (existing is null)
        {
            if (!canCreate)
            {
                Console.WriteLine($"      [SKIP] {locale.Locale} has no app info localization, and --no-create is set.");
                skipped.Add($"{locale.Locale} name/subtitle");
                return false;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine($"[WARN] {locale.Locale}: a new app info localization needs a 'name', skipped.");
                skipped.Add($"{locale.Locale} name/subtitle");
                return false;
            }

            Console.WriteLine($"      [NEW] {locale.Locale} app info localization: name, subtitle");

            if (dryRun)
            {
                created.Add($"{locale.Locale} name/subtitle");
                return true;
            }

            try
            {
                var request = AscHttp.Body(
                    "appInfoLocalizations",
                    new JsonObject
                    {
                        ["appInfo"] = AscHttp.Link("appInfos", (string?)target.AppInfo["id"] ?? ""),
                    },
                    new JsonObject
                    {
                        ["locale"] = locale.Locale,
                        ["name"] = name,
                        ["subtitle"] = subtitle,
                    }
                );

                var response = await Http.PostAsync("/v1/appInfoLocalizations", request);
                target.AppInfoLocalizations.Add(response["data"]!);

                created.Add($"{locale.Locale} name/subtitle");
                return true;
            }
            catch (Exception ex)
            {
                PrintApiError($"failed to create app info localization for {locale.Locale}", ex);
                failed.Add($"{locale.Locale} name/subtitle");
            }

            return false;
        }

        // only send what actually differs, App Store Connect requests are slow and rate limited
        var changed = new List<string>();

        if (IsChanged(name, (string?)existing["attributes"]?["name"])) changed.Add("name");
        else name = null;

        if (IsChanged(subtitle, (string?)existing["attributes"]?["subtitle"])) changed.Add("subtitle");
        else subtitle = null;

        if (changed.Count == 0)
        {
            if (verbose)
                Console.WriteLine($"      [SAME] {locale.Locale} name/subtitle already up to date.");
            return false;
        }

        Console.WriteLine($"      [SET] {locale.Locale} app info: {string.Join(", ", changed)}");

        if (dryRun)
        {
            updated.Add($"{locale.Locale} {string.Join("/", changed)}");
            return false;
        }

        try
        {
            var existingId = (string?)existing["id"] ?? "";

            var request = AscHttp.BodyWithAttributes(
                "appInfoLocalizations",
                existingId,
                BuildAppInfoAttributes(existing, name, subtitle)
            );

            await Http.PatchAsync($"/v1/appInfoLocalizations/{existingId}", request);
            updated.Add($"{locale.Locale} {string.Join("/", changed)}");
        }
        catch (Exception ex)
        {
            PrintApiError($"failed to update app info localization for {locale.Locale}", ex);
            failed.Add($"{locale.Locale} {string.Join("/", changed)}");
        }

        return false;
    }

    private async Task ImportVersionLocalization(
        MetadataTarget target, LocaleValues locale, bool canCreate, bool dryRun, bool verbose,
        List<string> updated, List<string> created, List<string> skipped, List<string> failed)
    {
        if (!locale.HasAnyOf(MetadataScope.Version))
            return;

        var promotionalText = Take(locale, "promotional_text", verbose);
        var description = Take(locale, "description", verbose);
        var whatsNew = Take(locale, "whats_new", verbose);
        var keywords = Take(locale, "keywords", verbose);

        var existing = target.FindVersionLocalization(locale.Locale);

        if (existing is null)
        {
            if (!canCreate)
            {
                Console.WriteLine($"      [SKIP] {locale.Locale} has no version localization, and --no-create is set.");
                skipped.Add($"{locale.Locale} version texts");
                return;
            }

            Console.WriteLine($"      [NEW] {locale.Locale} version localization");

            if (dryRun)
            {
                created.Add($"{locale.Locale} version texts");
                return;
            }

            try
            {
                var request = AscHttp.Body(
                    "appStoreVersionLocalizations",
                    new JsonObject
                    {
                        ["appStoreVersion"] = AscHttp.Link("appStoreVersions", (string?)target.Version["id"] ?? ""),
                    },
                    new JsonObject
                    {
                        ["description"] = description,
                        ["locale"] = locale.Locale,
                        ["keywords"] = keywords,
                        ["promotionalText"] = promotionalText,
                        ["whatsNew"] = whatsNew,
                    }
                );

                var response = await Http.PostAsync("/v1/appStoreVersionLocalizations", request);
                target.VersionLocalizations.Add(response["data"]!);

                created.Add($"{locale.Locale} version texts");
            }
            catch (Exception ex)
            {
                PrintApiError($"failed to create version localization for {locale.Locale}", ex);
                failed.Add($"{locale.Locale} version texts");
            }

            return;
        }

        var changed = new List<string>();

        if (IsChanged(promotionalText, (string?)existing["attributes"]?["promotionalText"])) changed.Add("promotional_text");
        else promotionalText = null;

        if (IsChanged(description, (string?)existing["attributes"]?["description"])) changed.Add("description");
        else description = null;

        if (IsChanged(whatsNew, (string?)existing["attributes"]?["whatsNew"])) changed.Add("whats_new");
        else whatsNew = null;

        if (IsChanged(keywords, (string?)existing["attributes"]?["keywords"])) changed.Add("keywords");
        else keywords = null;

        if (changed.Count == 0)
        {
            if (verbose)
                Console.WriteLine($"      [SAME] {locale.Locale} version texts already up to date.");
            return;
        }

        Console.WriteLine($"      [SET] {locale.Locale} version: {string.Join(", ", changed)}");

        if (dryRun)
        {
            updated.Add($"{locale.Locale} {string.Join("/", changed)}");
            return;
        }

        try
        {
            var existingId = (string?)existing["id"] ?? "";

            var request = AscHttp.BodyWithAttributes(
                "appStoreVersionLocalizations",
                existingId,
                BuildVersionAttributes(existing, description, keywords, promotionalText, whatsNew)
            );

            await Http.PatchAsync($"/v1/appStoreVersionLocalizations/{existingId}", request);
            updated.Add($"{locale.Locale} {string.Join("/", changed)}");
        }
        catch (Exception ex)
        {
            PrintApiError($"failed to update version localization for {locale.Locale}", ex);
            failed.Add($"{locale.Locale} {string.Join("/", changed)}");
        }
    }

    /// <summary>
    /// reads a field of a locale, leaving out what validation already rejected.
    /// a single bad field would fail the whole request and take the other fields of that locale with it
    /// </summary>
    private string? Take(LocaleValues locale, string key, bool verbose)
    {
        var value = locale.Get(key);
        if (value is null)
            return null;

        // already reported by Validate, this run only reaches here with --force
        if (invalidValues.Contains($"{locale.Locale}|{key}"))
            return null;

        if (verbose)
            Console.WriteLine($"         {key} = {Preview(value)}");

        return value;
    }

    private static bool IsChanged(string? value, string? current)
        => value is not null && !string.Equals(value, current, StringComparison.Ordinal);

    private static string Preview(string value)
    {
        var single = value.Replace("\n", "\\n").Replace("\r", "");
        return single.Length <= 60 ? single : single.Substring(0, 60) + "...";
    }

    private void PrintSummary(MetadataTarget target, List<string> updated, List<string> created, List<string> skipped, List<string> failed)
    {
        Console.WriteLine();
        Console.WriteLine("summary:");
        Console.WriteLine($"   version: {target.VersionString}");

        Console.WriteLine($"   updated: {updated.Count}");
        foreach (var item in updated)
            Console.WriteLine($"      -> {item}");

        Console.WriteLine($"   created: {created.Count}");
        foreach (var item in created)
            Console.WriteLine($"      -> {item}");

        Console.WriteLine($"   skipped: {skipped.Count}");
        foreach (var item in skipped)
            Console.WriteLine($"      -> {item}");

        Console.WriteLine($"   failed:  {failed.Count}");
        foreach (var item in failed)
            Console.WriteLine($"      -> {item}");
    }
}
