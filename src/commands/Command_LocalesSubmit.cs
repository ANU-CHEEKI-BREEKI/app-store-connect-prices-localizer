using AppStoreConnect.Net.Api;
using AppStoreConnect.Net.Client;
using AppStoreConnect.Net.Model;

/// <summary>
/// Sends everything that is waiting to App Store Connect review.
///
/// Three different things, three different mechanisms, and none of them is the button next to the
/// other two in the console:
/// - an In-App Purchase gets its own submission, independent of any app version
/// - a Game Center achievement is not reviewed but released, which is what turns a localization
///   from 'Prepare for Submission' into 'Live'
/// - the app store version goes into a review submission, as an item of it
///
/// Kept out of the imports on purpose: a translation is normally imported a few times before it is
/// right, and review is the one step in this tool that is not free to redo.
/// </summary>
public class Command_LocalesSubmit : GameCenterCommandBase
{
    protected override TextField[] Fields => Array.Empty<TextField>();

    public override string Name => "locales submit";

    public override string Description
        => "Sends everything that is waiting to App Store Connect review: the In-App Purchases, the Game Center achievements and the app store version.";

    public override void PrintHelp()
    {
        Console.WriteLine("locales submit [--iaps] [--texts] [--achievements] [--app] [--iap <id[,id...]>] [-n] [-v]");
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("description:");
        CommandLinesUtils.PrintDescription(Description);
        CommandLinesUtils.PrintDescription("Without any of the three flags all three are done. With one or more, only those.");
        CommandLinesUtils.PrintDescription("In-App Purchases: every product sitting in READY_TO_SUBMIT gets its own submission, and so does an approved product with a language still in 'Prepare for Submission'. A product already waiting for review or in review is left alone, so running this twice is safe.");
        CommandLinesUtils.PrintDescription("Achievements: a release is created for every achievement, which is what turns its localizations from 'Prepare for Submission' into 'Live'. An achievement whose languages have no image can not be released and is reported instead.");
        CommandLinesUtils.PrintDescription("App store version: the editable version is added to the open review submission, or to a new one, and that submission is then submitted.");
        CommandLinesUtils.PrintDescription("Run with -n first. This is the one command here that can not be undone by running it again.");

        Console.WriteLine();
        Console.WriteLine("options:");

        CommandLinesUtils.PrintOption("--iaps", "Submit the In-App Purchases only.");
        CommandLinesUtils.PrintOption("--texts", "Also submit approved products that have a language still in 'Prepare for Submission'. The api does not report those languages as submitted afterwards, so run this once.");
        CommandLinesUtils.PrintOption("--achievements", "Release the Game Center achievements only.");
        CommandLinesUtils.PrintOption("--app", "Submit the app store version only.");
        CommandLinesUtils.PrintOption(CommandLinesUtils.IapOptionName, CommandLinesUtils.IapOptionDescription);
        CommandLinesUtils.PrintOption("-n|--dry-run", "Print everything that would be submitted, without sending a single write request.");
        CommandLinesUtils.PrintOption("-v", "Include additional verbose output");
    }

    protected override async Task InternalExecuteAsync()
    {
        var wantIaps = Args.HasFlag("--iaps");
        var wantAchievements = Args.HasFlag("--achievements");
        var wantApp = Args.HasFlag("--app");

        // no flag means all three, which is what "submit everything" reads like
        if (!wantIaps && !wantAchievements && !wantApp)
            wantIaps = wantAchievements = wantApp = true;

        try
        {
            if (string.IsNullOrWhiteSpace(Config.AppId))
            {
                Console.WriteLine("[ERROR] no app id. specify it in config.json or with --app-id");
                return;
            }

            if (DryRun)
                Console.WriteLine("   -> DRY RUN, nothing will be submitted.");

            if (wantIaps)
                await SubmitIapsAsync();

            if (wantAchievements)
                await ReleaseAchievementsAsync();

            if (wantApp)
                await SubmitVersionAsync();
        }
        catch (Exception ex)
        {
            PrintApiError("failed to submit for review", ex);
        }
    }

