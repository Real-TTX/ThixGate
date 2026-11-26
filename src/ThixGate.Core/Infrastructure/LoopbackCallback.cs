using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ThixGate.Core.Infrastructure;

/// <summary>
/// The address on this computer a browser sign-in comes back to (RFC 8252: http://127.0.0.1:&lt;port&gt;/callback). It listens only on the
/// loopback address, answers the one request with a short page ("you can close this window") and hands its query on.
/// </summary>
internal sealed class LoopbackCallback : IDisposable
{
    private const string Path = "/callback";
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackCallback()
    {
        _listener.Start();
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{Path}";
    }

    public string RedirectUri { get; }

    /// <summary>Waits for the browser; returns the parameters of the callback.</summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(string pageTitle, string pageText, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            var stream = client.GetStream();
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            reading.CancelAfter(TimeSpan.FromSeconds(10));
            string requestLine;
            try
            {
                requestLine = await ReadHeadAsync(stream, reading.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            // "GET /callback?code=...&state=... HTTP/1.1"
            var parts = requestLine.Split(' ');
            var target = parts.Length >= 2 && parts[0] == "GET" ? parts[1] : "";
            if (!target.StartsWith(Path, StringComparison.Ordinal) || (target.Length > Path.Length && target[Path.Length] != '?'))
            {
                await RespondAsync(stream, "404 Not Found", "Nicht gefunden", "", cancellationToken);
                continue;
            }

            await RespondAsync(stream, "200 OK", pageTitle, pageText, cancellationToken);
            return ParseQuery(target.Length > Path.Length ? target[(Path.Length + 1)..] : "");
        }
    }

    public void Dispose() => _listener.Stop();

    internal static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((cut < 0 ? pair : pair[..cut]).Replace('+', ' '));
            var value = cut < 0 ? "" : Uri.UnescapeDataString(pair[(cut + 1)..].Replace('+', ' '));
            values.TryAdd(key, value);
        }

        return values;
    }

    // Reads up to the empty line after the headers; returns the request line.
    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        var buffer = new byte[2048];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0 || head.Length > 16 * 1024)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        var text = head.ToString();
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string title, string text, CancellationToken cancellationToken)
    {
        var body = $$"""
            <!doctype html><html lang="de"><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>body{font-family:system-ui,-apple-system,"Segoe UI",sans-serif;display:grid;place-items:center;height:100vh;margin:0;background:#f6f6fb;color:#1c1b29}
            main{max-width:28rem;text-align:center}h1{color:#4b3fcf;font-size:1.4rem}@media(prefers-color-scheme:dark){body{background:#16151f;color:#ecebf5} }</style>
            </head><body><main><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(text)}}</p></main></body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
