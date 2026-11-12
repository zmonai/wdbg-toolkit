using System.ServiceProcess;

namespace WdbgToolkit.App;

internal sealed class McpWindowsService : ServiceBase
{
    private McpServerManager? _server;
    private volatile bool _stopping;

    public McpWindowsService()
    {
        ServiceName = McpServiceManager.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        RequestAdditionalTime(20000);
        var settings = McpServiceManager.LoadSettings();
        _server = new McpServerManager(settings.Token);
        try
        {
            _server.StartAsync(settings.EntryPoint, settings.Port, settings.NodePath).GetAwaiter().GetResult();
            _server.Exited += ServerExited;
            if (!_server.IsRunning)
            {
                throw new InvalidOperationException("The MCP process exited during service startup.");
            }
        }
        catch
        {
            _server.Dispose();
            _server = null;
            throw;
        }
    }

    private void ServerExited(object? sender, EventArgs e)
    {
        if (!_stopping)
        {
            // An unclean exit lets SCM apply the configured service recovery policy.
            Environment.Exit(1);
        }
    }

    protected override void OnStop()
    {
        _stopping = true;
        if (_server is not null)
        {
            _server.Exited -= ServerExited;
            _server.StopAsync().GetAwaiter().GetResult();
            _server.Dispose();
            _server = null;
        }
    }

    protected override void OnShutdown() => OnStop();
}
