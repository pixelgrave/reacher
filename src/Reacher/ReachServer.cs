using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Reacher;

/// <summary>
/// Minimal HTTP/1.1 server on the loopback interface. Hand-rolled on TcpListener so it needs no
/// URL ACL and accepts any loopback Host (localhost, 127.0.0.1, [::1]).
/// Browser requests are refused (any Origin header) so web pages can't trigger bubbles.
/// </summary>
internal sealed class ReachServer(Coordinator coordinator, int port)
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private const double MaxWaitSeconds = 3600;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, string Body);

    public void Start()
    {
        // IPv4 must work; IPv6 loopback is a bonus.
        Listen(IPAddress.Loopback, required: true);
        Listen(IPAddress.IPv6Loopback, required: false);
    }

    private void Listen(IPAddress address, bool required)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            _ = AcceptLoop(listener);
        }
        catch (SocketException ex) when (!required)
        {
            Log.Write($"listen on {address} skipped: {ex.Message}");
        }
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var request = await Read(stream, readTimeout.Token);
                if (request is null) return;
                var (status, body) = await Route(request);
                await Write(stream, status, body);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // client went away
            }
            catch (Exception ex)
            {
                Log.Write($"request failed: {ex}");
            }
        }
    }

    private static async Task<Request?> Read(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n == 0) return null;
            buffer.Write(chunk, 0, n);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
            if (headerEnd < 0 && buffer.Length > MaxHeaderBytes) return null;
        }

        var all = buffer.GetBuffer();
        var lines = Encoding.ASCII.GetString(all, 0, headerEnd).Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);

        var reader = new BodyReader(stream, all.AsSpan(headerEnd + 4, (int)buffer.Length - headerEnd - 4).ToArray());
        byte[]? body;
        if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await reader.ReadChunked(ct);
        }
        else
        {
            var length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) ? len : 0;
            body = length is >= 0 and <= MaxBodyBytes ? await reader.ReadExactly(length, ct) : null;
        }
        if (body is null) return null;

        var target = start[1];
        var q = target.IndexOf('?');
        var path = Uri.UnescapeDataString(q < 0 ? target : target[..q]).TrimEnd('/');
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q >= 0)
        {
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) query[Uri.UnescapeDataString(pair)] = "";
                else query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }
        return new Request(start[0].ToUpperInvariant(), path, query, headers, Encoding.UTF8.GetString(body));
    }

    private static int IndexOf(byte[] haystack, int length, ReadOnlySpan<byte> needle) =>
        haystack.AsSpan(0, length).IndexOf(needle);

    /// <summary>Reads a request body that may be partly buffered already, sized or chunked.</summary>
    private sealed class BodyReader(NetworkStream stream, byte[] leftover)
    {
        private readonly MemoryStream _pending = new(leftover);

        private async Task<int> ReadByte(CancellationToken ct)
        {
            var one = new byte[1];
            if (_pending.Read(one, 0, 1) == 1) return one[0];
            return await stream.ReadAsync(one, ct) == 1 ? one[0] : -1;
        }

        public async Task<byte[]?> ReadExactly(int length, CancellationToken ct)
        {
            var body = new byte[length];
            var have = _pending.Read(body, 0, length);
            while (have < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(have, length - have), ct);
                if (n == 0) return null;
                have += n;
            }
            return body;
        }

        private async Task<string?> ReadLine(CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (true)
            {
                var b = await ReadByte(ct);
                if (b < 0 || sb.Length > 1024) return null;
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
            }
        }

        public async Task<byte[]?> ReadChunked(CancellationToken ct)
        {
            var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLine(ct);
                if (sizeLine is null) return null;
                var hex = sizeLine.Split(';')[0].Trim();
                if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0) return null;
                if (size == 0)
                {
                    // trailers end with an empty line
                    while (await ReadLine(ct) is { Length: > 0 }) { }
                    return body.ToArray();
                }
                if (body.Length + size > MaxBodyBytes) return null;
                var chunk = await ReadExactly(size, ct);
                if (chunk is null) return null;
                body.Write(chunk);
                await ReadLine(ct); // CRLF after each chunk
            }
        }
    }

    private async Task<(int, object)> Route(Request r)
    {
        if (r.Headers.ContainsKey("Origin"))
            return (403, new { error = "browser requests are not accepted" });
        if (r.Headers.TryGetValue("Host", out var host) && !IsLoopbackHost(host))
            return (403, new { error = "host not allowed" });

        var segments = r.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (r.Method, segments)
        {
            case ("GET", []):
            case ("GET", ["health"]):
                return (200, new
                {
                    ok = true,
                    name = "reacher",
                    version = typeof(ReachServer).Assembly.GetName().Version?.ToString(3),
                    presence = coordinator.Presence.ToDto(),
                    pending = coordinator.PendingCount,
                    doNotDisturb = coordinator.DoNotDisturb,
                });

            case ("POST", ["reach"]):
                return await PostReach(r);

            case ("GET", ["reach", var id]):
            {
                var item = coordinator.Find(id);
                if (item is null) return (404, new { error = $"no reach with id '{id}'" });
                await WaitFor(item, r.Query.TryGetValue("wait", out var w) && double.TryParse(w, out var s) ? s : 0);
                return (200, item.ToDto(coordinator.Presence));
            }

            case ("DELETE", ["reach", var id]):
            {
                var cancelled = coordinator.Cancel(id);
                var item = coordinator.Find(id);
                if (item is null) return (404, new { error = $"no reach with id '{id}'" });
                return (200, new { cancelled, reach = item.ToDto(coordinator.Presence) });
            }

            case ("GET", ["history"]):
            {
                var limit = r.Query.TryGetValue("limit", out var l) && int.TryParse(l, out var n) ? Math.Clamp(n, 1, 200) : 20;
                return (200, new { items = coordinator.Recent(limit).Select(i => i.ToDto(coordinator.Presence)) });
            }

            default:
                return (404, new { error = $"no route for {r.Method} {r.Path}" });
        }
    }

    private async Task<(int, object)> PostReach(Request r)
    {
        if (!r.Headers.TryGetValue("Content-Type", out var type) || !type.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return (415, new { error = "Content-Type must be application/json" });

        ReachRequest? body;
        try { body = JsonSerializer.Deserialize<ReachRequest>(r.Body, Json); }
        catch (JsonException ex) { return (400, new { error = $"invalid JSON: {ex.Message}" }); }

        if (body is null || string.IsNullOrWhiteSpace(body.Message))
            return (400, new { error = "'message' is required" });
        if (body.Message.Length > 4000) body.Message = body.Message[..4000];

        var item = coordinator.Submit(body);
        Log.Write($"{item.Id} from '{item.Source}' [{item.Priority}] {item.Message.ReplaceLineEndings(" ")}");
        await WaitFor(item, body.Wait ?? 0);
        return (200, item.ToDto(coordinator.Presence));
    }

    private static async Task WaitFor(ReachItem item, double seconds)
    {
        if (seconds <= 0 || item.IsFinal) return;
        await Task.WhenAny(item.Done.Task, Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, MaxWaitSeconds))));
    }

    private static bool IsLoopbackHost(string host)
    {
        var name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name is "127.0.0.1" or "[::1]";
    }

    private static async Task Write(NetworkStream stream, int status, object body)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 415 => "Unsupported Media Type", _ => "Error" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(json);
        await stream.FlushAsync();
    }
}
