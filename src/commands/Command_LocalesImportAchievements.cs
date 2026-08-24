using System.Text.Json.Nodes;

/// <summary>
/// Writes a translated achievements csv back into Game Center.
///
/// Every language also gets an image here, copied from the primary one, because App Store Connect
/// will happily store a language with text and no image and then never let it go live. Doing it in
/// the same pass is what keeps 'imported' and 'ready' from being two different things.
/// </summary>
public class Command_LocalesImportAchievements : GameCenterCommandBase
{
    /// <summary>how many languages get their image at the same time; App Store Connect copes with this many</summary>
    private const int ImageUploadParallelism = 8;

    /// <summary>"vendorId|locale|field" of every value validation rejected</summary>
    private readonly HashSet<string> _invalid = new(StringComparer.OrdinalIgnoreCase);

    protected override TextField[] Fields => Command_LocalesExportAchievements.AchievementFields;

    public override string Name => "locales import achievements";

    public override string Description
        => "Writes a translated achievements csv back into Game Center, giving every language the image of the primary one.";

    public override void PrintHelp()
    {
        Console.WriteLine("locales import achievements [--csv <path>] [--achievement <id[,id...]>] [--locales <code[,code...]>] [--force] [--no-create] [--no-images] [--submit] [-n] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription($"Reads the csv 'locales export achievements' writes: rows matched by the '{KeyColumn}' column, every key a '<vendor_id>.{Command_LocalesExportAchievements.NameField}', '.{Command_LocalesExportAchievements.BeforeEarnedField}' or '.{Command_LocalesExportAchievements.AfterEarnedField}', languages read from the locale code in the trailing parentheses of the column header.");
        CommandLinesUtils.PrintDescription("An empty cell means 'not translated yet' and is left alone - it never wipes a text that is already in App Store Connect. A value identical to what is already there is not sent, so re-running an unchanged csv writes nothing.");
        CommandLinesUtils.PrintDescription("The whole table is validated against the App Store Connect limits first, and nothing at all is sent when something would be rejected. Pass --force to send everything that is valid and skip only the offending values.");
        CommandLinesUtils.PrintDescription("Every language that ends up without an image gets the image of the primary language, downloaded once per achievement and uploaded to each. App Store Connect has no way to share one image between languages, so the bytes really do make the round trip. Pass --no-images to leave images alone.");
        CommandLinesUtils.PrintDescription("A new language needs a title and a pre-earned description, so one that would end up with less is skipped with a warning rather than sent and rejected.");
        CommandLinesUtils.PrintDescription("A language added to a live achievement is live the moment it lands, no review. A new achievement is 'Prepare for Submission' until it is reviewed: pass --submit, or run 'locales submit --achievements' later, then press Submit in the console.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption(
            "--csv <path>",
            $"The table with translations. If not specified, the path from global config json ('AchievementTranslationsFilePath') is used. A directory is also accepted, then '{Command_LocalesExportAchievements.DefaultFileName}' is read from it."
        );
        CommandLinesUtils.PrintOption("--achievement <id[,id...]>", "Import only these achievements, a comma separated list of vendor identifiers. Default is every achievement in the csv.");
        CommandLinesUtils.PrintOption("--locales <code[,code...]>", "Import only these languages, a comma separated list of locale codes, e.g. 'uk,de-DE'. Default is every language the csv has a column for.");
        CommandLinesUtils.PrintOption("--force", "Run even though validation found problems: every value that would be rejected is skipped, everything else is sent.");
        CommandLinesUtils.PrintOption("--no-create", "Do not create localizations for locales an achievement does not have yet, skip them instead.");
        CommandLinesUtils.PrintOption("--no-images", "Do not copy the primary language's image onto the languages that have none.");
        CommandLinesUtils.PrintOption("--submit", "Add every new achievement to the open review submission afterwards. The submission itself is sent from the console. A language added to a live achievement needs no review and is live already.");
        CommandLinesUtils.PrintOption("-n|--dry-run", "Print everything that would be changed, without sending a single write request.");
        CommandLinesUtils.PrintOption("-v", "Include additional verbose output");
    }

    protected override async Task InternalExecuteAsync()
    {
        var canCreate = !Args.HasFlag("--no-create");
        var force = Args.HasFlag("--force");
        var withImages = !Args.HasFlag("--no-images");
        var submit = Args.HasFlag("--submit");
        var onlyAchievements = new HashSet<string>(ParseList("--achievement"), StringComparer.Ordinal);
        var onlyLocales = new HashSet<string>(ParseList("--locales"), StringComparer.OrdinalIgnoreCase);

        _invalid.Clear();

        try
        {
            Console.WriteLine("   -> Importing Game Center achievements...");

            if (DryRun)
                Console.WriteLine("   -> DRY RUN, nothing will be written.");

            var path = ResolveCsvPath(Config.AchievementTranslationsFilePath, Command_LocalesExportAchievements.DefaultFileName);

            if (!File.Exists(path))
            {
                Console.WriteLine($"[ERROR] translations csv not found: '{Path.GetFullPath(path)}'");
                Console.WriteLine("        run 'locales export achievements' first, or pass --csv <path>");
                return;
            }

            var csv = await Translations.LoadAsync(path, Verbose);

            if (csv.Rows.Count == 0)
            {
                Console.WriteLine($"[ERROR] '{path}' has no data rows.");
                return;
            }

            Console.WriteLine($"   -> read {csv.Rows.Count} key(s) in {csv.Locales.Count} language(s) from {Path.GetFullPath(path)}");

            var achievements = await GetAchievementsAsync(Verbose);
            if (achievements is null)
                return;

            // narrowed down here, before the images, so an achievement outside the filter is not
            // touched at all - not even to give it an image
            if (onlyAchievements.Count > 0)
            {
                achievements = achievements.Where(a => onlyAchievements.Contains(a.VendorIdentifier)).ToList();

                foreach (var id in onlyAchievements.Except(achievements.Select(a => a.VendorIdentifier)))
                    Console.WriteLine($"Warning: --achievement '{id}' matched no achievement.");

                if (achievements.Count == 0)
                {
                    Console.WriteLine("   -> nothing to import, no achievement matched.");
                    return;
                }

                Console.WriteLine($"   -> only {achievements.Count} achievement(s): {string.Join(", ", achievements.Select(a => a.VendorIdentifier))}");
            }

            if (onlyLocales.Count > 0)
            {
                foreach (var code in onlyLocales.Where(c => !csv.Locales.Contains(c, StringComparer.OrdinalIgnoreCase)))
                    Console.WriteLine($"Warning: --locales '{code}' has no column in the csv.");

                Console.WriteLine($"   -> only {onlyLocales.Count} language(s): {string.Join(", ", onlyLocales)}");
            }

            var byVendorId = achievements
                .Where(a => !string.IsNullOrWhiteSpace(a.VendorIdentifier))
                .ToDictionary(a => a.VendorIdentifier, StringComparer.Ordinal);

            var updated = new List<string>();
            var created = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();

            var groups = ResolveGroups(csv, byVendorId, onlyAchievements, onlyLocales, skipped);

            if (!Validate(groups, force, skipped))
                return;

            var changed = new List<Achievement>();

            foreach (var group in groups)
            {
                if (await ImportAchievementAsync(group, canCreate, updated, created, skipped, failed))
                    changed.Add(group.Achievement);
            }

            if (withImages)
                await CopyImagesAsync(achievements, updated, skipped, failed);

            PrintSummary(updated, created, skipped, failed);

            if (submit)
            {
                Console.WriteLine();
                Console.WriteLine("   -> adding to the review submission...");
                await ReleaseAsync(changed, failed);
            }
            else if (changed.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"{changed.Count} achievement(s) changed. A live one is live already; a new one waits for 'locales submit --achievements'.");
            }
        }
        catch (Exception ex)
        {
            PrintApiError("failed to import achievements", ex);
        }
    }

