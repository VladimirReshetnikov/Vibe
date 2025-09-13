// SPDX-License-Identifier: MIT-0
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vibe.Utils;

namespace Vibe.Decompiler.Models;

/// <summary>
/// IModelProvider that communicates with the OpenAI Responses API to refine
/// decompiled output using GPT models.
/// </summary>
public sealed class OpenAiModelProvider : IModelProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromHours(1) }; // TODO: Make configurable
    /// <summary>API key used to authenticate against the OpenAI service.</summary>
    public string ApiKey { get; }
    /// <summary>Name of the OpenAI model to invoke.</summary>
    public string Model { get; }
    /// <summary>Optional reasoning effort hint for models that support it.</summary>
    public string? ReasoningEffort { get; }

    /// <summary>
    /// Creates a provider that targets the specified OpenAI model.
    /// </summary>
    public OpenAiModelProvider(string apiKey, string model = "gpt-4o-mini", string? reasoningEffort = null)
    {
        ApiKey = apiKey;
        Model = model;
        ReasoningEffort = reasoningEffort;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    /// <inheritdoc />
    public async Task<string> RefineAsync(
        string decompiledCode,
        string language,
        IEnumerable<string>? documentation = null,
        CancellationToken cancellationToken = default)
    {
        var input = new List<object>
        {
            new
            {
                role = "user",
                content =
                    $"Rewrite the following decompiler output into readable {language} code, " +
                    $"as close to the original source as possible. Output code only, not " +
                    $"enclosed in code fences. All your comments should appear only as part " +
                    $"of the code as syntactically well-formed comments. You may add " +
                    $"auxiliary declarations of structs and other symbols where it makes sense.\n\n{decompiledCode}"
            }
        };

        if (documentation is not null)
        {
            foreach (var docSnippet in documentation)
                input.Add(new { role = "user", content = $"Reference documentation:\n{docSnippet}" });
        }

        var instructions = $"You rewrite decompiled machine code into clear and idiomatic {language} code.";

        var req = new
        {
            model = Model,
            reasoning = string.IsNullOrWhiteSpace(ReasoningEffort) ? null : new { effort = ReasoningEffort },
            instructions,
            input
        };

        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var json = JsonSerializer.Serialize(req, options);
        Logger.Log($"OpenAI request: {json}");
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("https://api.openai.com/v1/responses", content, cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var errorContent = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"OpenAI API request failed with status {resp.StatusCode}: {errorContent}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
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

        message = message.Trim();
        Logger.Log($"OpenAI response: {message}");
        return message;
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
