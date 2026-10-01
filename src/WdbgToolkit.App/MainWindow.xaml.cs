using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WdbgToolkit.Core;
using WdbgToolkit.PackageManagement;
using WdbgToolkit.Workflows;

namespace WdbgToolkit.App;

public partial class MainWindow : Window
{
    private readonly ICommandRunner _commandRunner = new SystemCommandRunner();
    private readonly ToolInstaller _toolInstaller;
    private readonly PostmortemDebuggerConfigurator _postmortemDebuggerConfigurator;
    private readonly WorkflowActionCatalog _workflowActionCatalog;
    private readonly WorkflowRunner _workflowRunner;
    private readonly McpServerManager _mcpServerManager = new();
    private IReadOnlyList<PackageManagerAvailability> _availablePackageManagers = [];
    private IReadOnlyList<string> _selectedPrerequisiteToolIds = [];
    private bool _isBusy;
    private bool _mcpServerBusy;

    public MainWindow()
    {
        InitializeComponent();
        _toolInstaller = new ToolInstaller(_commandRunner);
        _postmortemDebuggerConfigurator = new PostmortemDebuggerConfigurator(_commandRunner);
        _workflowActionCatalog = new WorkflowActionCatalog(_commandRunner);
        _workflowRunner = new WorkflowRunner(_workflowActionCatalog);
        _mcpServerManager.Exited += McpServerManager_Exited;
        DataContext = ScenarioCatalog.All.Select(scenario => new ScenarioListItem(
            scenario,
            IconFor(scenario.Id)));
        DeviceInfo.Text = $"{Environment.OSVersion.VersionString}  |  .NET {Environment.Version}";
        ScenarioList.SelectedIndex = 0;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _mcpServerManager.Exited -= McpServerManager_Exited;
        _mcpServerManager.Dispose();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshPackageManagersAsync();
    }

