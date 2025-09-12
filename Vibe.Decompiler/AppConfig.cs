// SPDX-License-Identifier: MIT-0

using System.Text.Json;
using Vibe.Utils;

namespace Vibe.Decompiler;

public sealed class AppConfig
{
    public static AppConfig Default => new AppConfig();
    public string LoadedFrom { get; set; } = "";
    public bool UseWin32DocsLookup { get; set; } = true;
    public bool UseWebSearch { get; set; } = true;
    public string LlmProvider { get; set; } = "";
    public string LlmVersion { get; set; } = "";
    public string LlmReasoningEffort { get; set; } = "";
    public int MaxDataSizeBytes { get; set; } = 256 * 1024;
    public int MaxLlmCodeLength { get; set; } = 16 * 1024;
    public int DocTimeoutSeconds { get; set; } = 30;
    public int DocFragmentSize { get; set; } = 4000;
    public int DocSearchMaxPages { get; set; } = 2;
    public int LlmMaxTokens { get; set; } = 4096;
    public int MaxForwarderHops { get; set; } = 8;
    public int MaxRecentFiles { get; set; } = 10;

    public static AppConfig? AutoDetect()
    {
        var findFile = FileUtils.FindFile("config.json");
        return findFile is null ? null : Load(findFile);
    }

    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return AppConfig.Default;

            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, options);
            cfg.LoadedFrom = path;
            return cfg;
        }
        catch
        {
            return AppConfig.Default;
        }
    }
}
