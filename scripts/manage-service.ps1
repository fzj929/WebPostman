param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('install', 'uninstall', 'start', 'stop', 'restart', 'status', 'run')]
    [string]$Action,

    [Parameter(Position = 1)]
    [ValidateRange(1, 65535)]
    [int]$Port = 5078
)

$ErrorActionPreference = 'Stop'
$serviceName = 'WebPostman'
$projectRoot = Split-Path -Parent $PSScriptRoot
$webProject = Join-Path $projectRoot 'WebPostman.Web'
$apiProject = Join-Path $projectRoot 'WebPostman.Api/WebPostman.Api.csproj'
$installDirectory = Join-Path $env:ProgramFiles 'WebPostman'
$dataDirectory = Join-Path $env:ProgramData 'WebPostman'
$listenUrl = "http://127.0.0.1:$Port"

function Invoke-CheckedCommand {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File failed with exit code $LASTEXITCODE."
    }
}

function Build-WebApplication {
    $packageJson = Join-Path $webProject 'package.json'
    $packageLock = Join-Path $webProject 'package-lock.json'
    if (-not (Test-Path -LiteralPath $packageJson -PathType Leaf)) {
        throw "Frontend project not found: $packageJson"
    }
    if (Test-Path -LiteralPath $packageLock -PathType Leaf) {
        Invoke-CheckedCommand 'npm.cmd' @('--prefix', $webProject, 'ci')
    } else {
        Write-Warning "package-lock.json was not found. Falling back to npm install: $webProject"
        Invoke-CheckedCommand 'npm.cmd' @('--prefix', $webProject, 'install')
    }
    Invoke-CheckedCommand 'npm.cmd' @('--prefix', $webProject, 'run', 'build')
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Please run this command from an elevated PowerShell window.'
    }
}

function Get-InstalledService {
    Get-Service -Name $serviceName -ErrorAction SilentlyContinue
}

function Stop-InstalledService {
    $service = Get-InstalledService
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

switch ($Action) {
    'install' {
        Assert-Administrator
        Build-WebApplication
        Stop-InstalledService
        New-Item -ItemType Directory -Path $installDirectory, $dataDirectory -Force | Out-Null
        Invoke-CheckedCommand 'dotnet' @('publish', $apiProject, '-c', 'Release', '-o', $installDirectory)

        # LocalService must be able to create and update its SQLite database.
        Invoke-CheckedCommand 'icacls.exe' @($dataDirectory, '/grant', '*S-1-5-19:(OI)(CI)M', '/T')
        $executable = Join-Path $installDirectory 'WebPostman.Api.exe'
        if (-not (Get-InstalledService)) {
            New-Service -Name $serviceName -DisplayName 'WebPostman API Workspace' -BinaryPathName "`"$executable`"" -StartupType Automatic | Out-Null
        }
        Invoke-CheckedCommand 'sc.exe' @(
            'config', $serviceName,
            'binPath=', "`"$executable`"",
            'start=', 'auto',
            'obj=', 'NT AUTHORITY\LocalService'
        )
        $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
        New-ItemProperty -Path $registryPath -Name Environment -PropertyType MultiString -Value @(
            "WebPostman__DataDirectory=$dataDirectory",
            "WebPostman__ListenUrl=$listenUrl"
        ) -Force | Out-Null
        Start-Service -Name $serviceName
        (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        Write-Host "WebPostman is running at $listenUrl"
        Write-Host "Data directory: $dataDirectory"
    }
    'uninstall' {
        Assert-Administrator
        if (Get-InstalledService) {
            Stop-InstalledService
            Invoke-CheckedCommand 'sc.exe' @('delete', $serviceName)
            Write-Host "Service $serviceName removed. Published files and data were preserved."
        } else {
            Write-Host "Service $serviceName is not installed."
        }
    }
    'start' {
        Assert-Administrator
        Start-Service -Name $serviceName
        Get-Service -Name $serviceName
    }
    'stop' {
        Assert-Administrator
        Stop-InstalledService
        Get-Service -Name $serviceName
    }
    'restart' {
        Assert-Administrator
        Stop-InstalledService
        Start-Service -Name $serviceName
        Get-Service -Name $serviceName
    }
    'status' {
        $service = Get-InstalledService
        if ($service) { $service | Format-List Name, DisplayName, Status, StartType }
        else { Write-Host "Service $serviceName is not installed." }
    }
    'run' {
        Build-WebApplication
        $env:WebPostman__ListenUrl = $listenUrl
        Write-Host "Running WebPostman in the foreground at $listenUrl (Ctrl+C to stop)."
        Invoke-CheckedCommand 'dotnet' @('run', '--project', $apiProject, '--no-launch-profile')
    }
}
