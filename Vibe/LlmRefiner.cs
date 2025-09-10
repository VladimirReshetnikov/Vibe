using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public interface ILlmProvider
{
    Task<string> RefineAsync(string decompiledCode, CancellationToken cancellationToken = default);
}

public sealed class OpenAiLlmProvider : ILlmProvider
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
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return message?.Trim() ?? string.Empty;
    }
}

public sealed class AnthropicLlmProvider : ILlmProvider
{
    private readonly HttpClient _http = new();
    public string ApiKey { get; }
    public string Model { get; }

    public AnthropicLlmProvider(string apiKey, string model = "claude-3-5-sonnet-20240620")
    {
        ApiKey = apiKey;
        Model = model;
        _http.DefaultRequestHeaders.Add("x-api-key", ApiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<string> RefineAsync(string decompiledCode, CancellationToken cancellationToken = default)
    {
        var req = new
        {
            model = Model,
            max_tokens = 1024,
            system = "You rewrite decompiled machine code into clear and idiomatic C code.",
            messages = new object[]
            {
                new { role = "user", content = $"Rewrite the following decompiler output into readable C code, approximating the original source.\n\n{decompiledCode}" }
            }
        };

        var json = JsonSerializer.Serialize(req);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("https://api.anthropic.com/v1/messages", content, cancellationToken);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        var message = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
        return message?.Trim() ?? string.Empty;
    }
}

public static class LlmRefiner
{
    public static Task<string> RefineAsync(string decompiledCode, ILlmProvider provider, CancellationToken cancellationToken = default)
        => provider.RefineAsync(decompiledCode, cancellationToken);
}

