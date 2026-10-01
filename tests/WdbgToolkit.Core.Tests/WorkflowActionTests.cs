using WdbgToolkit.PackageManagement;
using WdbgToolkit.Workflows;
using WdbgToolkit.Workflows.Actions;

namespace WdbgToolkit.Core.Tests;

public sealed class WorkflowActionTests
{
    [Fact]
    public async Task ListCrashDumpsReportsZeroWhenDirectoryMissing()
    {
        var dumpDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var action = new ListCrashDumpsAction(dumpDirectory);

        var result = await action.ExecuteAsync(new Dictionary<string, string>());

        Assert.True(result.Succeeded);
        Assert.Equal("0", result.Data["count"]);
        Assert.Empty(result.ArtifactPaths);
    }

    [Fact]
    public async Task ListCrashDumpsFindsDmpFilesOrderedByMostRecent()
    {
        var dumpDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dumpDirectory);
        try
        {
            var older = Path.Combine(dumpDirectory, "older.dmp");
            var newer = Path.Combine(dumpDirectory, "newer.dmp");
            File.WriteAllText(older, string.Empty);
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
            File.WriteAllText(newer, string.Empty);
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

            var action = new ListCrashDumpsAction(dumpDirectory);
            var result = await action.ExecuteAsync(new Dictionary<string, string>());

            Assert.True(result.Succeeded);
            Assert.Equal("2", result.Data["count"]);
            Assert.Equal("newer.dmp", result.Data["latest"]);
            Assert.Equal(2, result.ArtifactPaths.Count);
        }
        finally
        {
            Directory.Delete(dumpDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeLatestDumpFailsWhenNoDumpAvailable()
    {
        var dumpDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var commandRunner = new FakeCommandRunner();
        var action = new AnalyzeLatestDumpAction(commandRunner, dumpDirectory);

        var result = await action.ExecuteAsync(new Dictionary<string, string>());

        Assert.False(result.Succeeded);
        Assert.Empty(commandRunner.Calls);
    }

    [Fact]
    public async Task AnalyzeLatestDumpRunsCdbAnalyzeVerboseAndWritesReport()
    {
        var runDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        try
        {
            var dumpPath = Path.Combine(Path.GetTempPath(), "crash.dmp");
            var commandRunner = new FakeCommandRunner();
            commandRunner.Enqueue(new CommandResult(0, "STACK_TEXT: ...", string.Empty));
            var action = new AnalyzeLatestDumpAction(commandRunner);

            var result = await action.ExecuteAsync(new Dictionary<string, string>
            {
                ["dumpPath"] = dumpPath,
                ["RunDirectory"] = runDirectory
            });

            Assert.True(result.Succeeded);
            Assert.Collection(
                commandRunner.Calls,
                call =>
                {
                    Assert.Equal("cdb", call.Executable);
                    Assert.Equal(["-z", dumpPath, "-c", "!analyze -v;q"], call.Arguments);
                });
            var reportPath = Assert.Single(result.ArtifactPaths);
            Assert.Equal("STACK_TEXT: ...", await File.ReadAllTextAsync(reportPath));
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartTraceUsesDefaultProfileWhenNoneProvided()
    {
        var commandRunner = new FakeCommandRunner();
        commandRunner.Enqueue(new CommandResult(0, string.Empty, string.Empty));
        var action = new StartTraceAction(commandRunner);

        var result = await action.ExecuteAsync(new Dictionary<string, string>());

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["-start", StartTraceAction.DefaultProfile],
            commandRunner.Calls[0].Arguments);
    }

    [Fact]
    public async Task StartTraceUsesRequestedProfile()
    {
        var commandRunner = new FakeCommandRunner();
        commandRunner.Enqueue(new CommandResult(0, string.Empty, string.Empty));
        var action = new StartTraceAction(commandRunner);

        await action.ExecuteAsync(new Dictionary<string, string> { ["profile"] = "CPU.Light" });

        Assert.Equal(["-start", "CPU.Light"], commandRunner.Calls[0].Arguments);
    }

    [Fact]
    public async Task StopTraceSavesEtlFileToRunDirectory()
    {
        var runDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        try
        {
            var expectedPath = Path.Combine(runDirectory, "trace.etl");
            var commandRunner = new FakeCommandRunner(
                onRun: (_, _) => File.WriteAllText(expectedPath, string.Empty));
            commandRunner.Enqueue(new CommandResult(0, string.Empty, string.Empty));
            var action = new StopTraceAction(commandRunner);

            var result = await action.ExecuteAsync(new Dictionary<string, string> { ["RunDirectory"] = runDirectory });

            Assert.True(result.Succeeded);
            Assert.Equal(
                ["-stop", expectedPath, StopTraceAction.DefaultDescription],
                commandRunner.Calls[0].Arguments);
            Assert.Equal([expectedPath], result.ArtifactPaths);
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CapturePacketsUsesDefaultsAndReportsCaptureFile()
    {
        var runDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        try
        {
            var expectedPath = Path.Combine(runDirectory, "capture.pcapng");
            var commandRunner = new FakeCommandRunner(
                onRun: (_, _) => File.WriteAllText(expectedPath, string.Empty));
            commandRunner.Enqueue(new CommandResult(0, string.Empty, string.Empty));
            var action = new CapturePacketsAction(commandRunner);

            var result = await action.ExecuteAsync(new Dictionary<string, string> { ["RunDirectory"] = runDirectory });

            Assert.True(result.Succeeded);
            Assert.Equal(
                ["-i", CapturePacketsAction.DefaultInterface, "-a", "duration:30", "-w", expectedPath],
                commandRunner.Calls[0].Arguments);
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SummarizeCaptureFailsWithoutCapturePath()
    {
        var commandRunner = new FakeCommandRunner();
        var action = new SummarizeCaptureAction(commandRunner);

        var result = await action.ExecuteAsync(new Dictionary<string, string>());

        Assert.False(result.Succeeded);
        Assert.Empty(commandRunner.Calls);
    }

    [Fact]
    public async Task SummarizeCaptureRunsTsharkProtocolHierarchy()
    {
        var runDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        try
        {
            var commandRunner = new FakeCommandRunner();
            commandRunner.Enqueue(new CommandResult(0, "io,phs summary", string.Empty));
            var action = new SummarizeCaptureAction(commandRunner);

            var result = await action.ExecuteAsync(new Dictionary<string, string>
            {
                ["capturePath"] = "capture.pcapng",
                ["RunDirectory"] = runDirectory
            });

            Assert.True(result.Succeeded);
            Assert.Equal(
                ["-r", "capture.pcapng", "-q", "-z", "io,phs"],
                commandRunner.Calls[0].Arguments);
            var summaryPath = Assert.Single(result.ArtifactPaths);
            Assert.Equal("io,phs summary", await File.ReadAllTextAsync(summaryPath));
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ListCustomScriptsCreatesFolderAndListsPs1Files()
    {
        var scriptsDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var action = new ListCustomScriptsAction(scriptsDirectory);

            var emptyResult = await action.ExecuteAsync(new Dictionary<string, string>());
            Assert.True(Directory.Exists(scriptsDirectory));
            Assert.Equal("0", emptyResult.Data["count"]);

            File.WriteAllText(Path.Combine(scriptsDirectory, "triage.ps1"), string.Empty);
            var populatedResult = await action.ExecuteAsync(new Dictionary<string, string>());

            Assert.Equal("1", populatedResult.Data["count"]);
            Assert.Equal("triage.ps1", populatedResult.Data["scripts"]);
        }
        finally
        {
            if (Directory.Exists(scriptsDirectory))
            {
                Directory.Delete(scriptsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCustomScriptFailsWhenScriptMissing()
    {
        var scriptsDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var commandRunner = new FakeCommandRunner();
        var action = new RunCustomScriptAction(commandRunner, scriptsDirectory);

        var result = await action.ExecuteAsync(new Dictionary<string, string> { ["scriptPath"] = "missing.ps1" });

        Assert.False(result.Succeeded);
        Assert.Empty(commandRunner.Calls);
    }

    [Fact]
    public async Task RunCustomScriptRunsPowerShellWithBypassPolicyAndCapturesOutput()
    {
        var scriptsDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scriptsDirectory);
        var runDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        try
        {
            var scriptPath = Path.Combine(scriptsDirectory, "triage.ps1");
            File.WriteAllText(scriptPath, "Write-Output 'ok'");
            var commandRunner = new FakeCommandRunner();
            commandRunner.Enqueue(new CommandResult(0, "ok", string.Empty));
            var action = new RunCustomScriptAction(commandRunner, scriptsDirectory);

            var result = await action.ExecuteAsync(new Dictionary<string, string>
            {
                ["scriptPath"] = "triage.ps1",
                ["RunDirectory"] = runDirectory
            });

            Assert.True(result.Succeeded);
            Assert.Equal(
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                commandRunner.Calls[0].Arguments);
            var outputPath = Assert.Single(result.ArtifactPaths);
            Assert.Contains("ok", await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            Directory.Delete(scriptsDirectory, recursive: true);
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Fact]
    public void CatalogHasNoDuplicateActionIdsAndCoversEveryScenario()
    {
        var catalog = new WorkflowActionCatalog(new FakeCommandRunner());

        var ids = catalog.All.Select(definition => definition.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var scenarioId in new[] { "crash", "performance", "networking", "custom-logs" })
        {
            Assert.NotEmpty(catalog.GetByScenario(scenarioId));
        }
    }

    [Fact]
    public async Task WorkflowRunnerWritesManifestForEachRun()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var scriptsDirectory = Path.Combine(rootDirectory, "Scripts");
            var catalog = new WorkflowActionCatalog(new FakeCommandRunner(), customScriptsDirectory: scriptsDirectory);
            var runner = new WorkflowRunner(catalog, rootDirectory);

            var result = await runner.RunAsync("custom-logs.list-scripts");

            Assert.True(result.Succeeded);
            var scenarioDirectory = Path.Combine(rootDirectory, "custom-logs");
            var runDirectories = Directory.GetDirectories(scenarioDirectory);
            var manifestPath = Path.Combine(Assert.Single(runDirectories), "manifest.json");
            Assert.True(File.Exists(manifestPath));
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    private sealed class FakeCommandRunner(Action<string, IReadOnlyList<string>>? onRun = null) : ICommandRunner
    {
        private readonly Queue<CommandResult> _results = new();

        public List<CommandCall> Calls { get; } = [];

        public void Enqueue(CommandResult result) => _results.Enqueue(result);

        public Task<CommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new CommandCall(executable, arguments.ToArray()));
            onRun?.Invoke(executable, arguments);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new CommandResult(0, string.Empty, string.Empty));
        }
    }

    private sealed record CommandCall(string Executable, IReadOnlyList<string> Arguments);
}
