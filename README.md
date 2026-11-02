# Reacher

**A way for your tools to reach you at your desk.**

Reacher is a tiny Windows tray app. When a script, a CI job, or an AI agent needs you, a bubble pops up at your mouse cursor, follows it for a few seconds so you actually notice, then docks in the bottom-right corner until you click it. It can carry buttons, so the thing reaching out can ask a question and get your answer back.

It was built so Claude Code can say *"I'm done"* or *"I'm blocked, which option?"* without you babysitting the terminal, but anything that can make an HTTP request can use it.

![Windows](https://img.shields.io/badge/platform-Windows-blue) ![.NET 10](https://img.shields.io/badge/.NET-10-purple) ![MIT](https://img.shields.io/badge/license-MIT-green)

## What it does

- **Never steals focus.** Keep typing; the bubble lets clicks pass through while it chases the cursor.
- **Waits for you.** Idle for 60s+ or screen locked? Reaches are held and pop up the moment you're back. Fullscreen apps and presentations hold everything except `urgent`.
- **Asks questions.** Add up to four buttons, wait, and get back which one was clicked.
- **Three priorities.** `info` is a quiet corner bubble. `attention` (default) chases the cursor for 3s. `urgent` chases longer, pulses, never auto-hides, and breaks through do-not-disturb.
- **Tray menu.** Test bubble, recent reaches, dismiss all, do-not-disturb, start with Windows, open the log.
- **Local only.** The API listens on `127.0.0.1:47322` and refuses browser requests, so web pages can't trigger bubbles.

## Install

Requirements: Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download), PowerShell 7+.

```powershell
git clone https://github.com/pixelgrave/reacher.git
cd reacher
.\install.ps1
```

The installer builds both executables into `%LOCALAPPDATA%\Programs\Reacher`, adds that folder to your PATH, starts Reacher with Windows, and registers the MCP server with Claude Code if the `claude` CLI is found. Use `-NoPath`, `-NoStartup` or `-NoMcp` to skip any of those. Re-run it to update.

Two executables:

| | |
|---|---|
| `Reacher.exe` | Tray daemon. Draws the bubbles and serves the local HTTP API. |
| `reach.exe` | CLI for scripts. `reach mcp` is the MCP server. Starts the daemon if it isn't running. |

## From Claude Code

Once installed, every new Claude Code session has two tools: `reach` sends a bubble (optionally with buttons, optionally waiting for your click), and `reach_presence` reports whether you're at the machine. The server's instructions tell Claude when to use them, so just say it in your prompt:

> fix the flaky login test, **reach me when you're done**

> run the migration and **reach out if you get blocked**

For scheduled or unattended tasks, put "use reacher to tell me the outcome" in the task prompt.

If the installer couldn't find the `claude` CLI, register it yourself:

```powershell
claude mcp add --scope user reacher -- "$env:LOCALAPPDATA\Programs\Reacher\reach.exe" mcp
```

Any other MCP client works too: run `reach.exe mcp` over stdio.

## From scripts

```powershell
reach "Nightly backup finished"
reach "Tests failed on main" -t "CI" -p urgent -o https://example.com/run/42
$answer = reach "Deploy to staging?" -a "Deploy,Not now" --wait 300   # prints the clicked label
```

| Option | |
|---|---|
| `-t, --title` | short headline |
| `-p, --priority` | `info`, `attention` (default), `urgent` |
| `-s, --source` | who's reaching out (default: current folder name) |
| `-a, --actions` | comma-separated button labels, max 4 |
| `-o, --open` | URL or path opened when the bubble is clicked |
| `-w, --wait [s]` | block until you respond (default 600s) |
| `--timeout <s>` | auto-hide after this long once docked, 0 = until clicked |
| `--sound` | play a system sound |
| `--json` | print the full JSON result |

Exit codes with `--wait`: 0 clicked, 2 dismissed, 3 expired, 4 no answer yet, 5 cancelled, 1 error. `reach help` lists everything, including `status`, `cancel`, `history` and `health`.

## HTTP API

Any language can use it. Bodies are JSON.

```
GET    /health                  presence (active|idle|locked|fullscreen), pending count, DND
POST   /reach                   {message, title?, priority?, source?, actions?[], open?, timeout?, chase?, sound?, wait?}
GET    /reach/{id}?wait=60      status, long-polling until it resolves
DELETE /reach/{id}              withdraw it
GET    /history?limit=20
```

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:47322/reach -ContentType application/json `
  -Body (@{ message = "Export done"; source = "Power BI job" } | ConvertTo-Json)
```

```bash
curl -s http://127.0.0.1:47322/reach -H 'Content-Type: application/json' \
  -d '{"message":"Ready for review?","actions":["Yes","Later"],"wait":300}'
```

## Config

Environment variables, read by the daemon at startup:

- `REACHER_PORT`: default 47322.
- `REACHER_IDLE_SECONDS`: seconds without input before you count as away. Default 60.
- `REACHER_SOURCE`: overrides the source label on the CLI/MCP side.

The log is at `%LOCALAPPDATA%\Reacher\reacher.log`.

## License

MIT. Do whatever you want with it.
