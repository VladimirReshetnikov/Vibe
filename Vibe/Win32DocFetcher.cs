using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

public static class Win32DocFetcher
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static Win32DocFetcher()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Vibe-Decompiler", "1.0"));
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

        var uriBuilder = new UriBuilder("https://learn.microsoft.com/api/search");
        var queryParams = System.Web.HttpUtility.ParseQueryString(string.Empty);
        queryParams["search"] = query;
        queryParams["scope"] = "desktop";
        queryParams["locale"] = "en-us";
        uriBuilder.Query = queryParams.ToString();
        string url = uriBuilder.ToString();

        try
        {
            using var stream = await _http.GetStreamAsync(url, cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return null;

            string exportLower = exportName.ToLowerInvariant();
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("url", out var urlProp))
                    continue;
                string resultUrl = urlProp.GetString() ?? string.Empty;
                if (!resultUrl.Contains("learn.microsoft.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Basic heuristic: ensure the URL contains the export name in lowercase.
                if (!resultUrl.Contains(exportLower, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    // Use a HEAD request first to ensure the URL is valid and points to HTML content.
                    using var headReq = new HttpRequestMessage(HttpMethod.Head, resultUrl);
                    using var headResp = await _http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!headResp.IsSuccessStatusCode)
                        continue;
                    var mediaType = headResp.Content.Headers.ContentType?.MediaType;
                    if (mediaType is null || !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string html = await _http.GetStringAsync(resultUrl, cancellationToken);

                    // Ensure the page looks like Microsoft Learn documentation.
                    if (!html.Contains("data-target=\"docs\"", StringComparison.OrdinalIgnoreCase))
                        continue;

                    return html;
                }
                catch (HttpRequestException)
                {
                    // Skip and try next result.
                }
                catch (TaskCanceledException)
                {
                    // Skip and try next result.
                }
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }

        return null;
    }
}