    /// <summary>states in which a product is already on its way, so submitting it again is noise</summary>
    private static readonly InAppPurchaseState[] AlreadyOnItsWay =
    {
        InAppPurchaseState.WAITINGFORREVIEW,
        InAppPurchaseState.INREVIEW,
        InAppPurchaseState.PENDINGBINARYAPPROVAL,
    };

    /// <summary>
    /// An approved product stays APPROVED when a language is added to it: the new text carries its
    /// own state instead, and the product is worth a submission exactly when some of it is still
    /// 'Prepare for Submission'.
    ///
    /// That state is not to be trusted after a submission though: the api keeps answering
    /// PREPARE_FOR_SUBMISSION while the console already says 'Waiting for Review'. Which is why
    /// this path is behind --texts, and running it twice is not safe.
    /// </summary>
    private async Task<int> PendingLocalizationsAsync(InAppPurchaseV2 product)
    {
        var api = new InAppPurchasesApi(Service);
        var response = await api.InAppPurchasesV2InAppPurchaseLocalizationsGetToManyRelatedAsync(product.Id, limit: 200);

        return (response?.Data ?? new())
            .Count(l => l.Attributes?.State == InAppPurchaseLocalizationAttributes.StateEnum.PREPAREFORSUBMISSION);
    }

    private async Task SubmitIapsAsync()
    {
        var texts = Args.HasFlag("--texts");

        Console.WriteLine();
        Console.WriteLine("   -> In-App Purchases...");

        var appsApi = new AppsApi(Service);

        var products = await FetchAllPagesAsync<InAppPurchasesV2Response, InAppPurchaseV2>(
            appsApi.AsynchronousClient,
            appsApi.Configuration,
            () => appsApi.AppsInAppPurchasesV2GetToManyRelatedAsync(Config.AppId, limit: 200),
            r => r.Data,
            r => r.Links?.Next,
            Verbose
        );

        products = FilterByIap(products, p => p.Attributes?.ProductId);

        var ready = new List<InAppPurchaseV2>();

        foreach (var product in products)
        {
            var state = product.Attributes?.State;

            if (state is not null && AlreadyOnItsWay.Contains(state.Value))
            {
                if (Verbose)
                    Console.WriteLine($"      [SAME] {product.Attributes?.ProductId} is already {state}.");
                continue;
            }

            if (state == InAppPurchaseState.READYTOSUBMIT)
            {
                ready.Add(product);
                continue;
            }

            if (state == InAppPurchaseState.APPROVED && texts)
            {
                var pending = await PendingLocalizationsAsync(product);

                if (pending > 0)
                {
                    Console.WriteLine($"      [TEXT] {product.Attributes?.ProductId} is approved, {pending} language(s) still to be submitted.");
                    ready.Add(product);
                }
                else if (Verbose)
                    Console.WriteLine($"      [SAME] {product.Attributes?.ProductId} is already {state}.");

                continue;
            }

            if (Verbose)
                Console.WriteLine($"      [SKIP] {product.Attributes?.ProductId} is {state}, not ready to submit.");
        }

        if (ready.Count == 0)
        {
            Console.WriteLine("      nothing to submit, no product is ready.");
            return;
        }

        await SubmitProductsAsync(Service, ready, DryRun, Verbose);
    }

