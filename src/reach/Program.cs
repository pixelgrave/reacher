using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reach;

Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
{
    Console.WriteLine(Usage.Text);
    return args.Length == 0 ? 1 : 0;
}

try
{
    return args[0] switch
    {
        "mcp" => await McpServer.Run(),
        "health" => await Health(),
        "start" => await Start(),
        "status" when args.Length >= 2 => await StatusCmd(args[1], OptionalWait(args.Skip(2).ToArray())),
        "cancel" when args.Length >= 2 => await CancelCmd(args[1]),
        "history" => await HistoryCmd(args.Length >= 2 && int.TryParse(args[1], out var n) ? n : 10),
        _ => await ReachCmd(args),
    };
}
catch (ReacherException ex)
{
    Console.Error.WriteLine($"reach: {ex.Message}");
    return 1;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"reach: {ex.Message}");
    Console.Error.WriteLine("Run 'reach help' for usage.");
    return 1;
}
catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
{
    Console.Error.WriteLine($"reach: Reacher isn't running on 127.0.0.1:{ReacherClient.Port} ({ex.Message}). Run 'reach start' to launch it.");
    return 1;
}

static async Task<int> ReachCmd(string[] args)
{
    var body = new JsonObject();
    var message = new List<string>();
    var actions = new List<string>();
    double wait = 0;
    var json = false;

    for (var i = 0; i < args.Length; i++)
    {
        var a = args[i];
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
        switch (a)
        {
            case "-t" or "--title": body["title"] = Next(); break;
            case "-p" or "--priority": body["priority"] = Next(); break;
            case "-s" or "--source": body["source"] = Next(); break;
            case "-o" or "--open": body["open"] = Next(); break;
            case "-a" or "--actions": actions.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
            case "--timeout": body["timeout"] = Number(a, Next()); break;
            case "--chase": body["chase"] = Number(a, Next()); break;
            case "--sound": body["sound"] = true; break;
            case "--json": json = true; break;
            case "-w" or "--wait":
                if (i + 1 < args.Length && double.TryParse(args[i + 1], out var w)) { wait = w; i++; }
                else wait = 600;
                break;
            case "-": message.Add(Console.In.ReadToEnd().Trim()); break;
            default:
                if (a.StartsWith('-') && a.Length > 1) throw new ArgumentException($"unknown option {a}");
                message.Add(a);
                break;
        }
    }

    var text = string.Join(' ', message).Trim();
    if (text.Length == 0) throw new ArgumentException("a message is required");
    body["message"] = text;
    if (actions.Count > 0) body["actions"] = new JsonArray(actions.Select(x => (JsonNode)x!).ToArray());
    if (!body.ContainsKey("source"))
        body["source"] = Environment.GetEnvironmentVariable("REACHER_SOURCE") ?? DefaultSource();

    var client = new ReacherClient();
    await client.EnsureRunning();
    var result = await client.Reach(body, wait);
    return Report(result, json, wait);
}

static async Task<int> StatusCmd(string id, double wait)
{
    var result = await new ReacherClient().Status(id, wait);
    return Report(result, json: true, wait);
}

static async Task<int> CancelCmd(string id)
{
    var result = await new ReacherClient().Cancel(id);
    Console.WriteLine(result.ToJsonString(Pretty));
    return 0;
}

static async Task<int> HistoryCmd(int limit)
{
    var result = await new ReacherClient().History(limit);
    foreach (var item in result["items"]!.AsArray())
    {
        var response = item!["response"]?.GetValue<string>() is { } r ? $" -> {r}" : "";
        Console.WriteLine($"{item["id"]}  {item["status"],-12} {item["source"]}: {item["message"]}{response}");
    }
    return 0;
}

static async Task<int> Health()
{
    var result = await new ReacherClient().Health(TimeSpan.FromSeconds(3));
    Console.WriteLine(result.ToJsonString(Pretty));
    return 0;
}

static async Task<int> Start()
{
    var client = new ReacherClient();
    await client.EnsureRunning();
    Console.WriteLine($"Reacher is running on 127.0.0.1:{ReacherClient.Port}");
    return 0;
}

static int Report(JsonNode result, bool json, double wait)
{
    var waited = wait > 0;
    var status = result["status"]?.GetValue<string>() ?? "unknown";
    var response = result["response"]?.GetValue<string>();
    if (json) Console.WriteLine(result.ToJsonString(Pretty));
    else if (waited && response is not null) Console.WriteLine(response);
    else Console.WriteLine(Describe.Result(result, waited, wait));

    if (!waited) return 0;
    return status switch
    {
        "acknowledged" => 0,
        "dismissed" => 2,
        "expired" => 3,
        "cancelled" => 5,
        _ => 4, // still queued/showing when the wait ran out
    };
}

static double OptionalWait(string[] rest)
{
    for (var i = 0; i < rest.Length; i++)
        if (rest[i] is "-w" or "--wait")
            return i + 1 < rest.Length && double.TryParse(rest[i + 1], out var w) ? w : 600;
    return 0;
}

static double Number(string option, string value) =>
    double.TryParse(value, out var d) ? d : throw new ArgumentException($"{option} expects a number of seconds");

static string DefaultSource()
{
    var cwd = Environment.CurrentDirectory.TrimEnd('\\', '/');
    var name = Path.GetFileName(cwd);
    var system = Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd('\\');
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
    return string.IsNullOrEmpty(name) || cwd.Equals(system, StringComparison.OrdinalIgnoreCase) || cwd.Equals(home, StringComparison.OrdinalIgnoreCase)
        ? "Script"
        : name;
}

partial class Program
{
    static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
