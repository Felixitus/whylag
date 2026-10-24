using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhyLag.AI;

public sealed record OllamaModel(string Name, long SizeBytes, string? ParameterSize, string? Family, string? Quantization);

public sealed record PullProgress(string Status, long? Total, long? Completed);

public sealed record ChatMessage(string Role, string Content);

public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Thin client for the parts of the Ollama REST API that WhyLag uses.
/// https://github.com/ollama/ollama/blob/main/docs/api.md
/// </summary>
public sealed class OllamaClient
{
    private readonly HttpClient _http;

    public OllamaClient(HttpClient http, Uri endpoint)
    {
        if (endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("Ollama endpoint must use http or https.", nameof(endpoint));
        _http = http;
        Endpoint = endpoint;
    }

    public static OllamaClient Create(string endpoint, TimeSpan timeout) =>
        new(new HttpClient { Timeout = timeout }, new Uri(endpoint.TrimEnd('/') + "/"));

    public Uri Endpoint { get; }

    /// <summary>True when the endpoint is this PC. Otherwise incident data would leave the machine.</summary>
    public bool IsLocal => Endpoint.IsLoopback;

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetFromJsonAsync<JsonObject>(new Uri(Endpoint, "api/version"), ct).ConfigureAwait(false);
            return json?["version"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<List<OllamaModel>> ListModelsAsync(CancellationToken ct = default)
    {
        var json = await Send(() => _http.GetFromJsonAsync<JsonObject>(new Uri(Endpoint, "api/tags"), ct)).ConfigureAwait(false);
        var result = new List<OllamaModel>();
        foreach (var m in json?["models"]?.AsArray() ?? [])
        {
            if (m?["name"]?.GetValue<string>() is not { } name) continue;
            var details = m["details"];
            result.Add(new OllamaModel(name,
                m["size"]?.GetValue<long>() ?? 0,
                details?["parameter_size"]?.GetValue<string>(),
                details?["family"]?.GetValue<string>(),
                details?["quantization_level"]?.GetValue<string>()));
        }
        return result;
    }

    /// <summary>
    /// Non-streaming chat with a JSON schema as output format. keep_alive=0 unloads the model right
    /// after the answer so it does not keep VRAM busy (important while gaming).
    /// </summary>
    public async Task<string> ChatAsync(string model, IEnumerable<ChatMessage> messages, JsonNode format, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["stream"] = false,
            ["format"] = format.DeepClone(),
            ["keep_alive"] = 0,
            ["think"] = false,
            ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_ctx"] = 8192 },
        };

        var response = await Send(() => _http.PostAsJsonAsync(new Uri(Endpoint, "api/chat"), body, ct)).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Older models reject the "think" flag; retry once without it.
            if (error.Contains("think", StringComparison.OrdinalIgnoreCase) && body.Remove("think"))
                response = await Send(() => _http.PostAsJsonAsync(new Uri(Endpoint, "api/chat"), body, ct)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OllamaException($"Ollama returned {(int)response.StatusCode}: {Trim(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false);
        return json?["message"]?["content"]?.GetValue<string>() ?? throw new OllamaException("Ollama returned no message content.");
    }

    /// <summary>Downloads a model. Only call this after the user confirmed the download size.</summary>
    public async IAsyncEnumerable<PullProgress> PullAsync(string model, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "api/pull"))
        {
            Content = JsonContent.Create(new JsonObject { ["model"] = model, ["stream"] = true }),
        };
        using var response = await Send(() => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var node = JsonNode.Parse(line);
            if (node?["error"]?.GetValue<string>() is { } error) throw new OllamaException(error);
            yield return new PullProgress(node?["status"]?.GetValue<string>() ?? "", node?["total"]?.GetValue<long>(), node?["completed"]?.GetValue<long>());
        }
    }

    private static async Task<T> Send<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaException(WhyLag.Core.Localization.Loc.T("ai.unreachable"), ex);
        }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            throw new OllamaException(WhyLag.Core.Localization.Loc.T("ai.timeout"), ex);
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