    /// <summary>
    /// Submits products for review, one submission each. Shared with 'locales import iaps --submit',
    /// which passes exactly the products it just changed
    /// </summary>
    public static async Task SubmitProductsAsync(AppStoreConnectConfiguration service, IEnumerable<InAppPurchaseV2> products, bool dryRun, bool verbose)
    {
        var api = new InAppPurchaseSubmissionsApi(service);

        var submitted = 0;
        var failed = 0;

        foreach (var product in products)
        {
            var productId = product.Attributes?.ProductId ?? product.Id;

            Console.WriteLine($"      [SEND] {productId}");

            if (dryRun)
            {
                submitted++;
                continue;
            }

            try
            {
                var request = new InAppPurchaseSubmissionCreateRequest(
                    new InAppPurchaseSubmissionCreateRequestData(
                        InAppPurchaseSubmissionCreateRequestData.TypeEnum.InAppPurchaseSubmissions,
                        new InAppPurchaseAppStoreReviewScreenshotCreateRequestDataRelationships(
                            inAppPurchaseV2: new InAppPurchaseAppStoreReviewScreenshotCreateRequestDataRelationshipsInAppPurchaseV2(
                                new AppRelationshipsInAppPurchasesDataInner(
                                    AppRelationshipsInAppPurchasesDataInner.TypeEnum.InAppPurchases,
                                    product.Id
                                )
                            )
                        )
                    )
                );

                await api.InAppPurchaseSubmissionsCreateInstanceAsync(request);
                submitted++;
            }
            catch (Exception ex)
            {
                PrintApiError($"failed to submit {productId}", ex);
                failed++;
            }
        }

        Console.WriteLine($"      {submitted} product(s) submitted, {failed} failed.");
    }

    /// <summary>
    /// Same product list, but taken straight from what an import changed rather than from the api.
    /// The import holds them as IapTexts, which is where the real product sits
    /// </summary>
    public static Task SubmitProductsAsync(AppStoreConnectConfiguration service, IEnumerable<IapLocalesCommandBase.IapTexts> products, bool dryRun, bool verbose)
        => SubmitProductsAsync(service, products.Select(p => p.Product), dryRun, verbose);

    private async Task ReleaseAchievementsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("   -> Game Center achievements...");

        var achievements = await GetAchievementsAsync(Verbose);
        if (achievements is null)
            return;

        if (achievements.Count == 0)
        {
            Console.WriteLine("      this app has no achievements.");
            return;
        }

        if (string.IsNullOrWhiteSpace(GameCenterDetailId))
        {
            Console.WriteLine("[ERROR] no Game Center detail to release against.");
            return;
        }

        var alreadyReleased = await ReleasedAchievementIdsAsync();

        var api = new GameCenterAchievementReleasesApi(Service);

