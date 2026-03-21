#nullable enable
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Common;

/// <summary>
/// Bazna klasa za REST klijente ka kripto berzama.
/// Brine o HttpClient-u, logovanju, osnovnom retry-u i JSON serijalizaciji.
/// Konkretne berze nasleđuju ovu klasu i dodaju signiranje/autentikaciju.
/// </summary>
public abstract class RestClientBase : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _log;

    protected JsonSerializerOptions JsonOptions { get; }

    protected RestClientBase(string baseUrl, ILogger log, HttpMessageHandler? handler = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _httpClient = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);

        _httpClient.BaseAddress = new Uri(baseUrl ?? throw new ArgumentNullException(nameof(baseUrl)));

        JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    protected async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        await PrepareRequestAsync(request, ct).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.Warning("[REST] GET {Path} failed: {Status} {Body}", path, response.StatusCode, content);
            throw new HttpRequestException($"GET {path} failed with status {response.StatusCode}");
        }

        _log.Debug("[REST] GET {Path} -> {Body}", path, content);

        var result = JsonSerializer.Deserialize<T>(content, JsonOptions);
        if (result == null)
        {
            throw new InvalidOperationException($"Cannot deserialize response for GET {path} into {typeof(T).Name}");
        }

        return result;
    }

    protected async Task<T> PostAsync<T>(string path, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content
        };

        await PrepareRequestAsync(request, ct).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.Warning("[REST] POST {Path} failed: {Status} {Body}", path, response.StatusCode, responseBody);
            throw new HttpRequestException($"POST {path} failed with status {response.StatusCode}");
        }

        _log.Debug("[REST] POST {Path} -> {Body}", path, responseBody);

        var result = JsonSerializer.Deserialize<T>(responseBody, JsonOptions);
        if (result == null)
        {
            throw new InvalidOperationException($"Cannot deserialize response for POST {path} into {typeof(T).Name}");
        }

        return result;
    }

    /// <summary>
    /// Hook za potpisivanje/autentikaciju – konkretan klijent može da doda header-e ovde.
    /// </summary>
    protected virtual Task PrepareRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // npr. ovde kasnije dodaješ API key, timestamp, signature...
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}