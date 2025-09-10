using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;

public static class Win32DocFetcher
{
    private static readonly HttpClient _http = new HttpClient()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static Win32DocFetcher()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "Vibe-Decompiler/1.0");
    }

    /// <summary>
    /// Attempts to download HTML documentation for a given Windows API export.
    /// Uses the learn.microsoft.com search API to locate a documentation page.
    /// </summary>
    /// <param name="dllName">Name of the DLL that exports the function (optional filter).</param>
    /// <param name="exportName">Exported function name (e.g. "CreateFileW").</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The HTML string if found; otherwise <c>null</c>.</returns>
    public static async Task<string?> TryDownloadExportDocAsync(
        string dllName,
        string exportName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exportName))
            throw new ArgumentException("Export name must be provided", nameof(exportName));

        string query = exportName;
        if (!string.IsNullOrWhiteSpace(dllName))
            query = dllName + " " + exportName;

        string url = "https://learn.microsoft.com/api/search" +
                     "?" +
                     "search=" + Uri.EscapeDataString(query) +
                     "&scope=desktop&locale=en-us";

        JsonElement results;
        try
        {
            using var stream = await _http.GetStreamAsync(url, cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("results", out results))
                return null;
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

        string exportLower = exportName.ToLowerInvariant();
        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("url", out var urlProp))
                continue;
            string resultUrl = urlProp.GetString() ?? string.Empty;
            if (resultUrl.IndexOf("learn.microsoft.com", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            // Basic heuristic: ensure the URL contains the export name in lowercase.
            if (!resultUrl.ToLowerInvariant().Contains(exportLower))
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
