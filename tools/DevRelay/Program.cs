using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

// Local stand-in for the Cloudflare Worker relay (relay/worker.js), same HTTP API:
//   PUT  /v1/blob/{id}     store ciphertext (10-minute TTL)
//   GET  /v1/blob/{id}     fetch ciphertext
//   POST /v1/push          {to, payload} → delivers the payload as a push.
//   POST /v1/register      accepted and ignored (real APNs tokens don't exist here)
//
// Instead of APNs, pushes are injected into a booted iOS Simulator with
// `xcrun simctl push`, which exercises the app's Notification Service Extension
// exactly like a production push would.
//
//   dotnet run -- --sim <udid|booted> [--bundle com.peterherrera.quacksnap] [--port 8787]

string sim = ArgValue("--sim") ?? "booted";
string bundle = ArgValue("--bundle") ?? "com.peterherrera.quacksnap";
int port = int.Parse(ArgValue("--port") ?? "8787");

var blobs = new ConcurrentDictionary<string, (byte[] Bytes, DateTime At)>();
var ttl = TimeSpan.FromMinutes(10);

var listener = new HttpListener();
listener.Prefixes.Add($"http://+:{port}/");
try
{
    listener.Start();
}
catch (HttpListenerException)
{
    // Non-admin fallback (Windows reserves wildcard prefixes).
    listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
}
Console.WriteLine($"DevRelay listening on port {port}; pushing to simulator '{sim}' bundle {bundle}");

while (true)
{
    var context = await listener.GetContextAsync();
    _ = Task.Run(() => HandleAsync(context));
}

async Task HandleAsync(HttpListenerContext context)
{
    var request = context.Request;
    var response = context.Response;
    try
    {
        foreach (var stale in blobs.Where(kv => DateTime.UtcNow - kv.Value.At > ttl).Select(kv => kv.Key).ToList())
            blobs.TryRemove(stale, out _);

        string path = request.Url!.AbsolutePath;

        if (request.HttpMethod == "PUT" && path.StartsWith("/v1/blob/"))
        {
            string id = path["/v1/blob/".Length..];
            using var ms = new MemoryStream();
            await request.InputStream.CopyToAsync(ms);
            blobs[id] = (ms.ToArray(), DateTime.UtcNow);
            Console.WriteLine($"  blob {id} stored ({ms.Length / 1024} KB)");
            await WriteJsonAsync(response, 200, new { ok = true, id });
        }
        else if (request.HttpMethod == "GET" && path.StartsWith("/v1/blob/"))
        {
            string id = path["/v1/blob/".Length..];
            if (blobs.TryGetValue(id, out var blob))
            {
                response.StatusCode = 200;
                response.ContentType = "application/octet-stream";
                await response.OutputStream.WriteAsync(blob.Bytes);
                Console.WriteLine($"  blob {id} fetched");
            }
            else
            {
                response.StatusCode = 404;
            }
        }
        else if (request.HttpMethod == "POST" && path == "/v1/push")
        {
            using var reader = new StreamReader(request.InputStream);
            var body = JsonDocument.Parse(await reader.ReadToEndAsync());
            var payload = body.RootElement.GetProperty("payload");
            string to = body.RootElement.GetProperty("to").GetString() ?? "?";

            string payloadPath = Path.Combine(Path.GetTempPath(), $"quacksnap-push-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(payloadPath, payload.GetRawText());
            var psi = new ProcessStartInfo("xcrun", $"simctl push {sim} {bundle} \"{payloadPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi)!;
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            File.Delete(payloadPath);

            Console.WriteLine(process.ExitCode == 0
                ? $"  push delivered to {to} via simctl"
                : $"  simctl push failed: {stderr.Trim()}");
            await WriteJsonAsync(response, process.ExitCode == 0 ? 200 : 502, new { ok = process.ExitCode == 0 });
        }
        else if (request.HttpMethod == "POST" && path == "/v1/register")
        {
            await WriteJsonAsync(response, 200, new { ok = true });
        }
        else
        {
            response.StatusCode = 404;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  error: {ex.Message}");
        try { response.StatusCode = 500; } catch { }
    }
    finally
    {
        try { response.Close(); } catch { }
    }
}

static async Task WriteJsonAsync(HttpListenerResponse response, int status, object body)
{
    response.StatusCode = status;
    response.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(response.OutputStream, body);
}

string? ArgValue(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