        var released = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var achievement in achievements)
        {
            // a second run should find nothing to do, not send seventy requests App Store Connect
            // will answer with the release it already has
            if (alreadyReleased.Contains(achievement.Data.Id))
            {
                if (Verbose)
                    Console.WriteLine($"      [SAME] {achievement.VendorIdentifier} is already released.");
                skipped++;
                continue;
            }

            // a language without an image blocks the release, and the api says so in a way that is
            // easy to miss among seventy of them
            var withoutImage = achievement.Localizations.Where(l => achievement.ImageOf(l) is null).ToList();

            if (withoutImage.Count > 0)
            {
                Console.WriteLine($"      [SKIP] {achievement.VendorIdentifier}: {withoutImage.Count} language(s) have no image ({string.Join(", ", withoutImage.Select(l => l.Attributes?.Locale))})");
                skipped++;
                continue;
            }

            Console.WriteLine($"      [SEND] {achievement.VendorIdentifier}");

            if (DryRun)
            {
                released++;
                continue;
            }

            try
            {
                var request = new GameCenterAchievementReleaseCreateRequest(
                    new GameCenterAchievementReleaseCreateRequestData(
                        GameCenterAchievementReleaseCreateRequestData.TypeEnum.GameCenterAchievementReleases,
                        new GameCenterAchievementReleaseCreateRequestDataRelationships(
                            gameCenterDetail: new GameCenterAchievementReleaseCreateRequestDataRelationshipsGameCenterDetail(
                                new AppRelationshipsGameCenterDetailData(
                                    AppRelationshipsGameCenterDetailData.TypeEnum.GameCenterDetails,
                                    GameCenterDetailId
                                )
                            ),
                            gameCenterAchievement: new GameCenterAchievementLocalizationCreateRequestDataRelationshipsGameCenterAchievement(
                                new GameCenterAchievementLocalizationRelationshipsGameCenterAchievementData(
                                    GameCenterAchievementLocalizationRelationshipsGameCenterAchievementData.TypeEnum.GameCenterAchievements,
                                    achievement.Data.Id
                                )
                            )
                        )
                    )
                );

                await api.GameCenterAchievementReleasesCreateInstanceAsync(request);
                released++;
            }
            catch (ApiException ex) when (ex.ErrorCode == 409)
            {
                // already released, which is exactly what a second run should find
                if (Verbose)
                    Console.WriteLine($"      [SAME] {achievement.VendorIdentifier} is already released.");
                skipped++;
            }
            catch (Exception ex)
            {
                PrintApiError($"failed to release {achievement.VendorIdentifier}", ex);
                failed++;
            }
        }

        Console.WriteLine($"      {released} achievement(s) released, {skipped} skipped, {failed} failed.");
    }

    /// <summary>
    /// the achievements that already have a release on this game center detail. Reading them is one
    /// request, and it is what makes running 'submit' twice a no-op instead of seventy writes
    /// </summary>
    private async Task<HashSet<string>> ReleasedAchievementIdsAsync()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(GameCenterDetailId))
            return result;

        try
        {
            var api = new GameCenterDetailsApi(Service);

            var releases = await FetchAllPagesAsync<GameCenterAchievementReleasesResponse, GameCenterAchievementRelease>(
                api.AsynchronousClient,
                api.Configuration,
                () => api.GameCenterDetailsAchievementReleasesGetToManyRelatedAsync(GameCenterDetailId, limit: 200),
                r => r.Data,
                r => r.Links?.Next,
                Verbose
            );

            foreach (var release in releases)
            {
                var id = release.Relationships?.GameCenterAchievement?.Data?.Id;
                if (!string.IsNullOrWhiteSpace(id))
                    result.Add(id);
            }

            if (Verbose)
                Console.WriteLine($"      {result.Count} achievement(s) are already released.");
        }
        catch (Exception ex)
        {
            // not fatal: without the list every achievement is simply attempted, and a duplicate
            // release is answered with a 409 that is already handled
            Console.WriteLine($"Warning: could not read the existing releases: {ex.Message}");
        }

        return result;
    }

    /// <summary>states of a review submission that is still open and can take another item</summary>
    private static readonly ReviewSubmissionAttributes.StateEnum[] OpenStates =
    {
        ReviewSubmissionAttributes.StateEnum.READYFORREVIEW,
        ReviewSubmissionAttributes.StateEnum.UNRESOLVEDISSUES,
    };

    private async Task SubmitVersionAsync()
    {
        Console.WriteLine();
        Console.WriteLine("   -> App store version...");

        var platform = Args.TryGetOption("--platform", "IOS").ToUpperInvariant();

        var versionsResponse = await new AppsApi(Service).AppsAppStoreVersionsGetToManyRelatedAsync(
            Config.AppId,
            filterPlatform: new List<string> { platform },
            fieldsAppStoreVersions: new List<string> { "versionString", "appVersionState", "appStoreState", "platform", "createdDate" },
            limit: 50
        );

        var version = (versionsResponse.Data ?? new())
            .OrderByDescending(v => v.Attributes?.CreatedDate ?? DateTimeOffset.MinValue)
            .FirstOrDefault(v => v.Attributes?.AppVersionState == AppVersionState.PREPAREFORSUBMISSION);

        if (version is null)
        {
            Console.WriteLine("      no version is waiting for submission.");
            return;
        }

        Console.WriteLine($"      version {version.Attributes?.VersionString}");

        var submissionsApi = new ReviewSubmissionsApi(Service);

        var open = (await submissionsApi.ReviewSubmissionsGetCollectionAsync(
                filterApp: new List<string> { Config.AppId },
                filterPlatform: new List<string> { platform },
                limit: 50,
                include: new List<string> { "items" }
            ))?.Data
            ?.FirstOrDefault(s => s.Attributes?.State is { } state && OpenStates.Contains(state));

        if (open is not null && HasVersion(open, version.Id))
        {
            Console.WriteLine("      already in the open review submission, nothing to add.");
            return;
        }

        Console.WriteLine(open is null
            ? "      [NEW]  review submission"
            : $"      [ADD]  to the open review submission {open.Id}");

        if (DryRun)
            return;

        try
        {
            var submissionId = open?.Id ?? await CreateSubmissionAsync(submissionsApi, platform);
            if (string.IsNullOrWhiteSpace(submissionId))
                return;

            await new ReviewSubmissionItemsApi(Service).ReviewSubmissionItemsCreateInstanceAsync(
                new ReviewSubmissionItemCreateRequest(
                    new ReviewSubmissionItemCreateRequestData(
                        ReviewSubmissionItemCreateRequestData.TypeEnum.ReviewSubmissionItems,
                        new ReviewSubmissionItemCreateRequestDataRelationships(
                            reviewSubmission: new ReviewSubmissionItemCreateRequestDataRelationshipsReviewSubmission(
                                new AppRelationshipsReviewSubmissionsDataInner(
                                    AppRelationshipsReviewSubmissionsDataInner.TypeEnum.ReviewSubmissions,
                                    submissionId
                                )
                            ),
                            appStoreVersion: new AppClipDefaultExperienceCreateRequestDataRelationshipsReleaseWithAppStoreVersion(
                                new AlternativeDistributionPackageCreateRequestDataRelationshipsAppStoreVersionData(
                                    AlternativeDistributionPackageCreateRequestDataRelationshipsAppStoreVersionData.TypeEnum.AppStoreVersions,
                                    version.Id
                                )
                            )
                        )
                    )
                )
            );

            await submissionsApi.ReviewSubmissionsUpdateInstanceAsync(
                submissionId,
                new ReviewSubmissionUpdateRequest(
                    new ReviewSubmissionUpdateRequestData(
                        ReviewSubmissionUpdateRequestData.TypeEnum.ReviewSubmissions,
                        submissionId,
                        new ReviewSubmissionUpdateRequestDataAttributes(submitted: true)
                    )
                )
            );

            Console.WriteLine($"      submitted, review submission {submissionId}.");
        }
        catch (Exception ex)
        {
            PrintApiError("failed to submit the app store version", ex);
        }
    }

    private async Task<string?> CreateSubmissionAsync(ReviewSubmissionsApi api, string platform)
    {
        var response = await api.ReviewSubmissionsCreateInstanceAsync(
            new ReviewSubmissionCreateRequest(
                new ReviewSubmissionCreateRequestData(
                    ReviewSubmissionCreateRequestData.TypeEnum.ReviewSubmissions,
                    new ReviewSubmissionCreateRequestDataAttributes(ParsePlatform(platform)),
                    new AccessibilityDeclarationCreateRequestDataRelationships(
                        new AccessibilityDeclarationCreateRequestDataRelationshipsApp(
                            new AccessibilityDeclarationCreateRequestDataRelationshipsAppData(
                                AccessibilityDeclarationCreateRequestDataRelationshipsAppData.TypeEnum.Apps,
                                Config.AppId
                            )
                        )
                    )
                )
            )
        );

        return response?.Data?.Id;
    }

    private static Platform ParsePlatform(string platform) => platform switch
    {
        "MAC_OS" or "MACOS" => Platform.MACOS,
        "TV_OS" or "TVOS" => Platform.TVOS,
        "VISION_OS" or "VISIONOS" => Platform.VISIONOS,
        _ => Platform.IOS,
    };

    private static bool HasVersion(ReviewSubmission submission, string versionId)
        => submission.Relationships?.Items?.Data?.Any(i => string.Equals(i.Id, versionId, StringComparison.Ordinal)) == true;
}
