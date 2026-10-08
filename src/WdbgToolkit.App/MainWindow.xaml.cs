using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
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
    private string? _chocolateyInstallationMessage;

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
        if (Environment.GetCommandLineArgs().Contains("--start-mcp-admin", StringComparer.Ordinal))
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                MessageBox.Show(this, "Administrator launch was requested, but this process is not elevated.",
                    "MCP administrator startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ScenarioList.SelectedItem = ScenarioList.Items.Cast<ScenarioListItem>()
                .Single(item => item.Scenario.Id == McpServerScenarioId);
            await StartMcpServerAsync();
        }
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

    private async void InstallChocolateyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            _chocolateyInstallationMessage = "Run Windows Debug Toolkit as administrator, then install Chocolatey.";
            UpdateChocolateySetup();
            MessageBox.Show(this, _chocolateyInstallationMessage, "Administrator required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(this,
                "Install Chocolatey system-wide using WinGet package Chocolatey.Chocolatey? This downloads software and accepts the package and source agreements.",
                "Confirm Chocolatey installation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        _chocolateyInstallationMessage = "Installing Chocolatey through WinGet...";
        UpdateChocolateySetup();
        try
        {
            var result = await _toolInstaller.InstallAsync("chocolatey", PackageManagerKind.WinGet, userConfirmed: true);
            _chocolateyInstallationMessage = FormatInstallationResults([result]);
        }
        catch (Exception exception)
        {
            _chocolateyInstallationMessage = $"Chocolatey installation failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }

        await RefreshPackageManagersAsync();
        if (!_availablePackageManagers.Any(item => item.Manager == PackageManagerKind.Chocolatey))
        {
            _chocolateyInstallationMessage += Environment.NewLine +
                "Chocolatey is not available yet. Review the installation result, then refresh package managers or restart the app.";
            UpdateChocolateySetup();
        }
    }

    private void UpdateChocolateySetup()
    {
        var installed = _availablePackageManagers.Any(item => item.Manager == PackageManagerKind.Chocolatey);
        var hasWinGet = _availablePackageManagers.Any(item => item.Manager == PackageManagerKind.WinGet);
        InstallChocolateyButton.IsEnabled = !_isBusy && hasWinGet && !installed;
        InstallChocolateyButton.Content = installed ? "Chocolatey installed" : "Install Chocolatey";
        var availability = installed ? "Chocolatey is available." :
                hasWinGet ? "WinGet is available. Chocolatey can be installed here." :
                "WinGet is unavailable. Install App Installer through Windows, then refresh package managers.";
        ChocolateySetupStatus.Text = string.IsNullOrWhiteSpace(_chocolateyInstallationMessage)
            ? availability
            : $"{_chocolateyInstallationMessage}{Environment.NewLine}{availability}";
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
                    $"Use Package Manager Setup on the left to install Chocolatey with WinGet, then retry installing {package.Name}.";
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
                "wdbgmcp was not found. For an installed app, repair or reinstall the latest Windows Debug Toolkit MSI. For a development build: cd mcp\\wdbgmcp, then npm ci and npm run build.";
            McpServerDetails.Text = string.Empty;
            StartMcpServerButton.IsEnabled = false;
            StartMcpServerAsAdminButton.IsEnabled = false;
            StopMcpServerButton.IsEnabled = false;
            CopyMcpServerDetailsButton.IsEnabled = false;
            return;
        }

        var port = _mcpServerManager.IsRunning ? _mcpServerManager.Port : McpServerManager.DefaultPort;
        McpServerDetails.Text = _mcpServerManager.IsRunning
            ? McpServerManager.BuildConnectionDetails(port, _mcpServerManager.ConnectionToken)
            : string.Empty;
        CopyMcpServerDetailsButton.IsEnabled = _mcpServerManager.IsRunning;
        StartMcpServerAsAdminButton.IsEnabled = !_mcpServerBusy && !_mcpServerManager.IsRunning;

        if (_mcpServerManager.IsRunning)
        {
            var url = $"http://{McpServerManager.ResolveMachineAddress()}:{_mcpServerManager.Port}{McpServerManager.McpPath}";
            using var identity = WindowsIdentity.GetCurrent();
            var permission = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                ? "administrator" : "standard user";
            McpServerStatus.Text = $"Running as {permission} (PID {_mcpServerManager.ProcessId}) at {url}. Authenticated clients can execute commands with these permissions. Keep the connection token private.";
            StartMcpServerButton.IsEnabled = false;
            StopMcpServerButton.IsEnabled = !_mcpServerBusy;
        }
        else
        {
            McpServerStatus.Text = "Stopped. Start this server first, then copy its verified connection details into your MCP client. Details from another toolkit instance will use a different token.";
            StartMcpServerButton.IsEnabled = !_mcpServerBusy;
            StopMcpServerButton.IsEnabled = false;
        }
    }

    private async void StartMcpServerButton_Click(object sender, RoutedEventArgs e)
    {
        await StartMcpServerAsync();
    }

    private async void StartMcpServerAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpServerBusy || _mcpServerManager.IsRunning)
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            await StartMcpServerAsync();
            return;
        }

        if (MessageBox.Show(this,
                "Relaunch Windows Debug Toolkit as administrator and start the MCP server? Windows will request UAC approval. AI commands will have administrator access to this machine. This window will close after the elevated app launches; copy new connection details from that app.",
                "Start MCP as administrator", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var executable = Environment.ProcessPath ??
                throw new InvalidOperationException("Could not determine the toolkit executable path.");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            // Framework-dependent launches through dotnet need the app assembly too.
            if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(typeof(MainWindow).Assembly.Location);
            }
            startInfo.ArgumentList.Add("--start-mcp-admin");
            using var elevated = Process.Start(startInfo) ??
                throw new InvalidOperationException("The elevated toolkit process was not created.");
            Close();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            McpServerStatus.Text = "Administrator startup cancelled. Windows UAC approval was not granted.";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            McpServerStatus.Text = $"Could not start the toolkit as administrator: {exception.Message}";
        }
    }

    private async Task StartMcpServerAsync()
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

        if (MessageBox.Show(
                this,
                "Starting wdbgmcp lets AI clients with the connection token execute commands on this machine, including commands that modify files or settings. Commands inherit this app's permissions (administrator if elevated).\n\nOnly share the token with trusted clients. HTTP is unencrypted: use a trusted network or a secure tunnel/TLS proxy.\n\nStart the server?",
                "Allow MCP command execution",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _mcpServerBusy = true;
        McpServerStatus.Text = "Starting wdbgmcp...";
        StartMcpServerButton.IsEnabled = false;
        StartMcpServerAsAdminButton.IsEnabled = false;
        StopMcpServerButton.IsEnabled = false;
        string? startupError = null;
        try
        {
            await _mcpServerManager.StartAsync(entryPoint);
        }
        catch (Exception exception)
        {
            startupError = $"Could not start wdbgmcp: {exception.Message}";
        }
        finally
        {
            _mcpServerBusy = false;
        }

        RefreshMcpServerUi();
        if (startupError is not null)
        {
            McpServerStatus.Text = startupError;
        }
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
        UpdateChocolateySetup();
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