    private async void RefreshPackageManagersButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshPackageManagersAsync();
    }

    private void PackageManagerSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInstallButton();
    }

    private async void InstallPrerequisitesButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallPrerequisitesAsync(
            _selectedPrerequisiteToolIds,
            "Confirm prerequisite installation",
            "Install these prerequisites?");
    }

    private async void DebugMachineSetupButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallPrerequisitesAsync(
            ScenarioCatalog.AllPrerequisiteToolIds,
            "Confirm debug machine setup",
            "Install all defined debug machine requirements?");
    }

    private async Task InstallPrerequisitesAsync(
        IReadOnlyList<string> prerequisiteToolIds,
        string confirmationTitle,
        string confirmationPrompt)
    {
        if (_isBusy || PackageManagerSelector.SelectedItem is not PackageManagerKind manager)
        {
            return;
        }

        prerequisiteToolIds = prerequisiteToolIds.ToArray();
        var packages = prerequisiteToolIds
            .Select(ToolPackageCatalog.GetById)
            .ToArray();
        var installPlan = new List<(ToolPackage Package, PackageManagerKind Manager)>();
        foreach (var package in packages)
        {
            if (HasPackageId(package, manager))
            {
                installPlan.Add((package, manager));
                continue;
            }

            var alternateManager = _availablePackageManagers
                .Select(result => result.Manager)
                .FirstOrDefault(candidate =>
                    candidate != manager && HasPackageId(package, candidate));

            if (!_availablePackageManagers.Any(result => result.Manager == alternateManager) ||
                !HasPackageId(package, alternateManager))
            {
                InstallationStatus.Text =
                    $"{package.Name} is not available through {manager}. " +
                    $"Install Chocolatey separately, then refresh package managers to install {package.Name}.";
                MessageBox.Show(
                    this,
                    InstallationStatus.Text,
                    "Required package manager unavailable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            installPlan.Add((package, alternateManager));
        }

        var installList = string.Join(
            Environment.NewLine,
            installPlan.Select(item =>
                $"  • {item.Package.Name} ({PackageIdFor(item.Package, item.Manager)}) via {item.Manager}"));

        var configuresPostmortemDebugger = prerequisiteToolIds.Contains("procdump", StringComparer.Ordinal);
        var postmortemNotice = configuresPostmortemDebugger
            ? $"{Environment.NewLine}{Environment.NewLine}After installing ProcDump, the toolkit will accept its Sysinternals license and register it as the Windows postmortem debugger with full-memory dumps (-ma). This replaces the current postmortem debugger, if one is configured. Dumps will be written to:{Environment.NewLine}{PostmortemDebuggerConfigurator.DefaultDumpDirectory}{Environment.NewLine}Full dumps can contain sensitive data and use significant disk space. This system-wide change requires running the toolkit as administrator."
            : string.Empty;
        var confirmation = MessageBox.Show(
            this,
            confirmationPrompt +
            $"{Environment.NewLine}{Environment.NewLine}{installList}" +
            $"{Environment.NewLine}{Environment.NewLine}This will run the listed package managers and may require administrator privileges." +
            postmortemNotice,
            confirmationTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            InstallationStatus.Text = "Installation cancelled.";
            return;
        }

        SetBusy(true);
        InstallationStatus.Text = "Installing prerequisites...";
        try
        {
            var results = new List<ToolInstallationResult>();
            foreach (var item in installPlan)
            {
                InstallationStatus.Text =
                    $"Installing {item.Package.Name} with {item.Manager}...";
                var result = await _toolInstaller.InstallAsync(
                    item.Package.Id,
                    item.Manager,
                    userConfirmed: true);
                results.Add(result);
            }

            var packagesInstalled = results.All(
                result => result.Status == WdbgToolkit.PackageManagement.InstallationStatus.Succeeded);
            var postmortemConfigured = !configuresPostmortemDebugger;
            if (configuresPostmortemDebugger && packagesInstalled)
            {
                InstallationStatus.Text = "Registering ProcDump as the full-dump postmortem debugger...";
                var configuration = await _postmortemDebuggerConfigurator.ConfigureProcDumpAsync(
                    userConfirmed: true);
                postmortemConfigured = configuration.Succeeded;
                results.Add(
                    new ToolInstallationResult(
                        "procdump",
                        PackageManagerKind.Chocolatey,
                        configuration.Succeeded
                            ? WdbgToolkit.PackageManagement.InstallationStatus.Succeeded
                            : WdbgToolkit.PackageManagement.InstallationStatus.Failed,
                        configuration.ExitCode,
                        configuration.StandardOutput,
                        configuration.StandardError,
                        configuration.Error));
            }

            InstallationStatus.Text = FormatInstallationResults(results);
            if (configuresPostmortemDebugger && packagesInstalled)
            {
                InstallationStatus.Text += postmortemConfigured
                    ? $"{Environment.NewLine}ProcDump is registered for full-memory dumps (-ma) in {PostmortemDebuggerConfigurator.DefaultDumpDirectory}."
                    : $"{Environment.NewLine}ProcDump postmortem registration failed. See the error reported for ProcDump.";
            }

            if (!packagesInstalled || !postmortemConfigured)
            {
                MessageBox.Show(
                    this,
                    InstallationStatus.Text,
                    "Some prerequisites could not be installed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            InstallationStatus.Text = $"Installation failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private const string McpServerScenarioId = "mcp-server";

    private void ScenarioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScenarioList.SelectedItem is not ScenarioListItem item)
        {
            SelectedScenarioName.Text = "Choose a scenario";
            SelectedScenarioDescription.Text = string.Empty;
            ScenarioTools.ItemsSource = null;
            WorkflowActionsList.ItemsSource = null;
            WorkflowActionsEmptyNotice.Visibility = Visibility.Collapsed;
            McpServerSection.Visibility = Visibility.Collapsed;
            return;
        }

        SelectedScenarioName.Text = item.Scenario.Name;
        SelectedScenarioDescription.Text = item.Scenario.Description;
        ScenarioTools.ItemsSource = item.Scenario.Tools;
        _selectedPrerequisiteToolIds = item.Scenario.PrerequisiteToolIds;
        PrerequisiteTitle.Text = _selectedPrerequisiteToolIds.Count == 0
            ? "Prerequisites not defined"
            : $"Install {string.Join(" and ", _selectedPrerequisiteToolIds.Select(
                toolId => ToolPackageCatalog.GetById(toolId).Name))}";
        PrerequisiteDescription.Text = item.Scenario.PrerequisiteDescription;
        PackageManagerControls.Visibility = _selectedPrerequisiteToolIds.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        InstallationStatus.Text = string.Empty;
        UpdateInstallButton();

        var actions = _workflowActionCatalog.GetByScenario(item.Scenario.Id);
        WorkflowActionsList.ItemsSource = actions
            .Select(definition => new WorkflowActionListItem(definition))
            .ToArray();
        WorkflowActionsList.Visibility = actions.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        WorkflowActionsEmptyNotice.Visibility = actions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        WorkflowActionStatus.Text = string.Empty;

        var isMcpServerScenario = string.Equals(item.Scenario.Id, McpServerScenarioId, StringComparison.Ordinal);
        McpServerSection.Visibility = isMcpServerScenario ? Visibility.Visible : Visibility.Collapsed;
        if (isMcpServerScenario)
        {
            RefreshMcpServerUi();
        }
    }

    private async void RunWorkflowActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { DataContext: WorkflowActionListItem item })
        {
            return;
        }

        var parameters = new Dictionary<string, string>();
        if (item.HasParameter && !string.IsNullOrWhiteSpace(item.ParameterValue))
        {
            parameters[item.ParameterName!] = item.ParameterValue;
        }

        SetBusy(true);
        WorkflowActionStatus.Text = $"Running {item.Name}...";
        try
        {
            var result = await _workflowRunner.RunAsync(item.Id, parameters);
            WorkflowActionStatus.Text = result.Succeeded
                ? result.Summary
                : $"{result.Summary}{(string.IsNullOrWhiteSpace(result.Error) ? string.Empty : $" {result.Error}")}";
        }
        catch (Exception exception)
        {
            WorkflowActionStatus.Text = $"{item.Name} failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshMcpServerUi()
    {
        var entryPoint = McpServerManager.ResolveServerEntryPoint();
        if (entryPoint is null)
        {
            McpServerStatus.Text =
                "wdbgmcp was not found. Build it first: cd mcp\\wdbgmcp, then npm install and npm run build.";
            McpServerDetails.Text = string.Empty;
            StartMcpServerButton.IsEnabled = false;
            StopMcpServerButton.IsEnabled = false;
            CopyMcpServerDetailsButton.IsEnabled = false;
            return;
        }

        McpServerDetails.Text = McpServerManager.BuildConnectionDetails(entryPoint);
        CopyMcpServerDetailsButton.IsEnabled = true;

        if (_mcpServerManager.IsRunning)
        {
            McpServerStatus.Text = $"Running (PID {_mcpServerManager.ProcessId}). Node.js spawns this same server for you; an MCP client (e.g. an LLM assistant) connects to its own copy using the command below over stdio.";
            StartMcpServerButton.IsEnabled = false;
            StopMcpServerButton.IsEnabled = !_mcpServerBusy;
        }
        else
        {
            McpServerStatus.Text = "Stopped. Start it to verify it runs, or copy the details below into your MCP client's configuration (the client will launch its own copy).";
            StartMcpServerButton.IsEnabled = !_mcpServerBusy;
            StopMcpServerButton.IsEnabled = false;
        }
    }

    private async void StartMcpServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpServerBusy)
        {
            return;
        }

        var entryPoint = McpServerManager.ResolveServerEntryPoint();
        if (entryPoint is null)
        {
            RefreshMcpServerUi();
            return;
        }

        _mcpServerBusy = true;
        McpServerStatus.Text = "Starting wdbgmcp...";
        StartMcpServerButton.IsEnabled = false;
        StopMcpServerButton.IsEnabled = false;
        try
        {
            await _mcpServerManager.StartAsync(entryPoint);
        }
        catch (Exception exception)
        {
            McpServerStatus.Text = $"Could not start wdbgmcp: {exception.Message}";
        }
        finally
        {
            _mcpServerBusy = false;
        }

        RefreshMcpServerUi();
    }

    private async void StopMcpServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpServerBusy)
        {
            return;
        }

        _mcpServerBusy = true;
        McpServerStatus.Text = "Stopping wdbgmcp...";
        StartMcpServerButton.IsEnabled = false;
        StopMcpServerButton.IsEnabled = false;
        try
        {
            await _mcpServerManager.StopAsync();
        }
        catch (Exception exception)
        {
            McpServerStatus.Text = $"Could not stop wdbgmcp cleanly: {exception.Message}";
        }
        finally
        {
            _mcpServerBusy = false;
        }

        RefreshMcpServerUi();
    }

    private void McpServerManager_Exited(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (string.Equals(
                    (ScenarioList.SelectedItem as ScenarioListItem)?.Scenario.Id,
                    McpServerScenarioId,
                    StringComparison.Ordinal))
            {
                RefreshMcpServerUi();
            }
        });
    }

    private void CopyMcpServerDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(McpServerDetails.Text))
        {
            Clipboard.SetText(McpServerDetails.Text);
            McpServerStatus.Text = "Connection details copied to the clipboard.";
        }
    }

    private static string IconFor(string scenarioId) =>
        scenarioId switch
        {
            "crash" => "!",
            "performance" => "↗",
            "networking" => "⇄",
            "custom-logs" => "≡",
            "mcp-server" => "⚙",
            _ => "·"
        };

    private async Task RefreshPackageManagersAsync()
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        PackageManagerStatus.Text = "Checking for WinGet and Chocolatey...";
        try
        {
            _availablePackageManagers = (await Task.WhenAll(
                    Enum.GetValues<PackageManagerKind>()
                        .Select(manager => _toolInstaller.CheckAvailabilityAsync(manager))))
                .Where(result => result.IsAvailable)
                .ToArray();

            PackageManagerSelector.ItemsSource = _availablePackageManagers
                .Select(result => result.Manager)
                .ToArray();
            PackageManagerSelector.SelectedItem =
                _availablePackageManagers.FirstOrDefault(
                    result => result.Manager == PackageManagerKind.WinGet)?.Manager ??
                _availablePackageManagers.FirstOrDefault()?.Manager;

            if (_availablePackageManagers.Count == 0)
            {
                PackageManagerStatus.Text =
                    "No supported package manager was found. Install WinGet through Windows or install Chocolatey separately, then refresh.";
            }
            else
            {
                PackageManagerStatus.Text = string.Join(
                    Environment.NewLine,
                    _availablePackageManagers.Select(result =>
                        $"{result.Manager}: available{(string.IsNullOrWhiteSpace(result.Version) ? string.Empty : $" ({result.Version})")}"));
            }
        }
        catch (Exception exception)
        {
            _availablePackageManagers = [];
            PackageManagerSelector.ItemsSource = null;
            PackageManagerStatus.Text = $"Could not check package managers: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        ScenarioList.IsEnabled = !isBusy;
        DebugMachineSetupButton.IsEnabled = !isBusy;
        RefreshPackageManagersButton.IsEnabled = !isBusy;
        PackageManagerSelector.IsEnabled = !isBusy && _availablePackageManagers.Count > 0;
        WorkflowActionsList.IsEnabled = !isBusy;
        UpdateInstallButton();
        if (McpServerSection.Visibility == Visibility.Visible)
        {
            RefreshMcpServerUi();
        }
    }

    private void UpdateInstallButton()
    {
        InstallPrerequisitesButton.IsEnabled =
            !_isBusy &&
            _selectedPrerequisiteToolIds.Count > 0 &&
            PackageManagerSelector.SelectedItem is PackageManagerKind;
        InstallPrerequisitesButton.Content = _selectedPrerequisiteToolIds.Count == 0
            ? "Prerequisites not defined"
            : "Install prerequisites";
        DebugMachineSetupButton.IsEnabled =
            !_isBusy && _availablePackageManagers.Count > 0;
    }

    private static bool HasPackageId(ToolPackage package, PackageManagerKind manager) =>
        !string.IsNullOrWhiteSpace(PackageIdFor(package, manager));

    private static string? PackageIdFor(ToolPackage package, PackageManagerKind manager) =>
        manager switch
        {
            PackageManagerKind.WinGet => package.WinGetPackageId,
            PackageManagerKind.Chocolatey => package.ChocolateyPackageId,
            _ => null
        };

    private static string FormatInstallationResults(IEnumerable<ToolInstallationResult> results) =>
        string.Join(
            Environment.NewLine,
            results.Select(result =>
            {
                var package = ToolPackageCatalog.GetById(result.ToolId);
                var status = result.Status == WdbgToolkit.PackageManagement.InstallationStatus.Succeeded
                    ? "installed"
                    : result.Status.ToString();
                var detail = result.Error ??
                             (!string.IsNullOrWhiteSpace(result.StandardError)
                                 ? result.StandardError.Trim()
                                 : string.Empty);
                return string.IsNullOrWhiteSpace(detail)
                    ? $"{package.Name}: {status}."
                    : $"{package.Name}: {status}. {detail}";
            }));

    private sealed record ScenarioListItem(DiagnosticScenario Scenario, string Icon)
    {
        public string Name => Scenario.Name;

        public string Description => Scenario.Description;
    }

    private sealed class WorkflowActionListItem : INotifyPropertyChanged
    {
        private string _parameterValue = string.Empty;

        public WorkflowActionListItem(WorkflowActionDefinition definition)
        {
            Id = definition.Id;
            Name = definition.Name;
            Description = definition.Description;

            var requiredParameter = definition.Parameters.FirstOrDefault(parameter => parameter.Required);
            if (requiredParameter is not null)
            {
                ParameterName = requiredParameter.Name;
                ParameterHint = requiredParameter.Description;
            }
        }

        public string Id { get; }

        public string Name { get; }

        public string Description { get; }

        public string? ParameterName { get; }

        public string? ParameterHint { get; }

        public bool HasParameter => ParameterName is not null;

        public string ParameterValue
        {
            get => _parameterValue;
            set
            {
                if (_parameterValue == value)
                {
                    return;
                }

                _parameterValue = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ParameterValue)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
