using System.Net.Http.Headers;
using System.Text;

namespace CopilotUsageFilter;

/// <summary>
/// Forwards raw OTLP/HTTP request bodies to a remote collector (fire-and-forget).
/// A single <see cref="HttpClient"/> is reused for the process lifetime.
/// </summary>
internal static class OtlpForwarder
{
    private static readonly HttpClient s_http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// Fire-and-forgets a POST to <c>{forwardTo}{path}</c>.
    /// Errors are written to stderr; the caller is never blocked.
    /// </summary>
    public static void Forward(string forwardTo, string path, string body, string? contentType)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var url = forwardTo.TrimEnd('/') + path;
                var ct  = contentType ?? "application/json";
                // strip charset suffix for MediaTypeHeaderValue
                var media = ct.Contains(';') ? ct[..ct.IndexOf(';')].Trim() : ct.Trim();

                using var content = new StringContent(body, Encoding.UTF8, media);
                // Preserve original Content-Type exactly (incl. charset)
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(ct);

                using var resp = await s_http.PostAsync(url, content);
                if (!resp.IsSuccessStatusCode)
                    Console.Error.WriteLine($"[Forwarder] {(int)resp.StatusCode} from {url}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Forwarder] {ex.Message}");
            }
        });
    }
}
