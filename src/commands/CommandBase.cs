using AppStoreConnect.Net.Client;

public abstract class CommandBase
{
    public AppStoreConnectConfiguration Service { get; private set; } = null!;

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

    public void Initialize(AppStoreConnectConfiguration? service, Config config, string[] args)
    {
        Args = args;
        Service = service!;
        Config = config;

        if (service is not null)
            Http = new AscHttp(service);
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

    public async Task ExecuteAsync()
        => await InternalExecuteAsync();

    protected async Task<List<TItem>> FetchAllPagesAsync<TResponse, TItem>(
        IAsynchronousClient asyncClient,
        IReadableConfiguration configuration,
        Func<Task<TResponse>> firstPageFetcher,
        Func<TResponse, List<TItem>?> itemsExtractor,
        Func<TResponse, string?> nextLinkExtractor,
        bool verbose)
        where TResponse : class
    {
        var result = new List<TItem>();
        var response = await firstPageFetcher();
        if (response is null)
            return result;

        var items = itemsExtractor(response);
        if (items is not null)
            result.AddRange(items);

        var nextHref = nextLinkExtractor(response);
        while (!string.IsNullOrEmpty(nextHref))
        {
            try
            {
                var nextUri = new Uri(nextHref);
                var relativePath = nextUri.PathAndQuery;

                var requestOptions = new RequestOptions();
                if (!string.IsNullOrEmpty(configuration.AccessToken))
                {
                    requestOptions.HeaderParameters.Add("Authorization", "Bearer " + configuration.AccessToken);
                }

                var pageWrapper = await asyncClient.GetAsync<TResponse>(
                    relativePath,
                    requestOptions,
                    configuration
                );

                var pageData = pageWrapper.Data;
                if (pageData is not null)
                {
                    var pageItems = itemsExtractor(pageData);
                    if (pageItems is not null)
                        result.AddRange(pageItems);

                    nextHref = nextLinkExtractor(pageData);
                }
                else
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                if (verbose)
                    Console.WriteLine($"[WARN] error fetching next page: {ex.Message}");
                break;
            }
        }

        return result;
    }

    public abstract void PrintHelp();
    protected abstract Task InternalExecuteAsync();
}
