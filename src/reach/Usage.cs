namespace Reach;

internal static class Usage
{
    public const string Text = """
        reach - pop an attention bubble at your mouse cursor (via the Reacher daemon)

        Usage:
          reach <message> [options]        send a reach
          reach - [options]                read the message from stdin
          reach status <id> [--wait [s]]   show (or wait for) a reach's outcome, as JSON
          reach cancel <id>                withdraw a reach that's queued or showing
          reach history [n]                recent reaches
          reach health                     daemon status and whether you're at the machine
          reach start                      start the daemon if it isn't running
          reach mcp                        run as an MCP server over stdio (for Claude)

        Options:
          -t, --title <text>        short headline
          -p, --priority <level>    info | attention (default) | urgent
          -s, --source <name>       who's reaching out (default: current folder name)
          -a, --actions <a,b,...>   buttons (max 4); the clicked label is printed with --wait
          -o, --open <url|path>     opened when the bubble is clicked
          -w, --wait [seconds]      block until you respond (default 600s)
              --timeout <seconds>   auto-hide after this long once docked (0 = until clicked)
              --chase <seconds>     how long the bubble follows the cursor
              --sound               play a system sound
              --json                print the full JSON result

        Exit codes with --wait: 0 clicked, 2 dismissed, 3 expired, 4 no answer yet, 5 cancelled, 1 error.

        Examples:
          reach "Nightly backup finished"
          reach "Tests failed on main" -t "CI" -p urgent -o https://example.com/run/42
          reach "Deploy to staging?" -a "Deploy,Not now" --wait 300
        """;
}
