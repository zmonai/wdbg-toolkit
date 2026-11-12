using System.ServiceProcess;

namespace WdbgToolkit.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--mcp-service", StringComparer.Ordinal))
        {
            ServiceBase.Run(new McpWindowsService());
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
