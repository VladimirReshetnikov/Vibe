// SPDX-License-Identifier: MIT-0

using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vibe.Decompiler;

public interface ILlmProvider : IDisposable
{
    Task<string> RefineAsync(
        string decompiledCode,
        IEnumerable<string>? documentation = null,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAiLlmProvider : ILlmProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromHours(1) }; // TODO: Make configurable
    public string ApiKey { get; }
    public string Model { get; }
    public string? ReasoningEffort { get; }

    public OpenAiLlmProvider(string apiKey, string model = "gpt-4o-mini", string? reasoningEffort = null)
    {
        ApiKey = apiKey;
        Model = model;
        ReasoningEffort = reasoningEffort;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    public async Task<string> RefineAsync(
        string decompiledCode,
        IEnumerable<string>? documentation = null,
        CancellationToken cancellationToken = default)
    {
        var input = new List<object>
        {
            new
            {
                role = "user",
                content =
                    $"Rewrite the following decompiler output into readable C or C++ code, " +
                    $"as close to the original source as possible. Output code only, not " +
                    $"enclosed in code fences. All your comments should appear only as part " +
                    $"of the code as syntactically valid C comments. You may (and should) add " +
                    $"auxiliary declarations of structs and other symbols where it makes sense.\n\n{decompiledCode}"
            }
        };

        if (documentation is not null)
        {
            foreach (var docSnippet in documentation)
                input.Add(new { role = "user", content = $"Reference documentation:\n{docSnippet}" });
        }

        var instructions = "You rewrite decompiled machine code into clear and idiomatic C code.";

        var req = new
        {
            model = Model,
            reasoning = string.IsNullOrWhiteSpace(ReasoningEffort) ? null : new { effort = ReasoningEffort },
            instructions,
            input
        };

        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var json = JsonSerializer.Serialize(req, options);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("https://api.openai.com/v1/responses", content, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var errorContent = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"OpenAI API request failed with status {resp.StatusCode}: {errorContent}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("output", out var output) || output.GetArrayLength() == 0)
            throw new InvalidOperationException("OpenAI API returned no output");

        JsonElement messageObj = default;
        var foundMessage = false;
        foreach (var element in output.EnumerateArray())
        {
            if (element.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "message")
            {
                messageObj = element;
                foundMessage = true;
                break;
            }
        }

        if (!foundMessage)
            throw new InvalidOperationException("OpenAI API response missing message");

        if (!messageObj.TryGetProperty("content", out var contentArr) ||
            contentArr.ValueKind != JsonValueKind.Array ||
            contentArr.GetArrayLength() == 0)
            throw new InvalidOperationException("OpenAI API response missing content");

        string? message = null;
        foreach (var part in contentArr.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textProp))
            {
                message = textProp.GetString();
                if (!string.IsNullOrEmpty(message))
                    break;
            }
        }

        if (string.IsNullOrEmpty(message))
            throw new InvalidOperationException("OpenAI API response missing text");

        return message.Trim();
    }

    public void Dispose() => _http.Dispose();
}

public sealed class AnthropicLlmProvider : ILlmProvider
{
    private readonly HttpClient _http = new();
    public string ApiKey { get; }
    public string Model { get; }
    public int MaxTokens { get; }
    public string ApiVersion { get; }

    public AnthropicLlmProvider(string apiKey, string model = "claude-3-5-sonnet-20240620", string apiVersion = "2023-06-01", int maxTokens = 4096)
    {
        ApiKey = apiKey;
        Model = model;
        ApiVersion = apiVersion;
        MaxTokens = maxTokens;
        _http.DefaultRequestHeaders.Add("x-api-key", ApiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
    }

    public async Task<string> RefineAsync(
        string decompiledCode,
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
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("https://api.anthropic.com/v1/messages", content, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var errorContent = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Anthropic API request failed with status {resp.StatusCode}: {errorContent}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        var contentArr = doc.RootElement.GetProperty("content");
        if (contentArr.GetArrayLength() == 0)
            throw new InvalidOperationException("Anthropic API returned no content");

        var message = contentArr[0].GetProperty("text").GetString();
        if (message is null)
            throw new InvalidOperationException("Anthropic API response missing text");

        return message.Trim();
    }

    public void Dispose() => _http.Dispose();
}
