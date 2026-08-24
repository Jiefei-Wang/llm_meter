using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;

namespace LLMMeter.Core;

/// <summary>Thin HTTP wrapper used by adapters/fingerprinting; keeps timeouts uniform.</summary>
public interface IHttp : IDisposable
{
    Uri BaseUrl { get; }
    TimeSpan Timeout { get; }

    Task<(int Status, string Body)> GetStringAsync(string path, CancellationToken ct);

    async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            var (_, body) = await GetStringAsync(path, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64,
            });
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed class HttpService : IHttp, IDisposable
{
    private readonly HttpClient _client;

    public static HttpService Create(Uri baseUrl, TimeSpan timeout)
        => CreateOwning(baseUrl, timeout);

    public static HttpService CreateOwning(Uri baseUrl, TimeSpan timeout)
    {
        var client = SharedClientFactory.Create();
        client.Timeout = timeout;
        return new HttpService(baseUrl, client);
    }

    private HttpService(Uri baseUrl, HttpClient client)
    {
        BaseUrl = NormalizeBase(baseUrl);
        _client = client;
    }

    public Uri BaseUrl { get; }
    public TimeSpan Timeout => _client.Timeout;

    public async Task<(int Status, string Body)> GetStringAsync(string path, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(Timeout);
        try
        {
            using var resp = await _client.GetAsync(new Uri(BaseUrl, path), HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return ((int)resp.StatusCode, string.Empty);
            var body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            return ((int)resp.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (-1, string.Empty); // our timeout, not app shutdown
        }
        catch (HttpRequestException)
        {
            return (-1, string.Empty);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public static Uri NormalizeBase(Uri u)
    {
        var s = u.ToString();
        if (!s.EndsWith('/')) s += "/";
        return new Uri(s);
    }
}

/// <summary>
/// One shared handler pool so discovery bursts don't exhaust sockets.
/// </summary>
public static class SharedClientFactory
{
    private static readonly Lazy<HttpClientHandler> Handler = new(() =>
        new HttpClientHandler
        {
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        });

    public static HttpClient Create() => new(Handler.Value, disposeHandler: false)
    {
        DefaultRequestHeaders = { { "User-Agent", "LLMMeter/1.0" } },
    };
}

/// <summary>Monotonic clock helper.</summary>
public static class MonoClock
{
    public static long NowTicks => Stopwatch.GetTimestamp();
    public static double SecondsBetween(long startTicks, long endTicks) =>
        (double)(endTicks - startTicks) / Stopwatch.Frequency;
}
