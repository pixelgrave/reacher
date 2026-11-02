using System.Text.Json.Nodes;

namespace Reach;

/// <summary>Plain-language summaries of a reach result, shared by the CLI and the MCP tool.</summary>
internal static class Describe
{
    public static string Result(JsonNode r, bool waited, double waitSeconds = 0)
    {
        var id = r["id"]?.GetValue<string>();
        var status = r["status"]?.GetValue<string>();
        var response = r["response"]?.GetValue<string>();
        return status switch
        {
            "acknowledged" when response is not null => $"They clicked \"{response}\". (id {id})",
            "acknowledged" => $"They clicked the bubble - message seen. (id {id})",
            "dismissed" => $"They dismissed the bubble without choosing anything. (id {id})",
            "expired" => $"The bubble timed out without a click; they may not have seen it. (id {id})",
            "cancelled" => $"The reach was cancelled. (id {id})",
            "showing" when waited => $"No response within {waitSeconds:0}s; the bubble is still on their screen. (id {id})",
            "showing" => $"Delivered - the bubble is on their screen now and they are at the machine. (id {id})",
            "queued" => $"Queued: {QueueReason(r)} (id {id})",
            _ => $"Status: {status} (id {id})",
        };
    }

    public static string Presence(JsonNode? p)
    {
        var state = p?["state"]?.GetValue<string>();
        var idle = p?["idleSeconds"]?.GetValue<double>() ?? 0;
        return state switch
        {
            "active" => $"They are at the machine (last input {Ago(idle)}).",
            "idle" => $"They are away from the machine - no input for {Ago(idle, bare: true)}.",
            "locked" => "Their screen is locked; they are away.",
            "fullscreen" => "They are in a fullscreen app or presentation - only urgent reaches show right now.",
            _ => $"Presence: {state}",
        };
    }

    private static string QueueReason(JsonNode r)
    {
        var idle = r["presence"]?["idleSeconds"]?.GetValue<double>() ?? 0;
        return r["queuedReason"]?.GetValue<string>() switch
        {
            "away" => $"they're away from the machine (no input for {Ago(idle, bare: true)}). It will pop up the moment they're back.",
            "locked" => "their screen is locked. It will pop up as soon as they unlock.",
            "do-not-disturb" => "they have do-not-disturb on. It shows when they turn it off (only priority urgent breaks through).",
            "fullscreen" => "they're in a fullscreen app or presentation. It shows when they leave it (only priority urgent breaks through).",
            var other => $"{other}. It will show shortly.",
        };
    }

    private static string Ago(double seconds, bool bare = false)
    {
        var text = seconds < 60 ? $"{seconds:0}s"
            : seconds < 3600 ? $"{seconds / 60:0} min"
            : $"{seconds / 3600:0.#} h";
        return bare ? text : $"{text} ago";
    }
}
