<#
.SYNOPSIS
  Builds Reacher and installs it for the current user.
.DESCRIPTION
  - Publishes Reacher.exe (tray daemon) and reach.exe (CLI + MCP server) to %LOCALAPPDATA%\Programs\Reacher
  - Adds that folder to the user PATH so `reach` works in new terminals
  - Starts Reacher with Windows (toggle later from the tray menu)
  - Registers the `reacher` MCP server for Claude Code at user scope
  Safe to re-run: it stops the running daemon, updates the files and starts it again.
#>
param(
    [switch]$NoStartup,
    [switch]$NoMcp,
    [switch]$NoPath
)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Reacher'

Get-Process Reacher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

foreach ($project in 'src\Reacher\Reacher.csproj', 'src\reach\reach.csproj') {
    dotnet publish (Join-Path $PSScriptRoot $project) -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=None -o $dest --nologo -v quiet
    if ($LASTEXITCODE) { throw "publish failed: $project" }
}
Write-Host "Installed to $dest"

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User') ?? ''
if (-not $NoPath -and ($userPath -split ';') -notcontains $dest) {
    [Environment]::SetEnvironmentVariable('Path', (($userPath.TrimEnd(';'), $dest) -join ';').TrimStart(';'), 'User')
    Write-Host "Added $dest to your user PATH (new terminals will see 'reach')."
}

if (-not $NoStartup) {
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Reacher -Value "`"$dest\Reacher.exe`""
    Write-Host 'Reacher will start with Windows (untick "Start with Windows" in the tray menu to stop that).'
}

if (-not $NoMcp) {
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        claude mcp remove --scope user reacher *> $null
        claude mcp add --scope user reacher -- "$dest\reach.exe" mcp
        Write-Host 'Registered the reacher MCP server for Claude Code (new sessions get the `reach` tool).'
    } else {
        Write-Warning "claude CLI not found; register manually: claude mcp add --scope user reacher -- `"$dest\reach.exe`" mcp"
    }
}

& "$dest\reach.exe" start
