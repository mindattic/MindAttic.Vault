<#
.SYNOPSIS
  Publishes MindAttic.Vault.Desktop.Wpf as one self-contained single-file win-x64 exe into
  %LOCALAPPDATA%\MindAttic\Vault.Desktop\ (VaultDesktopContract.DefaultExePath, where VaultSettings.Open finds it) and
  registers the per-user mindattic-vault:// protocol handler (HKCU only) for it.
.PARAMETER Launch
  Open the app when done.
.PARAMETER Configuration
  Build configuration (Release by default).
#>
param([switch]$Launch, [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$name = 'MindAttic.Vault.Desktop.Wpf'

# Same rule as VaultPaths.LocalRoot: MINDATTIC_VAULT_LOCAL_ROOT verbatim when set, else %LOCALAPPDATA%\MindAttic.
$localRoot = if ($env:MINDATTIC_VAULT_LOCAL_ROOT) { $env:MINDATTIC_VAULT_LOCAL_ROOT } else { Join-Path $env:LOCALAPPDATA 'MindAttic' }
$dest = Join-Path $localRoot 'Vault.Desktop'
$exe = Join-Path $dest "$name.exe"

# The running copy holds the exe open; stop only the installed one, never a dev build.
Get-Process $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $exe) } | ForEach-Object {
  Write-Host "Stopping the running $name ($($_.Id))"
  $_ | Stop-Process -Force
  $_.WaitForExit(10000) | Out-Null
}

Write-Host "Publishing $name to $dest"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
dotnet publish (Join-Path $repo "$name\$name.csproj") -c $Configuration -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o $dest
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# The exe registers mindattic-vault:// under HKCU\Software\Classes for itself and exits (it re-checks on every start too).
$p = Start-Process -FilePath $exe -ArgumentList '--register-protocol' -Wait -PassThru -WindowStyle Hidden
if ($p.ExitCode -ne 0) { Write-Warning "Protocol registration returned $($p.ExitCode)" } else { Write-Host 'Registered mindattic-vault:// for this user' }

Write-Host "Installed $exe"
if ($Launch) { Start-Process -FilePath $exe }
