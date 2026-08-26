# Build IronNestAgentBridge.
#
# Default: builds straight into the game's Mods folder (csproj OutputPath =
# $(GameDir)\Mods\). That copy fails while the game is running, because
# MelonLoader holds the loaded DLL open, and a half-finished copy leaves a
# stale mod deployed - so a running game is a hard refusal.
#
# -Staging: builds to bin\staging\ instead. Nothing under Mods\ is touched, so
# this is allowed while the game runs; it is the only way to verify that the
# code compiles without closing the game. Copy the DLL into Mods\ by hand once
# the game is closed.
#
#   .\tools\Build.ps1                          # Release build + deploy to Mods\
#   .\tools\Build.ps1 -Configuration Debug
#   .\tools\Build.ps1 -Staging                 # compile check only, game may run
#
# ASCII only on purpose: PowerShell 5.1 reads a BOM-less .ps1 as the ANSI code
# page, so non-ASCII text here would be mojibake on a Chinese Windows install.

param(
    [string]$Configuration = "Release",
    [switch]$Staging
)

$project = Join-Path $PSScriptRoot "..\IronNestAgentBridge.csproj"
$stagingPath = "bin\staging\"

$game = Get-Process "Iron Nest Heavy Turret Simulator" -ErrorAction SilentlyContinue
if ($game -and (-not $Staging)) {
    Write-Error "game is running (pid $($game.Id)) - close it first, Mods\IronNestAgentBridge.dll is locked"
    exit 1
}

$arguments = @("build", $project, "-c", $Configuration, "-m:10")
if ($Staging) {
    $arguments += "-p:OutputPath=$stagingPath"
}

dotnet @arguments
$code = $LASTEXITCODE

if ($Staging -and ($code -eq 0)) {
    $resolved = Join-Path (Split-Path -Parent $project) $stagingPath
    Write-Host "staging build only - nothing deployed. Output: $resolved"
    Write-Host "close the game, then copy IronNestAgentBridge.dll into <GameDir>\Mods\"
}

exit $code
