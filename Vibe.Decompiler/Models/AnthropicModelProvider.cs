// SPDX-License-Identifier: MIT-0

using System.Text;
using System.Text.Json;
using Vibe.Utils;

namespace Vibe.Decompiler.Models;

/// <summary>
/// Invokes Anthropic's Claude API to rewrite decompiled functions into clearer
/// C code. Instances are disposable and hold an internal HTTP client.
/// </summary>
public sealed class AnthropicModelProvider : IModelProvider
{
    private readonly HttpClient _http = new();
    /// <summary>API key used to authenticate with Anthropic.</summary>
    public string ApiKey { get; }
    /// <summary>Identifier of the Claude model to use.</summary>
    public string Model { get; }
    /// <summary>Maximum number of tokens to request in the response.</summary>
    public int MaxTokens { get; }
    /// <summary>API version header sent with each request.</summary>
    public string ApiVersion { get; }

    /// <summary>
    /// Initializes a new instance targeting the specified model and API version.
    /// </summary>
    public AnthropicModelProvider(string apiKey, string model = "claude-3-5-sonnet-20240620", string apiVersion = "2023-06-01", int maxTokens = 4096)
    {
        ApiKey = apiKey;
        Model = model;
        ApiVersion = apiVersion;
        MaxTokens = maxTokens;
        _http.DefaultRequestHeaders.Add("x-api-key", ApiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
    }

    /// <inheritdoc />
    public async Task<string> RefineAsync(
        string decompiledCode,
        string language,
        IEnumerable<string>? documentation = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<object>
        {
            new { role = "user", content = $"Rewrite the following decompiler output into readable C code, approximating the original source.\n\n{decompiledCode}" }
        };

        if (documentation is not null)
        {
            foreach (var docSnippet in documentation)
                messages.Add(new { role = "user", content = $"Reference documentation:\n{docSnippet}" });
        }

        var req = new
        {
            model = Model,
            max_tokens = MaxTokens,
            system = "You rewrite decompiled machine code into clear and idiomatic C code.",
            messages
        };

        var json = JsonSerializer.Serialize(req);
        Logger.Log($"Anthropic request: {json}");
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("https://api.anthropic.com/v1/messages", content, cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var errorContent = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Anthropic API request failed with status {resp.StatusCode}: {errorContent}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var contentArr = doc.RootElement.GetProperty("content");
        if (contentArr.GetArrayLength() == 0)
            throw new InvalidOperationException("Anthropic API returned no content");

        var message = contentArr[0].GetProperty("text").GetString();
        if (message is null)
            throw new InvalidOperationException("Anthropic API response missing text");

        message = message.Trim();
        Logger.Log($"Anthropic response: {message}");
        return message;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
