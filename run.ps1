#Requires -Version 5.1
<#
.SYNOPSIS
    MindAttic.Vault's run menu: 1) Redeploy  2) Run Debug  3) Run Release  (the MindAttic standard, MAT-T279).

.DESCRIPTION
    A thin wrapper over the shared deploy-lib.ps1 (Invoke-AppRun, MAT-T299). run.bat beside it (written by
    deploy-lib.ps1's Write-RunBat) opens the same menu on a double-click.
      1) Redeploy     builds Vault.Desktop.Wpf to R:\MindAttic\Apps\MindAttic.Vault\{Debug,Release}
                      Debug   framework-dependent (fast rebuilds, the dev/test copy)
                      Release self-contained single-file win-x64 (no .NET runtime needed)
      2) Run Debug    R:\...\Debug\MindAttic.Vault.Desktop.Wpf.exe in this console; extra arguments pass through
      3) Run Release  R:\...\Release\MindAttic.Vault.Desktop.Wpf.exe, started the way a double-click would start it
    Picks combine: 13 is Deploy & Run.

    Vault's own differences, in the override block below:
      - Plan names the Desktop.Wpf project; the repo's other exes aren't deployed.
      - PostBuild, after the Release build, installs that exe into %LOCALAPPDATA%\MindAttic\Vault.Desktop\
        (VaultDesktopContract.DefaultExePath, where VaultSettings.Open finds it; MINDATTIC_VAULT_LOCAL_ROOT replaces
        %LOCALAPPDATA%\MindAttic as in VaultPaths.LocalRoot), stopping only that installed copy first, and registers
        the per-user mindattic-vault:// protocol handler (HKCU only) for it.

    Callers from before the shared menu: -Launch means -Redeploy -RunRelease.

.EXAMPLE
    .\run.ps1                    the menu
    .\run.ps1 -Redeploy          build and deploy both configs, install the Release copy, register the protocol
    .\run.ps1 -Redeploy -RunRelease -DryRun
#>
param(
    [switch]$Redeploy,
    [switch]$RunDebug,
    [switch]$RunRelease,
    [switch]$DryRun,
    [switch]$Launch,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs = @()
)
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$Override = @{
    Exe       = 'MindAttic.Vault.Desktop.Wpf.exe'
    Plan      = @{ Kind = 'dotnet'; Projects = @("$repoRoot\MindAttic.Vault.Desktop.Wpf\MindAttic.Vault.Desktop.Wpf.csproj") }
    PostBuild = {
        param($Config, $Out)
        if ($Config -ne 'Release') { return }
        $name = 'MindAttic.Vault.Desktop.Wpf'
        $localRoot = if ($env:MINDATTIC_VAULT_LOCAL_ROOT) { $env:MINDATTIC_VAULT_LOCAL_ROOT } else { Join-Path $env:LOCALAPPDATA 'MindAttic' }
        $dest = Join-Path $localRoot 'Vault.Desktop'
        $exe = Join-Path $dest "$name.exe"
        Get-Process $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $exe) } | ForEach-Object {
            Write-Host "  Stopping the installed $name ($($_.Id))" -ForegroundColor DarkGray
            $_ | Stop-Process -Force
            $_.WaitForExit(10000) | Out-Null
        }
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item -LiteralPath (Join-Path $Out "$name.exe") -Destination $exe -Force
        Write-Host "  Installed $exe" -ForegroundColor DarkGray
        $p = Start-Process -FilePath $exe -ArgumentList '--register-protocol' -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -ne 0) { Write-Warning "Protocol registration returned $($p.ExitCode)" } else { Write-Host '  Registered mindattic-vault:// for this user' -ForegroundColor DarkGray }
    }
}

function Find-DeployLib {
    if ($env:MINDATTIC_DEPLOY_LIB -and (Test-Path -LiteralPath $env:MINDATTIC_DEPLOY_LIB)) { return $env:MINDATTIC_DEPLOY_LIB }
    $roots = @()
    $d = $PSScriptRoot
    while ($d) { $roots += $d; $d = Split-Path -Parent $d }
    $roots += 'D:\Projects\MindAttic'
    foreach ($r in $roots) {
        foreach ($rel in 'MindAttic.Agency\shared\deploy-lib.ps1', 'mindattic-agent-standard\shared\deploy-lib.ps1') {
            $p = Join-Path $r $rel
            if (Test-Path -LiteralPath $p) { return $p }
        }
    }
    throw 'deploy-lib.ps1 not found: clone MindAttic.Agency beside this repo, or set MINDATTIC_DEPLOY_LIB to its path.'
}

. (Find-DeployLib)
Invoke-AppRun -Repo $PSScriptRoot -Redeploy:($Redeploy -or $Launch) -RunDebug:$RunDebug -RunRelease:($RunRelease -or $Launch) `
    -DryRun:$DryRun -AppArgs $AppArgs -Override $Override
