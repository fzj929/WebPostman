param(
    [Parameter(Position = 0)]
    [ValidateRange(1, 65535)]
    [int]$Port = 5078,

    [Parameter(Position = 1)]
    [ValidateSet('127.0.0.1', '0.0.0.0', 'localhost')]
    [string]$ListenAddress = '127.0.0.1'
)

$ErrorActionPreference = 'Stop'
$serviceName = 'WebPostman'
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $publishDirectory 'WebPostman.Api.exe'
$dataDirectory = Join-Path $publishDirectory 'App_Data'
$listenUrl = "http://${ListenAddress}:$Port"

function Invoke-CheckedCommand {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File failed with exit code $LASTEXITCODE."
    }
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Please run this script from an elevated PowerShell window.'
    }
}

Assert-Administrator
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable not found: $executable. Keep this script in the scripts folder of the published application."
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
Invoke-CheckedCommand 'icacls.exe' @($publishDirectory, '/grant', '*S-1-5-19:(OI)(CI)RX', '/T')
Invoke-CheckedCommand 'icacls.exe' @($dataDirectory, '/grant', '*S-1-5-19:(OI)(CI)M', '/T')

if (-not $service) {
    New-Service -Name $serviceName -DisplayName 'WebPostman API Workspace' -BinaryPathName "`"$executable`"" -StartupType Automatic | Out-Null
}
Invoke-CheckedCommand 'sc.exe' @(
    'config', $serviceName,
    'binPath=', "`"$executable`"",
    'start=', 'auto',
    'obj=', 'NT AUTHORITY\LocalService'
)
Invoke-CheckedCommand 'sc.exe' @('description', $serviceName, 'Local API request workspace')

$registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
New-ItemProperty -Path $registryPath -Name Environment -PropertyType MultiString -Value @(
    "WebPostman__DataDirectory=$dataDirectory",
    "WebPostman__ListenUrl=$listenUrl"
) -Force | Out-Null

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "WebPostman service installed and running at $listenUrl"
Write-Host "Application directory: $publishDirectory"
Write-Host "Data directory: $dataDirectory"
