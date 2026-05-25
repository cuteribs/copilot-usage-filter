using System.Net;
using System.Text;

namespace CopilotUsageFilter;

/// <summary>
/// Minimal HTTP listener on http://localhost:4318/
/// Handles all standard OTLP/HTTP paths: /v1/traces, /v1/metrics, /v1/logs
/// </summary>
public sealed class OtlpHttpReceiver : IDisposable
{
    private static readonly byte[] s_okBytes = "{}"u8.ToArray();

    private readonly HttpListener _listener;
    private readonly Func<string, string, Task> _onRequest; // (path, body)
    private readonly AppOptions _opts;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public OtlpHttpReceiver(AppOptions opts, Func<string, string, Task> onRequest)
    {
        _opts = opts;
        _onRequest = onRequest;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{opts.Port}/");
    }

    /// <summary>Backwards-compat overload (no forwarding).</summary>
    public OtlpHttpReceiver(int port, Func<string, string, Task> onRequest)
        : this(new AppOptions { Port = port }, onRequest) { }

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
        // Wait briefly for the listen loop to drain before caller releases resources
        try { _listenTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
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
            var path        = ctx.Request.Url?.AbsolutePath ?? "/";
            var contentType = ctx.Request.ContentType;
            var isTrace     = path.StartsWith("/v1/traces", StringComparison.OrdinalIgnoreCase);
            var forwardTo   = _opts.ForwardTo;
            var needBody    = (isTrace || forwardTo != null)
                              && ctx.Request.HttpMethod is "POST" or "PUT";

            string? body = null;
            if (needBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream,
                    ctx.Request.ContentEncoding ?? Encoding.UTF8);
                body = await reader.ReadToEndAsync();
            }

            if (isTrace && !string.IsNullOrWhiteSpace(body))
                await _onRequest(path, body);

            if (forwardTo != null && !string.IsNullOrWhiteSpace(body))
                OtlpForwarder.Forward(forwardTo, path, body, contentType);

            // Always return standard OTLP success response — metrics/logs exporters
            // expect 200 just like trace exporters do.
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = s_okBytes.Length;
            await ctx.Response.OutputStream.WriteAsync(s_okBytes);
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
        _cts?.Dispose();
        _listener.Close();
    }
}
