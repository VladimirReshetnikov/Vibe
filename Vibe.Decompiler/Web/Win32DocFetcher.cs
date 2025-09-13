// SPDX-License-Identifier: MIT-0

using System.Text.Json;
using Vibe.Utils;

namespace Vibe.Decompiler.Web;

/// <summary>
/// Provides helpers for locating and downloading documentation for Windows API
/// exports from the official learn.microsoft.com site.
/// </summary>
public static class Win32DocFetcher
{
    private static readonly HttpClient _http = new();

    static Win32DocFetcher()
    {
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language",
            "en-US,en;q=0.9");
    }

    /// <summary>
    /// Attempts to download HTML documentation for a given Windows API export.
    /// Uses the learn.microsoft.com search API to locate a documentation page.
    /// </summary>
    /// <param name="dllName">Name of the DLL that exports the function (optional filter).</param>
    /// <param name="exportName">Exported function name (e.g. "CreateFileW").</param>
    /// <param name="timeoutSeconds">HTTP request timeout in seconds.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The HTML string if found; otherwise <c>null</c>.</returns>
    public static async Task<string?> TryDownloadExportDocAsync(
        string dllName,
        string exportName,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exportName))
            throw new ArgumentException("Export name must be provided", nameof(exportName));

        _http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        string query = exportName;
        if (!string.IsNullOrWhiteSpace(dllName))
            query = dllName + " " + exportName;

        var uriBuilder = new UriBuilder("https://learn.microsoft.com/api/search");
        var queryParams = System.Web.HttpUtility.ParseQueryString(string.Empty);
        queryParams["search"] = query;
        queryParams["scope"] = "desktop";
        queryParams["locale"] = "en-us";
        uriBuilder.Query = queryParams.ToString();
        string url = uriBuilder.ToString();

        JsonElement results;
        try
        {
            await using var stream = await _http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
                return null;

            // Clone the results array so it survives after the JsonDocument is disposed
            results = resultsElement.Clone();
        }
        catch (HttpRequestException ex)
        {
            Logger.LogException(ex);
            // Search API request failed - return null to maintain "Try*" semantics
            return null;
        }
        catch (JsonException ex)
        {
            Logger.LogException(ex);
            // Invalid JSON response - return null to maintain "Try*" semantics
            return null;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Logger.LogException(ex);
            // User requested cancellation - propagate immediately
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Logger.LogException(ex);
            // Timeout occurred - return null to maintain "Try*" semantics
            return null;
        }

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("url", out var urlProp))
                continue;
            string resultUrl = urlProp.GetString() ?? string.Empty;
            if (resultUrl.IndexOf("learn.microsoft.com", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            // Basic heuristic: ensure the URL contains the export name (case-insensitive).
            if (resultUrl.IndexOf(exportName, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            try
            {
                return await _http.GetStringAsync(resultUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogException(ex);
                // Skip and try next result.
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                Logger.LogException(ex);
                // User requested cancellation - propagate immediately
                throw;
            }
            catch (OperationCanceledException ex)
            {
                Logger.LogException(ex);
                // Timeout occurred - skip and try next result.
            }
        }

        return null;
    }
}
