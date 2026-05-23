using System.Net;
using System.Text;

namespace CopilotUsageFilter;

/// <summary>
/// Minimal HTTP listener on http://localhost:4318/
/// Handles all standard OTLP/HTTP paths: /v1/traces, /v1/metrics, /v1/logs
/// </summary>
public sealed class OtlpHttpReceiver : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<string, string, Task> _onRequest; // (path, body)
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public OtlpHttpReceiver(int port, Func<string, string, Task> onRequest)
    {
        _onRequest = onRequest;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _listenTask = ListenAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch { continue; }

            _ = Task.Run(() => HandleRequestAsync(ctx), ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";

            // Only read + forward the body for trace requests.
            // Metrics, logs, and any future OTLP signal types are acknowledged
            // with 200 but their bodies are not read — no wasted allocation.
            var isTrace = path.StartsWith("/v1/traces", StringComparison.OrdinalIgnoreCase);

            if (isTrace && ctx.Request.HttpMethod is "POST" or "PUT")
            {
                using var reader = new StreamReader(ctx.Request.InputStream,
                    ctx.Request.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(body))
                    await _onRequest(path, body);
            }

            // Always return standard OTLP success response — metrics/logs exporters
            // expect 200 just like trace exporters do.
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            var okBytes = "{}"u8.ToArray();
            ctx.Response.ContentLength64 = okBytes.Length;
            await ctx.Response.OutputStream.WriteAsync(okBytes);
        }
        catch (Exception ex)
        {
            try { ctx.Response.StatusCode = 500; } catch { }
            Console.Error.WriteLine($"[OtlpReceiver] Error: {ex.Message}");
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
    }
}
