using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Reach;

/// <summary>Talks to the Reacher daemon, starting it if it isn't running.</summary>
internal sealed class ReacherClient
{
    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("REACHER_PORT"), out var p) ? p : 47322;

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{Port}/"),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<JsonNode> Health(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await Send(new HttpRequestMessage(HttpMethod.Get, "health"), cts.Token);
    }

    public async Task EnsureRunning()
    {
        try
        {
            await Health(TimeSpan.FromSeconds(2));
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }

        var exe = Path.Combine(AppContext.BaseDirectory, "Reacher.exe");
        if (!File.Exists(exe))
            throw new ReacherException($"Reacher isn't running and {exe} wasn't found to start it.");

        // Launch through the shell so the daemon isn't tied to our process tree (or a caller's job object).
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false }); }
        catch { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            try
            {
                await Health(TimeSpan.FromSeconds(1));
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
        }
        throw new ReacherException("Started Reacher but it didn't come up within 15 seconds.");
    }

    public Task<JsonNode> Reach(JsonObject body, double waitSeconds)
    {
        if (waitSeconds > 0) body["wait"] = waitSeconds;
        var request = new HttpRequestMessage(HttpMethod.Post, "reach")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        return SendWithin(request, waitSeconds);
    }

    public Task<JsonNode> Status(string id, double waitSeconds) =>
        SendWithin(new HttpRequestMessage(HttpMethod.Get, $"reach/{Uri.EscapeDataString(id)}?wait={waitSeconds}"), waitSeconds);

    public Task<JsonNode> Cancel(string id) =>
        SendWithin(new HttpRequestMessage(HttpMethod.Delete, $"reach/{Uri.EscapeDataString(id)}"), 0);

    public Task<JsonNode> History(int limit) =>
        SendWithin(new HttpRequestMessage(HttpMethod.Get, $"history?limit={limit}"), 0);

    private async Task<JsonNode> SendWithin(HttpRequestMessage request, double waitSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(waitSeconds + 30));
        return await Send(request, cts.Token);
    }

    private async Task<JsonNode> Send(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = JsonNode.Parse(text) ?? new JsonObject();
        if (!response.IsSuccessStatusCode)
            throw new ReacherException(json["error"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}");
        return json;
    }
}

internal sealed class ReacherException(string message) : Exception(message);
