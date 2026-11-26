# Starts Roster Riot on this PC and opens it in the default browser.
# Used by the "Roster Riot" shortcut in the Chase folder. Close this window to stop the app.
# If the app is already running, it just opens the browser.
param([switch]$NoBrowser)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$port = 5084
$url = "http://localhost:$port/"
$Host.UI.RawUI.WindowTitle = "Roster Riot"

function Test-Up {
    try { (Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { $false }
}

Write-Host ""
Write-Host "  ROSTER RIOT" -ForegroundColor Green
Write-Host "  Your team. Every move." -ForegroundColor DarkGray
Write-Host ""

if (Test-Up) {
    Write-Host "  Already running at $url"
    if (-not $NoBrowser) { Start-Process $url }
    Start-Sleep -Seconds 2
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "  .NET isn't installed. Get the .NET 10 SDK from https://dotnet.microsoft.com/download" -ForegroundColor Red
    Read-Host "  Press Enter to close"
    return
}

function Start-Server {
    Start-Process dotnet -ArgumentList @("run", "--project", "`"$root\RosterRiot`"", "--urls", $url.TrimEnd('/')) `
        -WorkingDirectory $root -NoNewWindow -PassThru
}

Write-Host "  Starting up (the first time takes about 20 seconds)..."
$server = Start-Server
$retried = $false

$deadline = (Get-Date).AddSeconds(150)
while (-not (Test-Up)) {
    if ($server.HasExited -and -not $retried) {
        # A stuck background compiler can break the build; restarting it usually fixes that.
        Write-Host "  Hit a snag. Resetting the build tools and trying once more..." -ForegroundColor Yellow
        dotnet build-server shutdown | Out-Null
        $server = Start-Server
        $retried = $true
        continue
    }
    if ($server.HasExited) {
        Write-Host "  The app stopped while starting. Scroll up for the error." -ForegroundColor Red
        Read-Host "  Press Enter to close"
        return
    }
    if ((Get-Date) -gt $deadline) {
        Write-Host "  It's taking too long to start. Try again in a minute." -ForegroundColor Red
        Read-Host "  Press Enter to close"
        return
    }
    Start-Sleep -Milliseconds 700
}

Write-Host ""
Write-Host "  Roster Riot is running at $url" -ForegroundColor Green
Write-Host "  Keep this window open while you use it. Close it to stop the app."
if (-not $NoBrowser) { Start-Process $url }
Wait-Process -Id $server.Id
