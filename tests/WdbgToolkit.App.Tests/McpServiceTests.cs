using System.Net;
using System.Net.Sockets;
using WdbgToolkit.App;

namespace WdbgToolkit.App.Tests;

public sealed class McpServiceTests
{
    [Theory]
    [InlineData(null, 7890)]
    [InlineData("", 7890)]
    [InlineData("short", 7890)]
    [InlineData("0123456789012345678901234567890 ", 7890)]
    [InlineData("01234567890123456789012345678901", 0)]
    [InlineData("01234567890123456789012345678901", 65536)]
    public void ServiceSettingsRejectCorruptPortOrToken(string? token, int port)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            EntryPoint = @"C:\Program Files\Windows Debug Toolkit\mcp\wdbgmcp\dist\index.js",
            NodePath = @"C:\Program Files\nodejs\node.exe",
            Port = port,
            Token = token
        });
        var settings = System.Text.Json.JsonSerializer.Deserialize<McpServiceSettings>(json)!;
        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void ServiceCommandQuotesExecutableAndSelectsHeadlessHost()
    {
        Assert.Equal("\"C:\\Program Files\\Windows Debug Toolkit\\WdbgToolkit.App.exe\" --mcp-service",
            McpServiceManager.BuildServiceCommand(@"C:\Program Files\Windows Debug Toolkit\WdbgToolkit.App.exe"));
    }

    [Theory]
    [InlineData("WdbgToolkit.App.exe")]
    [InlineData("C:\\tool\" --other")]
    [InlineData("C:\\tool\n.exe")]
    public void ServiceCommandRejectsUnsafePaths(string path)
    {
        Assert.Throws<ArgumentException>(() => McpServiceManager.BuildServiceCommand(path));
    }

    [Fact]
    public void ServiceConnectionDetailsDescribePersistentPrivilegedAccess()
    {
        const string token = "test-token-not-a-real-credential";
        var serviceDetails = McpServerManager.BuildConnectionDetails(7890, token, isService: true);
        var appDetails = McpServerManager.BuildConnectionDetails(7890, token);

        Assert.Contains($"\"Authorization\": \"Bearer {token}\"", serviceDetails);
        Assert.Contains("LocalSystem", serviceDetails);
        Assert.Contains("starts automatically after reboot", serviceDetails);
        Assert.DoesNotContain("token changes", serviceDetails);
        Assert.Contains("token changes", appDetails);
        Assert.DoesNotContain("LocalSystem", appDetails);
    }

    [Fact]
    public void ServiceTokenCanBeReusedAcrossServerInstances()
    {
        using var first = new McpServerManager();
        using var restarted = new McpServerManager(first.ConnectionToken);
        using var separateApp = new McpServerManager();
        Assert.Equal(first.ConnectionToken, restarted.ConnectionToken);
        Assert.NotEqual(first.ConnectionToken, separateApp.ConnectionToken);
    }

    [Theory]
    [InlineData(405, true)]
    [InlineData(403, false)]
    [InlineData(200, false)]
    public async Task EndpointVerificationRequiresAuthenticatedMcpResponse(int statusCode, bool succeeds)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string token = "verification-test-token";
        var verification = McpServerManager.VerifyEndpointAsync(port, token);
        using var socket = await listener.AcceptTcpClientAsync();
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        var headers = new List<string>();
        while (await reader.ReadLineAsync() is { Length: > 0 } line)
        {
            headers.Add(line);
        }
        Assert.Contains($"Authorization: Bearer {token}", headers);
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        if (succeeds)
        {
            await verification;
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => verification);
        }
    }
}
