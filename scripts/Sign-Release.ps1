<#
.SYNOPSIS
    Builds Windows Debug Toolkit, signs its binaries and MSI with a self-signed
    code-signing certificate, and verifies the signatures.

.DESCRIPTION
    Reuses a valid code-signing certificate with the requested subject from
    Cert:\CurrentUser\My, or creates one. The public certificate is exported to
    the output directory so test machines can trust it.

    Self-signed signatures are suitable only for internal or test distribution.
    Windows does not trust them unless the exported certificate is installed in
    Trusted Root Certification Authorities and Trusted Publishers.

.EXAMPLE
    .\scripts\Sign-Release.ps1

.EXAMPLE
    .\scripts\Sign-Release.ps1 -PfxPassword (Read-Host -AsSecureString 'PFX password')

.EXAMPLE
    # Run elevated to trust the certificate on this machine.
    .\scripts\Sign-Release.ps1 -TrustCertificate
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $CertificateSubject = 'CN=Windows Debug Toolkit (Self-Signed)',

    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $CertificateThumbprint,

    [ValidateRange(1, 10)]
    [int] $ValidityYears = 3,

    [string] $OutputDirectory,

    [securestring] $PfxPassword,

    [string] $TimestampUrl = 'http://timestamp.digicert.com',

    [switch] $NoTimestamp,

    [switch] $TrustCertificate,

    [string] $SignToolPath,

    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\WdbgToolkit.App\WdbgToolkit.App.csproj'
$installerProject = Join-Path $repoRoot 'installer\WdbgToolkit.Installer\WdbgToolkit.Installer.wixproj'
$appOutput = Join-Path $repoRoot "src\WdbgToolkit.App\bin\$Configuration\net10.0-windows"
$msiPath = Join-Path $repoRoot "installer\WdbgToolkit.Installer\bin\$Configuration\WdbgToolkit.Installer.msi"
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\signing'
}

function Invoke-Checked {
    param([string] $FilePath, [string[]] $Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Resolve-SignTool {
    if ($SignToolPath) {
        if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
            throw "signtool.exe was not found at '$SignToolPath'."
        }
        return (Resolve-Path -LiteralPath $SignToolPath).Path
    }

    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -LiteralPath $kitsBin -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -as [version] } |
        Sort-Object { [version] $_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $candidate) {
        throw 'signtool.exe was not found. Install the Windows SDK signing tools or pass -SignToolPath.'
    }
    return $candidate
}

function Test-CodeSigningCertificate {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate)
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $hasCodeSigningUsage = @($Certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq $codeSigningOid }).Count -gt 0
    return $Certificate.HasPrivateKey -and
        $Certificate.NotBefore -le (Get-Date) -and
        $Certificate.NotAfter -gt (Get-Date).AddDays(1) -and
        $hasCodeSigningUsage
}

function Get-SigningCertificate {
    if ($CertificateThumbprint) {
        $path = "Cert:\CurrentUser\My\$($CertificateThumbprint.ToUpperInvariant())"
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Certificate $CertificateThumbprint was not found in Cert:\CurrentUser\My."
        }
        $certificate = Get-Item -LiteralPath $path
        if (-not (Test-CodeSigningCertificate $certificate)) {
            throw "Certificate $CertificateThumbprint is not a currently valid code-signing certificate with a private key."
        }
        return $certificate
    }

    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $CertificateSubject -and (Test-CodeSigningCertificate $_) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if ($existing) {
        Write-Host "Reusing certificate $($existing.Thumbprint) (expires $($existing.NotAfter.ToString('yyyy-MM-dd')))."
        return $existing
    }

    Write-Host "Creating self-signed code-signing certificate '$CertificateSubject'."
    $exportPolicy = if ($PfxPassword) { 'Exportable' } else { 'NonExportable' }
    return New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $CertificateSubject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy $exportPolicy `
        -NotAfter (Get-Date).AddYears($ValidityYears)
}

function Add-TrustedCertificate {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate)
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '-TrustCertificate requires an elevated PowerShell session.'
    }
    foreach ($storeName in 'Root', 'TrustedPublisher') {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')
        $store.Open('ReadWrite')
        try {
            if (-not $store.Certificates.Find('FindByThumbprint', $Certificate.Thumbprint, $false).Count) {
                $store.Add($Certificate)
            }
        }
        finally {
            $store.Close()
        }
    }
    Write-Host 'Trusted the certificate in LocalMachine Root and TrustedPublisher stores.'
}

function Invoke-Sign {
    param([string[]] $Files, [string] $Description)
    $arguments = @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint, '/s', 'My', '/d', $Description)
    if (-not $NoTimestamp) {
        $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
    }
    Invoke-Checked $signTool ($arguments + $Files)
}

function Assert-Signed {
    param([string[]] $Files)
    foreach ($file in $Files) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw "'$file' is not signed with certificate $($certificate.Thumbprint)."
        }
        # UnknownError means the chain ends in an untrusted self-signed root; the file hash is still intact.
        if ($signature.Status -notin 'Valid', 'UnknownError') {
            throw "'$file' signature status is $($signature.Status): $($signature.StatusMessage)"
        }
        if ($trusted -and $signature.Status -ne 'Valid') {
            throw "'$file' signature is not trusted: $($signature.StatusMessage)"
        }
        if (-not $NoTimestamp -and -not $signature.TimeStamperCertificate) {
            throw "'$file' signature has no timestamp."
        }
        Write-Host "Verified $([IO.Path]::GetFileName($file)): $($signature.Status)"
    }
}

$signTool = Resolve-SignTool
$certificate = Get-SigningCertificate
$trusted = $false

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$cerPath = Join-Path $OutputDirectory 'WdbgToolkit-CodeSigning.cer'
Export-Certificate -Cert $certificate -FilePath $cerPath -Type CERT | Out-Null
Write-Host "Exported public certificate to $cerPath"

if ($PfxPassword) {
    $pfxPath = Join-Path $OutputDirectory 'WdbgToolkit-CodeSigning.pfx'
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $PfxPassword | Out-Null
    Write-Warning "Exported private key to $pfxPath. Keep it secret and never commit it."
}

if ($TrustCertificate) {
    Add-TrustedCertificate $certificate
    $trusted = $true
}

if (-not $SkipBuild) {
    Invoke-Checked dotnet @('build', $appProject, '-c', $Configuration)
}

if (-not (Test-Path -LiteralPath $appOutput -PathType Container)) {
    throw "App output '$appOutput' was not found. Build the app or omit -SkipBuild."
}
$binaries = @(Get-ChildItem -LiteralPath $appOutput -File |
    Where-Object { $_.Extension -in '.exe', '.dll' } |
    Select-Object -ExpandProperty FullName)
if (-not $binaries.Count) {
    throw "No binaries found in '$appOutput'."
}

Invoke-Sign $binaries 'Windows Debug Toolkit'
Assert-Signed $binaries

# Package the already-signed binaries without rebuilding (and overwriting) them.
Invoke-Checked dotnet @('build', $installerProject, '-c', $Configuration, '-p:BuildProjectReferences=false')

Invoke-Sign @($msiPath) 'Windows Debug Toolkit Installer'
Assert-Signed @($msiPath)

Write-Host ''
Write-Host "Signed installer: $msiPath"
Write-Host "Certificate thumbprint: $($certificate.Thumbprint)"
if (-not $trusted) {
    Write-Host "To trust these signatures on a test machine, import $cerPath into"
    Write-Host 'Local Machine > Trusted Root Certification Authorities and Trusted Publishers.'
}
