using AppStoreConnect.Net.Client;

public abstract class CommandBase
{
    public AppStoreConnectConfiguration ApiConfig { get; private set; } = null!;
    public GlobalConfig GlobalConfig { get; private set; } = null!;
    public string[] Args { get; set; } = null!;

    public abstract string CommandName { get; }

    public void Initialize(AppStoreConnectConfiguration config, GlobalConfig globalConfig, string[] args)
    {
        Args = args;
        ApiConfig = config;
        GlobalConfig = globalConfig;
    }

    public async Task ExecuteAsync()
    {
        if (!Args.Begins(CommandName))
            return;

        await InternalExecuteAsync();
    }

    public abstract void PrintHelp();

    protected abstract Task InternalExecuteAsync();
}