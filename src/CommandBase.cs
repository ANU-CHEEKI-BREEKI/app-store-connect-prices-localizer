using AppStoreConnect.Net.Client;

public abstract class CommandBase
{
    public AppStoreConnectConfiguration Config { get; private set; } = null!;
    public string[] Args { get; set; } = null!;

    public void Initialize(AppStoreConnectConfiguration config, string[] args)
    {
        Args = args;
        Config = config;
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