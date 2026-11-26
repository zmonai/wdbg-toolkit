param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigBase64
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$config = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ConfigBase64)) | ConvertFrom-Json
if ($config.action -notin @('status', 'install_enable', 'disable')) {
    throw 'Unsupported SSH action.'
}
$ruleName = 'WdbgToolkit-SSH-In'

function Get-SshStatus {
    $service = Get-Service -Name sshd -ErrorAction SilentlyContinue
    $rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    $addresses = @()
    if ($rule) {
        $addresses = @($rule | Get-NetFirewallAddressFilter | Select-Object -ExpandProperty RemoteAddress)
    }
    [pscustomobject]@{
        installed = [bool]$service
        serviceStatus = if ($service) { [string]$service.Status } else { 'NotInstalled' }
        startupType = if ($service) { [string]$service.StartType } else { $null }
        toolkitFirewallEnabled = [bool]($rule -and $rule.Enabled -eq 'True')
        allowedClients = $addresses
        notice = 'Toolkit rule covers TCP port 22 only. Other firewall rules and sshd_config may permit different access; review them separately. Existing authentication settings are unchanged.'
    }
}

if ($config.action -eq 'status') {
    Get-SshStatus | ConvertTo-Json -Depth 4
    exit 0
}
if ($config.confirmed -ne $true) {
    throw 'Explicit confirmation is required to change SSH.'
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run as administrator to install or configure SSH.'
}

$clients = @()
if ($config.action -eq 'install_enable') {
    $clients = @($config.allowedClients)
    if ($clients.Count -lt 1 -or $clients.Count -gt 64) {
        throw 'Supply 1 to 64 explicit client IP addresses or CIDR subnets.'
    }
    foreach ($client in $clients) {
        $parts = ([string]$client).Split('/')
        $ip = $null
        if ($parts.Count -gt 2 -or $parts[0].Contains('%') -or
            -not [Net.IPAddress]::TryParse($parts[0], [ref]$ip)) {
            throw "Invalid client IP/CIDR: $client"
        }
        if ($ip.Equals([Net.IPAddress]::Any) -or $ip.Equals([Net.IPAddress]::IPv6Any)) {
            throw 'Unspecified/all-client addresses are not allowed.'
        }
        if ($parts.Count -eq 2) {
            $prefix = 0
            $maximum = if ($ip.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) { 32 } else { 128 }
            if (-not [int]::TryParse($parts[1], [ref]$prefix) -or $prefix -lt 1 -or $prefix -gt $maximum) {
                throw "Invalid CIDR prefix: $client"
            }
        }
    }
}

$mutex = New-Object Threading.Mutex($false, 'Global\WdbgToolkit-SshSetup')
$locked = $false
try {
    try {
        $locked = $mutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $locked = $true
    }
    if (-not $locked) { throw 'Another SSH setup operation is running.' }

    if ($config.action -eq 'disable') {
        $service = Get-Service -Name sshd -ErrorAction SilentlyContinue
        if ($service) {
            Stop-Service -Name sshd
            Set-Service -Name sshd -StartupType Disabled
        }
        if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) {
            Disable-NetFirewallRule -Name $ruleName | Out-Null
        }
    }
    else {
        if (-not (Get-Service -Name sshd -ErrorAction SilentlyContinue)) {
            $capabilityName = 'OpenSSH.Server~~~~0.0.1.0'
            $capability = Get-WindowsCapability -Online -Name $capabilityName
            if ($capability.State -ne 'Installed') {
                $installation = Add-WindowsCapability -Online -Name $capabilityName
                if ($installation.RestartNeeded) {
                    throw 'OpenSSH was installed but requires a restart. Restart manually, then rerun setup. SSH has not been enabled by this operation.'
                }
            }
        }
        if (-not (Get-Service -Name sshd -ErrorAction SilentlyContinue)) {
            throw 'OpenSSH installation did not register sshd. Review Windows servicing logs; no alternate installer was downloaded.'
        }
        # Replace our own rule only; disable the known Windows default broad rule.
        if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) {
            Remove-NetFirewallRule -Name $ruleName
        }
        New-NetFirewallRule -Name $ruleName -DisplayName 'Windows Debug Toolkit SSH (selected clients)' `
            -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22 `
            -RemoteAddress $clients -Profile Any -Enabled True | Out-Null
        if (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue) {
            Disable-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' | Out-Null
        }
        Set-Service -Name sshd -StartupType Automatic
        Start-Service -Name sshd
        (Get-Service -Name sshd).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(20))
        $sshd = Get-CimInstance Win32_Service -Filter "Name='sshd'"
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 22 -ErrorAction SilentlyContinue |
            Where-Object { $_.OwningProcess -eq $sshd.ProcessId })
        if ($listeners.Count -eq 0) {
            throw 'sshd is running but does not own a TCP port 22 listener. Review sshd_config and event logs; setup may have partially completed.'
        }
    }
    Get-SshStatus | ConvertTo-Json -Depth 4
}
finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
