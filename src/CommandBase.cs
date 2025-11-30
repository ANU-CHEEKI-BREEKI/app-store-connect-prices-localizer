using AppStoreConnect.Net.Client;

public abstract class CommandBase
{
    public AppStoreConnectConfiguration ApiConfig { get; private set; } = null!;
    public GlobalConfig GlobalConfig { get; private set; } = null!;
    public string[] Args { get; set; } = null!;

    public void Initialize(AppStoreConnectConfiguration config, GlobalConfig globalConfig, string[] args)
    {
        Args = args;
        ApiConfig = config;
        GlobalConfig = globalConfig;
    }

    public async Task ExecuteAsync()
    {
        if (!IsMatches(Args))
            return;

        await InternalExecuteAsync();
    }

    public abstract bool IsMatches(string[] args);
    public abstract void PrintHelp();

    protected abstract Task InternalExecuteAsync();
}