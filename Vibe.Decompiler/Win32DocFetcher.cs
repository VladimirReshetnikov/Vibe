// SPDX-License-Identifier: MIT-0

using System.Text.Json;

namespace Vibe.Decompiler;

public static class Win32DocFetcher
{
    private static readonly HttpClient _http = new();

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
            await using var stream = await _http.GetStreamAsync(url, cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
                return null;

            // Clone the results array so it survives after the JsonDocument is disposed
            results = resultsElement.Clone();
        }
        catch (HttpRequestException)
        {
            // Search API request failed - return null to maintain "Try*" semantics
            return null;
        }
        catch (JsonException)
        {
            // Invalid JSON response - return null to maintain "Try*" semantics
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User requested cancellation - propagate immediately
            throw;
        }
        catch (OperationCanceledException)
        {
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
                return await _http.GetStringAsync(resultUrl, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Skip and try next result.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User requested cancellation - propagate immediately
                throw;
            }
            catch (OperationCanceledException)
            {
                // Timeout occurred - skip and try next result.
            }
        }

        return null;
    }
}
