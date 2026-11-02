using System.Text;
using System.Text.Json.Nodes;

namespace Reach;

/// <summary>
/// `reach mcp`: a stdio MCP server (newline-delimited JSON-RPC) exposing Reacher to Claude.
/// Hand-rolled because it only needs initialize, ping, tools/list and tools/call.
/// </summary>
internal static class McpServer
{
    private const string Instructions =
        "Reacher pops an attention bubble at the user's mouse cursor on their Windows PC, follows the cursor for a few " +
        "seconds, then docks in the corner until clicked. If they're away (idle/locked) it waits and pops up when they return. " +
        "Use the `reach` tool when the user asked you to reach out / ping them / let them know when you're done (do it as the " +
        "very last step, after the work is verified), when an unattended or scheduled run finishes or fails, or when you're " +
        "blocked and need them. Lead with the outcome in one or two short sentences. Don't reach out during normal back-and-forth " +
        "when they're clearly watching, and don't send more than one reach for the same event.";

    private static readonly JsonArray Tools =
    [
        new JsonObject
        {
            ["name"] = "reach",
            ["description"] =
                "Reach out to the user at their desk: a bubble pops up at their mouse cursor and follows it for a few seconds " +
                "so they notice, then docks in the screen corner until they click it. If they're away from the machine it is " +
                "held and pops up the moment they return. The current project name is shown as the source automatically. " +
                "Use when the user asked to be reached/pinged/told when you're done, when an unattended task finishes or " +
                "fails, or when you're blocked on them. Add `actions` + `wait_for_response` to ask a quick question and get " +
                "the clicked answer back.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["message"] = Prop("string", "What you need to tell them. One or two short sentences, outcome first, then what (if anything) they need to do."),
                    ["title"] = Prop("string", "Optional short headline, e.g. 'Build finished' or 'Need your OK'."),
                    ["priority"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("info", "attention", "urgent"),
                        ["description"] = "info: quiet corner bubble, no chasing, hides after 20s. attention (default): pops at the cursor, " +
                                          "follows it ~3s, hides after 2 min. urgent: chases longer, pulses, never auto-hides, breaks through " +
                                          "do-not-disturb - only when you are blocked on them.",
                    },
                    ["actions"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["maxItems"] = 4,
                        ["description"] = "Optional button labels (max 4, keep them 1-3 words), e.g. [\"Approve\", \"Skip\"]. Use with wait_for_response.",
                    },
                    ["wait_for_response"] = Prop("boolean", "Block until they click or dismiss the bubble (or wait_seconds passes) and return what they did."),
                    ["wait_seconds"] = Prop("number", "Max seconds to wait when wait_for_response is true. Default 300, max 1800."),
                    ["open"] = Prop("string", "Optional URL or file/folder path to open when they click the bubble, e.g. a PR link or a report."),
                    ["timeout_seconds"] = Prop("number", "Seconds before it auto-hides once docked (paused while they're away or hovering). 0 = stay until clicked."),
                    ["sound"] = Prop("boolean", "Also play a short system sound."),
                },
                ["required"] = new JsonArray("message"),
            },
        },
        new JsonObject
        {
            ["name"] = "reach_presence",
            ["description"] = "Check whether the user is at their machine right now (active, idle for 60s+, screen locked, or in a fullscreen app) and how long since their last input.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        },
    ];

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    public static async Task<int> Run()
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var writeLock = new SemaphoreSlim(1, 1);
        var inFlight = new List<Task>();

        async Task Send(JsonObject message)
        {
            await writeLock.WaitAsync();
            try { await output.WriteLineAsync(message.ToJsonString()); }
            finally { writeLock.Release(); }
        }

        while (await input.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? request;
            try { request = JsonNode.Parse(line) as JsonObject; }
            catch { await Send(Error(null, -32700, "Parse error")); continue; }
            if (request is null) continue;

            // Requests run concurrently: a reach that waits for the user mustn't block pings.
            inFlight.Add(Task.Run(async () =>
            {
                var reply = await Handle(request);
                if (reply is not null) await Send(reply);
            }));
            inFlight.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(inFlight);
        return 0;
    }

    private static async Task<JsonObject?> Handle(JsonObject request)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        if (id is null) return null; // notification

        try
        {
            return method switch
            {
                "initialize" => Result(id, new JsonObject
                {
                    ["protocolVersion"] = request["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "reacher", ["version"] = "0.1.0" },
                    ["instructions"] = Instructions,
                }),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = Tools.DeepClone() }),
                "tools/call" => Result(id, await CallTool(request["params"] as JsonObject)),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    private static async Task<JsonObject> CallTool(JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>();
        var args = p?["arguments"] as JsonObject ?? new JsonObject();
        var client = new ReacherClient();
        try
        {
            await client.EnsureRunning();
            switch (name)
            {
                case "reach":
                {
                    var message = args["message"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(message)) return ToolText("`message` is required.", isError: true);

                    var body = new JsonObject
                    {
                        ["message"] = message,
                        ["source"] = Environment.GetEnvironmentVariable("REACHER_SOURCE") ?? $"Claude · {ProjectName()}",
                    };
                    foreach (var (from, to) in new[] { ("title", "title"), ("priority", "priority"), ("open", "open"), ("timeout_seconds", "timeout"), ("sound", "sound") })
                        if (args[from] is { } value) body[to] = value.DeepClone();
                    if (args["actions"] is JsonArray actions) body["actions"] = actions.DeepClone();

                    var wait = args["wait_for_response"]?.GetValue<bool>() == true
                        ? Math.Clamp(args["wait_seconds"]?.GetValue<double>() ?? 300, 1, 1800)
                        : 0;
                    var result = await client.Reach(body, wait);
                    return ToolText(Describe.Result(result, waited: wait > 0, wait));
                }
                case "reach_presence":
                {
                    var health = await client.Health(TimeSpan.FromSeconds(3));
                    var text = Describe.Presence(health["presence"]);
                    if (health["doNotDisturb"]?.GetValue<bool>() == true) text += " Do-not-disturb is on (only urgent reaches show).";
                    return ToolText(text);
                }
                default:
                    return ToolText($"Unknown tool: {name}", isError: true);
            }
        }
        catch (Exception ex) when (ex is ReacherException or HttpRequestException or OperationCanceledException)
        {
            return ToolText($"Couldn't reach Reacher: {ex.Message}", isError: true);
        }
    }

    private static string ProjectName()
    {
        var name = Path.GetFileName(Environment.CurrentDirectory.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? "session" : name;
    }

    private static JsonObject ToolText(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
