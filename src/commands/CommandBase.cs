using AppStoreConnect.Net.Client;

public abstract class CommandBase
{
    public AppStoreConnectConfiguration Service { get; private set; } = null!;
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
    }

    public async Task ExecuteAsync()
        => await InternalExecuteAsync();

    public abstract void PrintHelp();
    protected abstract Task InternalExecuteAsync();
}
