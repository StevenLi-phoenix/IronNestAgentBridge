# Build IronNestAgentBridge straight into the game's Mods folder.
# Refuses to build while the game is running (the loaded DLL is locked,
# so the copy would fail and leave a stale mod deployed).
#
#   .\tools\Build.ps1                 # Release build + deploy
#   .\tools\Build.ps1 -Configuration Debug

param(
    [string]$Configuration = "Release"
)

$game = Get-Process "Iron Nest Heavy Turret Simulator" -ErrorAction SilentlyContinue
if ($game) {
    Write-Error "game is running (pid $($game.Id)) - close it first, Mods\IronNestAgentBridge.dll is locked"
    exit 1
}

$project = Join-Path $PSScriptRoot "..\IronNestAgentBridge.csproj"
dotnet build $project -c $Configuration -m:10
exit $LASTEXITCODE
