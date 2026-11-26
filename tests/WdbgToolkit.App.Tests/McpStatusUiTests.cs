using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using WdbgToolkit.App;

namespace WdbgToolkit.App.Tests;

public sealed class McpStatusUiTests
{
    [Fact]
    public void RefreshPreservesTheLastStartupErrorAndIsDisabledDuringStartup()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                try
                {
                    const string error = "Service registration failed: Access is denied.";
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(MainWindow).GetField("_mcpOperationError", flags)!.SetValue(window, error);
                    var refresh = (Button)window.FindName("RefreshMcpStatusButton");
                    var status = (TextBlock)window.FindName("McpServerStatus");
                    refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Contains(error, status.Text);
                    refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Contains(error, status.Text);

                    typeof(MainWindow).GetField("_mcpServerBusy", flags)!.SetValue(window, true);
                    typeof(MainWindow).GetMethod("RefreshMcpServerUi", flags)!.Invoke(window, null);
                    Assert.False(refresh.IsEnabled);
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "MCP status UI test did not finish.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
