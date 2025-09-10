// SPDX-License-Identifier: MIT-0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public interface IDocPageEvaluator : IDisposable
{
    Task<bool> IsRelevantAsync(string functionName, string content, CancellationToken cancellationToken = default);
}

public sealed class OpenAiDocPageEvaluator : IDocPageEvaluator
{
    private readonly HttpClient _http = new();
    public string ApiKey { get; }
    public string Model { get; }

    public OpenAiDocPageEvaluator(string apiKey, string model = "gpt-4o-mini")
    {
        ApiKey = apiKey;
        Model = model;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    public async Task<bool> IsRelevantAsync(string functionName, string content, CancellationToken cancellationToken = default)
    {
        var req = new
        {
            model = Model,
            messages = new object[]
            {
                new { role = "system", content = "You are a classifier that answers yes or no." },
                new { role = "user", content = $"Function: {functionName}\n\nContent:\n{content}\n\nDoes the content provide useful technical information about the function? Answer yes or no." }
            }
        };

        var json = JsonSerializer.Serialize(req);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("https://api.openai.com/v1/chat/completions", httpContent, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var errorContent = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"OpenAI API request failed with status {resp.StatusCode}: {errorContent}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            return false;

        var message = choices[0].GetProperty("message").GetProperty("content").GetString();
        if (message is null)
            return false;

        return message.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _http.Dispose();
}

public static class DuckDuckGoDocFetcher
{
    private static readonly HttpClient _http = new HttpClient()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static DuckDuckGoDocFetcher()
    {
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("Vibe-Decompiler/1.0");
    }

    private const int FragmentSize = 4000;
    private static readonly string ResultLinkPattern =
        @"<a[^>]*(?:class=""result__a""[^>]*href=""(?<url>[^""]*)""|href=""(?<url>[^""]*)""[^>]*class=""result__a"")[^>]*>";
    private static readonly char[] WordBreakChars = { ' ', '\n', '\r', '\t' };

    public static async Task<List<string>> FindDocumentationPagesAsync(
        string functionName,
        int maxPages,
        IDocPageEvaluator evaluator,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new ArgumentException("Function name must be provided", nameof(functionName));
        if (maxPages <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPages));
        if (evaluator is null)
            throw new ArgumentNullException(nameof(evaluator));

        string queryUrl = $"https://duckduckgo.com/html/?q={Uri.EscapeDataString(functionName + " documentation")}&kl=us-en";

        string html;
        try
        {
            html = await _http.GetStringAsync(queryUrl, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new List<string>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new List<string>();
        }

        var linkMatches = Regex.Matches(html, ResultLinkPattern, RegexOptions.IgnoreCase);
        var links = linkMatches.Cast<Match>()
            .Select(m => m.Groups["url"].Value)
            .Where(u => !string.IsNullOrEmpty(u))
            .Distinct()
            .Take(maxPages)
            .ToList();

        var pages = new List<string>();

        foreach (var link in links)
        {
            string page;
            try
            {
                page = await _http.GetStringAsync(link, cancellationToken);
            }
            catch (HttpRequestException)
            {
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                continue;
            }

            bool relevant = false;
            foreach (var fragment in SplitFragments(page, FragmentSize))
            {
                try
                {
                    if (await evaluator.IsRelevantAsync(functionName, fragment, cancellationToken))
                    {
                        relevant = true;
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Ignore evaluator errors for this fragment
                }
            }

            if (relevant)
                pages.Add(page);
        }

        return pages;
    }

    private static IEnumerable<string> SplitFragments(string text, int maxLen)
    {
        int index = 0;
        while (index < text.Length)
        {
            int length = Math.Min(maxLen, text.Length - index);
            int nextIndex = index + length;
            if (nextIndex < text.Length)
            {
                int lastBreak = text.LastIndexOfAny(WordBreakChars, nextIndex - 1, length);
                if (lastBreak > index)
                {
                    length = lastBreak - index + 1;
                    nextIndex = lastBreak + 1;
                }
            }
            yield return text.Substring(index, length);
            index = nextIndex;
        }
    }
}

