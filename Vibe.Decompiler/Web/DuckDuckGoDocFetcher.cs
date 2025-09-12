// SPDX-License-Identifier: MIT-0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Vibe.Utils;

namespace Vibe.Decompiler.Web;

/// <summary>
/// Uses an OpenAI model to decide whether downloaded documentation pages are
/// relevant to a particular function.
/// </summary>
public sealed class OpenAiDocPageEvaluator : IDocPageEvaluator
{
    private readonly HttpClient _http = new();
    public string ApiKey { get; }
    public string Model { get; }
    public string? ReasoningEffort { get; }

    /// <summary>
    /// Initializes a new evaluator that queries the specified OpenAI model.
    /// </summary>
    public OpenAiDocPageEvaluator(string apiKey, string model = "gpt-4o-mini", string? reasoningEffort = null)
    {
        ApiKey = apiKey;
        Model = model;
        ReasoningEffort = reasoningEffort;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    /// <inheritdoc />
    public async Task<bool> IsRelevantAsync(string functionName, string content, CancellationToken cancellationToken = default)
    {
        var req = new
        {
            model = Model,
            reasoning = string.IsNullOrWhiteSpace(ReasoningEffort) ? null : new { effort = ReasoningEffort },
            messages = new object[]
            {
                new { role = "system", content = "You are a classifier that answers yes or no." },
                new { role = "user", content = $"Function: {functionName}\n\nContent:\n{content}\n\nDoes the content provide useful technical information about the function? Answer yes or no." }
            }
        };

        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var json = JsonSerializer.Serialize(req, options);
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

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Scrapes DuckDuckGo search results and filters pages using an
/// <see cref="IDocPageEvaluator"/> implementation.
/// </summary>
public static class DuckDuckGoDocFetcher
{
    private static readonly HttpClient _http = new();

    private const string ResultLinkPattern =
        """<a[^>]*(?:class="result__a"[^>]*href="(?<url>[^"]*)"|href="(?<url>[^"]*)"[^>]*class="result__a")[^>]*>""";

    private static readonly char[] WordBreakChars = [' ', '\n', '\r', '\t'];

    /// <summary>
    /// Searches DuckDuckGo for documentation related to a function and filters pages using the provided evaluator.
    /// </summary>
    /// <param name="functionName">Function name to search for.</param>
    /// <param name="maxPages">Maximum number of search results to inspect.</param>
    /// <param name="evaluator">Evaluator that determines if a page is relevant.</param>
    /// <param name="fragmentSize">Maximum size of page fragments passed to the evaluator.</param>
    /// <param name="timeoutSeconds">HTTP request timeout in seconds.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public static async Task<List<string>> FindDocumentationPagesAsync(
        string functionName,
        int maxPages,
        IDocPageEvaluator evaluator,
        int fragmentSize = 4000,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new ArgumentException("Function name must be provided", nameof(functionName));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPages);
        ArgumentNullException.ThrowIfNull(evaluator);

        _http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        string queryUrl = $"https://duckduckgo.com/html/?q={Uri.EscapeDataString(functionName + " documentation")}&kl=us-en";

        string html;
        try
        {
            html = await _http.GetStringAsync(queryUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogException(ex);
            return [];
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Logger.LogException(ex);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Logger.LogException(ex);
            return [];
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
            catch (HttpRequestException ex)
            {
                Logger.LogException(ex);
                continue;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                Logger.LogException(ex);
                throw;
            }
            catch (OperationCanceledException ex)
            {
                Logger.LogException(ex);
                continue;
            }

            bool relevant = false;
            foreach (var fragment in SplitFragments(page, fragmentSize))
            {
                try
                {
                    if (await evaluator.IsRelevantAsync(functionName, fragment, cancellationToken))
                    {
                        relevant = true;
                        break;
                    }
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    Logger.LogException(ex);
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex);
                    // Ignore evaluator errors for this fragment
                }
            }

            if (relevant)
                pages.Add(page);
        }

        return pages;
    }

    /// <summary>
    /// Splits the HTML page into smaller fragments so that they can be analysed
    /// individually by the evaluator without exceeding token limits.
    /// </summary>
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
