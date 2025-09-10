using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public interface ILlmProvider : IDisposable
{
    Task<string> RefineAsync(string decompiledCode, CancellationToken cancellationToken = default);
}

public sealed class OpenAiLlmProvider : ILlmProvider, IDisposable
{
    private readonly HttpClient _http = new();
    public string ApiKey { get; }
    public string Model { get; }

    public OpenAiLlmProvider(string apiKey, string model = "gpt-4o-mini")
    {
        ApiKey = apiKey;
        Model = model;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    public async Task<string> RefineAsync(string decompiledCode, CancellationToken cancellationToken = default)
    {
        var req = new
        {
            model = Model,
            messages = new object[]
            {
                new { role = "system", content = "You rewrite decompiled machine code into clear and idiomatic C code." },
                new { role = "user", content = $"Rewrite the following decompiler output into readable C code, approximating the original source.\n\n{decompiledCode}" }
            }
        };

        var json = JsonSerializer.Serialize(req);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("https://api.openai.com/v1/chat/completions", content, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var errorContent = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"OpenAI API request failed with status {resp.StatusCode}: {errorContent}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            throw new InvalidOperationException("OpenAI API returned no choices");

        var message = choices[0].GetProperty("message").GetProperty("content").GetString();
        if (message is null)
            throw new InvalidOperationException("OpenAI API response missing content");

        return message.Trim();
    }

    public void Dispose() => _http.Dispose();
}

public sealed class AnthropicLlmProvider : ILlmProvider, IDisposable
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

    public async Task<string> RefineAsync(string decompiledCode, CancellationToken cancellationToken = default)
    {
        var req = new
        {
            model = Model,
            max_tokens = MaxTokens,
            system = "You rewrite decompiled machine code into clear and idiomatic C code.",
            messages = new object[]
            {
                new { role = "user", content = $"Rewrite the following decompiler output into readable C code, approximating the original source.\n\n{decompiledCode}" }
            }
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

