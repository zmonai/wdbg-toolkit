using WdbgToolkit.PackageManagement;

namespace WdbgToolkit.Core.Tests;

public sealed class PostmortemDebuggerConfiguratorTests
{
    [Fact]
    public async Task ConfigurationRequiresExplicitConfirmation()
    {
        var commandRunner = new FakeCommandRunner();
        var dumpDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var configurator = new PostmortemDebuggerConfigurator(commandRunner, dumpDirectory);

        var result = await configurator.ConfigureProcDumpAsync(userConfirmed: false);

        Assert.False(result.Succeeded);
        Assert.Empty(commandRunner.Calls);
        Assert.False(Directory.Exists(dumpDirectory));
    }

    [Fact]
    public async Task RegistersProcDumpForFullMemoryDumps()
    {
        var commandRunner = new FakeCommandRunner();
        var dumpDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var configurator = new PostmortemDebuggerConfigurator(commandRunner, dumpDirectory);

        try
        {
            var result = await configurator.ConfigureProcDumpAsync(userConfirmed: true);

            Assert.True(result.Succeeded);
            Assert.True(Directory.Exists(dumpDirectory));
            Assert.Equal(dumpDirectory, result.DumpDirectory);
            Assert.Collection(
                commandRunner.Calls,
                call =>
                {
                    Assert.Equal("procdump", call.Executable);
                    Assert.Equal(
                        ["-accepteula", "-i", dumpDirectory, "-ma"],
                        call.Arguments);
                });
        }
        finally
        {
            if (Directory.Exists(dumpDirectory))
            {
                Directory.Delete(dumpDirectory);
            }
        }
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public List<CommandCall> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new CommandCall(executable, arguments.ToArray()));
            return Task.FromResult(new CommandResult(0, "Installed", string.Empty));
        }
    }

    private sealed record CommandCall(string Executable, IReadOnlyList<string> Arguments);
}