    /// <summary>the csv values of one achievement, keyed by locale then field</summary>
    private class AchievementValues
    {
        public Achievement Achievement = null!;
        public Dictionary<string, Dictionary<string, string>> ByLocale = new(StringComparer.OrdinalIgnoreCase);

        public string VendorId => Achievement.VendorIdentifier;
    }

    private List<AchievementValues> ResolveGroups(
        TranslationsCsv csv,
        Dictionary<string, Achievement> byVendorId,
        HashSet<string> onlyAchievements,
        HashSet<string> onlyLocales,
        List<string> skipped
    )
    {
        var result = new List<AchievementValues>();
        var unknown = new List<string>();

        foreach (var group in csv.ById)
        {
            // a row outside --achievement is not unknown, it is simply not asked for this run
            if (onlyAchievements.Count > 0 && !onlyAchievements.Contains(group.Key))
                continue;

            if (!byVendorId.TryGetValue(group.Key, out var achievement))
            {
                unknown.Add(group.Key);
                continue;
            }

            var values = new AchievementValues { Achievement = achievement };

            foreach (var row in group)
            {
                if (Fields.All(f => !string.Equals(f.Key, row.Field, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"Warning: '{row.Key}' is not one of {string.Join(", ", Fields.Select(f => f.Key))}, skipped.");
                    continue;
                }

                foreach (var (sourceLocale, value) in row.Values)
                {
                    if (onlyLocales.Count > 0 && !onlyLocales.Contains(sourceLocale))
                        continue;

                    if (!AppStoreLocales.TryResolve(sourceLocale, achievement.Locales, out var locale, out _))
                    {
                        var reason = $"{sourceLocale} (language the App Store does not support)";
                        if (!skipped.Contains(reason))
                            skipped.Add(reason);
                        continue;
                    }

                    if (!values.ByLocale.TryGetValue(locale, out var fields))
                        values.ByLocale[locale] = fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    fields[row.Field] = value;
                }
            }

            if (values.ByLocale.Count > 0)
                result.Add(values);
        }

        foreach (var id in unknown)
            Console.WriteLine($"Warning: no achievement '{id}' in this app, skipped.");

        return result;
    }

    private bool Validate(List<AchievementValues> groups, bool force, List<string> skipped)
    {
        var errors = new List<string>();

        foreach (var group in groups)
        {
            foreach (var (locale, fields) in group.ByLocale)
            {
                var existing = group.Achievement.Find(locale);

                foreach (var (key, value) in fields)
                {
                    var field = Fields.First(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

                    if (value.Length <= field.MaxLength)
                        continue;

                    // see the same note in Command_LocalesImportIaps: only new text is held to the
                    // documented limit, so a plain round trip of an existing catalog never blocks
                    if (!IsChanged(value, Command_LocalesExportAchievements.ValueOf(existing, key)))
                        continue;

                    errors.Add($"   {key,-28} {group.VendorId} [{locale}] is {value.Length} characters, the limit is {field.MaxLength}");
                    _invalid.Add($"{group.VendorId}|{locale}|{key}");
                    skipped.Add($"{group.VendorId} [{locale}] {key} (too long)");
                }
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

        foreach (var error in errors)
            Console.WriteLine(error);

        Console.WriteLine();

        if (force)
        {
            Console.WriteLine("   -> --force: those values are skipped, everything else is sent.");
            Console.WriteLine();
            return true;
        }

        if (DryRun)
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

    private async Task<bool> ImportAchievementAsync(
        AchievementValues group, bool canCreate,
        List<string> updated, List<string> created, List<string> skipped, List<string> failed)
    {
        var changed = false;

        foreach (var (locale, fields) in group.ByLocale.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            var name = Take(group, locale, Command_LocalesExportAchievements.NameField, fields);
            var before = Take(group, locale, Command_LocalesExportAchievements.BeforeEarnedField, fields);
            var after = Take(group, locale, Command_LocalesExportAchievements.AfterEarnedField, fields);

            if (name is null && before is null && after is null)
                continue;

            var existing = group.Achievement.Find(locale);

            if (existing is null)
            {
                if (!canCreate)
                {
                    Console.WriteLine($"      [SKIP] {group.VendorId} [{locale}] does not exist, and --no-create is set.");
                    skipped.Add($"{group.VendorId} [{locale}]");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(before))
                {
                    Console.WriteLine($"[WARN] {group.VendorId} [{locale}] is new and needs a title and a pre-earned description, skipped.");
                    skipped.Add($"{group.VendorId} [{locale}] (half a localization)");
                    continue;
                }

                Console.WriteLine($"      [NEW]  {group.VendorId} [{locale}] {Preview(name)}");

                if (DryRun)
                {
                    created.Add($"{group.VendorId} [{locale}]");
                    changed = true;
                    continue;
                }

                if (await CreateAsync(group, locale, name, before, after, created, failed))
                    changed = true;

                continue;
            }

            var fieldsChanged = new List<string>();

            if (IsChanged(name, existing.Attributes?.Name)) fieldsChanged.Add(Command_LocalesExportAchievements.NameField);
            else name = null;

            if (IsChanged(before, existing.Attributes?.BeforeEarnedDescription)) fieldsChanged.Add(Command_LocalesExportAchievements.BeforeEarnedField);
            else before = null;

            if (IsChanged(after, existing.Attributes?.AfterEarnedDescription)) fieldsChanged.Add(Command_LocalesExportAchievements.AfterEarnedField);
            else after = null;

            if (fieldsChanged.Count == 0)
            {
                if (Verbose)
                    Console.WriteLine($"      [SAME] {group.VendorId} [{locale}] already up to date.");
                continue;
            }

            Console.WriteLine($"      [SET]  {group.VendorId} [{locale}] {string.Join(", ", fieldsChanged)}");

            if (DryRun)
            {
                updated.Add($"{group.VendorId} [{locale}] {string.Join("/", fieldsChanged)}");
                changed = true;
                continue;
            }

            if (await UpdateAsync(group, locale, existing, name, before, after, fieldsChanged, updated, failed))
                changed = true;
        }

        return changed;
    }

    private async Task<bool> CreateAsync(
        AchievementValues group, string locale, string name, string before, string? after,
        List<string> created, List<string> failed)
    {
        try
        {
            var body = AscHttp.Body(
                "gameCenterAchievementLocalizations",
                new JsonObject
                {
                    ["gameCenterAchievement"] = AscHttp.Link("gameCenterAchievements", group.Achievement.Id),
                },
                new JsonObject
                {
                    ["locale"] = locale,
                    ["name"] = name,
                    ["beforeEarnedDescription"] = before,
                    ["afterEarnedDescription"] = after ?? before,
                }
            );

            var response = await Http.PostAsync("/v1/gameCenterAchievementLocalizations", body);

            if (response["data"] is JsonNode data)
                group.Achievement.Localizations.Add(new Localization(data));

            created.Add($"{group.VendorId} [{locale}]");
            return true;
        }
        catch (Exception ex)
        {
            PrintApiError($"failed to create the {locale} localization of {group.VendorId}", ex);
            failed.Add($"{group.VendorId} [{locale}]");
            return false;
        }
    }

    private async Task<bool> UpdateAsync(
        AchievementValues group, string locale, Localization existing,
        string? name, string? before, string? after, List<string> fieldsChanged,
        List<string> updated, List<string> failed)
    {
        try
        {
            // every attribute goes out, including the ones left as they are: App Store Connect
            // reads an explicit null as "clear this field", which is also what the generated
            // client always sent. So everything this update does not mean to change is resent as it is
            var body = AscHttp.BodyWithAttributes(
                "gameCenterAchievementLocalizations",
                existing.Id,
                new JsonObject
                {
                    ["name"] = name ?? existing.Attributes?.Name,
                    ["beforeEarnedDescription"] = before ?? existing.Attributes?.BeforeEarnedDescription,
                    ["afterEarnedDescription"] = after ?? existing.Attributes?.AfterEarnedDescription,
                }
            );

            await Http.PatchAsync($"/v1/gameCenterAchievementLocalizations/{existing.Id}", body);

            updated.Add($"{group.VendorId} [{locale}] {string.Join("/", fieldsChanged)}");
            return true;
        }
        catch (Exception ex)
        {
            PrintApiError($"failed to update the {locale} localization of {group.VendorId}", ex);
            failed.Add($"{group.VendorId} [{locale}]");
            return false;
        }
    }

    /// <summary>
    /// Gives every language without an image the image of the primary one.
    ///
    /// Re-reads the languages of the achievements this run touched, because the ones it just
    /// created have no image and are not in the snapshot taken before the writes.
    /// </summary>
    private async Task CopyImagesAsync(List<Achievement> achievements, List<string> updated, List<string> skipped, List<string> failed)
    {
        Console.WriteLine();
        Console.WriteLine("   -> images...");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        var copied = 0;

        foreach (var achievement in achievements)
        {
            if (!DryRun)
                await LoadLocalizationsAsync(achievement, false);

            var targets = achievement.Localizations.Where(l => achievement.ImageOf(l) is null).ToList();
            if (targets.Count == 0)
                continue;

            var source = FindImageSource(achievement);
            if (source is null)
            {
                Console.WriteLine($"      [SKIP] {achievement.VendorIdentifier} has no image anywhere to copy from.");
                skipped.Add($"{achievement.VendorIdentifier} images (no source image)");
                continue;
            }

            Console.WriteLine($"      {achievement.VendorIdentifier}: {targets.Count} language(s) from {source.Attributes?.Locale}");

            if (DryRun)
            {
                foreach (var target in targets)
                    updated.Add($"{achievement.VendorIdentifier} [{target.Attributes?.Locale}] image");

                copied += targets.Count;
                continue;
            }

            // downloaded once, uploaded to each: the same bytes, and the api has no way to share them
            var image = achievement.ImageOf(source)!;
            var downloaded = await DownloadImageAsync(http, image);

            if (downloaded is null)
            {
                Console.WriteLine($"      [SKIP] {achievement.VendorIdentifier}: the {source.Attributes?.Locale} image is not downloadable yet.");
                skipped.Add($"{achievement.VendorIdentifier} images (source not ready)");
                continue;
            }

            // a copy is three slow round trips and the languages do not depend on each other,
            // so a few of them go at once; the lists are shared, hence the lock
            var bytes = downloaded.Value.Bytes;
            var fileName = downloaded.Value.FileName;
            var gate = new SemaphoreSlim(ImageUploadParallelism);

            await Task.WhenAll(targets.Select(async target =>
            {
                var locale = target.Attributes?.Locale;
                await gate.WaitAsync();

                try
                {
                    await CopyImageAsync(achievement, target, bytes, fileName, Verbose);

                    lock (updated)
                    {
                        updated.Add($"{achievement.VendorIdentifier} [{locale}] image");
                        copied++;
                    }
                }
                catch (Exception ex)
                {
                    lock (failed)
                    {
                        PrintApiError($"failed to copy the image to {achievement.VendorIdentifier} [{locale}]", ex);
                        failed.Add($"{achievement.VendorIdentifier} [{locale}] image");
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        Console.WriteLine(copied == 0
            ? "      every language already has an image."
            : $"      {copied} image(s) copied.");
    }

    private async Task ReleaseAsync(List<Achievement> changed, List<string> failed)
    {
        if (changed.Count == 0)
        {
            Console.WriteLine("      nothing changed, nothing to release.");
            return;
        }

        // only a new achievement needs review; a language added to a live one is live already
        var submit = new Command_LocalesSubmit();
        submit.Initialize(Auth, Config, Args.Where(a => a != "--achievement").Concat(new[] { "--achievements" }).ToArray());
        await submit.ExecuteAsync();
    }

    private string? Take(AchievementValues group, string locale, string field, Dictionary<string, string> fields)
    {
        if (!fields.TryGetValue(field, out var value))
            return null;

        if (_invalid.Contains($"{group.VendorId}|{locale}|{field}"))
            return null;

        return value;
    }
}
