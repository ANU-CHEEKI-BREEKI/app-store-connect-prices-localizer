
public abstract class CommandBase
{
    

    /// <summary>the direct http client over the same token; endpoints are named by path at the call</summary>
    protected AscHttp Http { get; private set; } = null!;

    public Config Config { get; private set; } = null!;
    public string[] Args { get; set; } = null!;

    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>
    /// whether the command needs an app config at all. Offline commands like 'config'
    /// run before any config is located and before the private key is read.
    /// </summary>
    public virtual bool NeedsConfig => true;

    protected AscAuth? Auth { get; private set; }

    public void Initialize(AscAuth? auth, Config config, string[] args)
    {
        Args = args;
        Auth = auth;
        Config = config;

        if (auth is not null)
            Http = new AscHttp(auth);
    }

    /// <summary>
    /// product ids from the --iap option, empty means every product.
    /// A run time filter: it narrows a command down to a few products without touching the config
    /// </summary>
    public HashSet<string> IapFilter => CommandLinesUtils.ParseIapFilter(Config.Iap);

    /// <summary>
    /// keeps only the items named in the --iap option, everything when it is not given.
    /// An id that matched nothing is reported: a typo would otherwise look like a clean no-op run
    /// </summary>
    protected List<T> FilterByIap<T>(IEnumerable<T> items, Func<T, string?> productIdOf)
    {
        var ids = IapFilter;
        var all = items.ToList();

        if (ids.Count == 0)
            return all;

        var kept = all.Where(i => ids.Contains(productIdOf(i) ?? "")).ToList();

        var missing = ids.Except(kept.Select(i => productIdOf(i) ?? "")).ToList();
        foreach (var id in missing)
            Console.WriteLine($"Warning: --iap '{id}' matched no product.");

        return kept;
    }


    /// <summary>
    /// How many products go at once. An explicit --parallel wins; otherwise the quota the api
    /// reports decides: when what is left would not even cover this run, everything goes one by
    /// one and lets the retry-on-429 pacing do its job.
    /// </summary>
    protected int ResolveParallelism(int productCount, bool verbose)
    {
        var option = Args.TryGetOption("--parallel", "");

        if (int.TryParse(option, out var parsed))
            return Math.Clamp(parsed, 1, 8);

        var estimate = productCount * 35;
        var remaining = AscHttp.HourRemaining;

        var chosen = remaining is { } rem && rem < estimate + 100 ? 1 : 4;

        if (verbose)
            Console.WriteLine($"Parallelism: {chosen} (quota remaining: {remaining?.ToString() ?? "unknown"}, this run needs ~{estimate}).");

        return chosen;
    }

    public async Task ExecuteAsync()
        => await InternalExecuteAsync();


    public abstract void PrintHelp();
    protected abstract Task InternalExecuteAsync();
}
