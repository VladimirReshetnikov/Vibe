// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Vibe.Utils;

namespace Vibe.Decompiler;

public static class Program
{
    static async Task Main(string[] args)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = AppConfig.Load(configPath);

        string dllPath = @"C:\Windows\System32\Microsoft-Edge-WebView\msedge.dll";
        string exportName = "CreateTestWebClientProxy";

        var disasm = DisassembleExportToPseudo(dllPath, exportName, config.MaxDataSizeBytes, config.MaxForwarderHops);
        Console.WriteLine(disasm);

        var docs = new List<string>();
        if (config.UseWin32DocsLookup)
        {
            try
            {
                string? msDoc = await Win32DocFetcher.TryDownloadExportDocAsync(
                    Path.GetFileName(dllPath),
                    exportName,
                    config.DocTimeoutSeconds);
                if (msDoc is not null)
                    docs.Add(msDoc);
            }
            catch { }
        }

        ILlmProvider? provider = null;
        string? openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        string? anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        switch (config.LlmProvider?.ToLowerInvariant())
        {
            case "openai" when !string.IsNullOrWhiteSpace(openAiKey):
                {
                    string model = string.IsNullOrWhiteSpace(config.LlmVersion) ? "gpt-4o-mini" : config.LlmVersion;
                    provider = new OpenAiLlmProvider(openAiKey, model);
                    if (config.UseWebSearch)
                    {
                        try
                        {
                            using var evaluator = new OpenAiDocPageEvaluator(openAiKey, model);
                            var pages = await DuckDuckGoDocFetcher.FindDocumentationPagesAsync(
                                exportName,
                                config.DocSearchMaxPages,
                                evaluator,
                                config.DocFragmentSize,
                                config.DocTimeoutSeconds);
                            docs.AddRange(pages);
                        }
                        catch { }
                    }
                    break;
                }
            case "anthropic" when !string.IsNullOrWhiteSpace(anthropicKey):
                {
                    string model = string.IsNullOrWhiteSpace(config.LlmVersion) ? "claude-3-5-sonnet-20240620" : config.LlmVersion;
                    provider = new AnthropicLlmProvider(anthropicKey, model, maxTokens: config.LlmMaxTokens);
                    break;
                }
        }

        if (provider is not null)
        {
            string truncated = disasm;
            if (config.MaxLlmCodeLength > 0 && truncated.Length > config.MaxLlmCodeLength)
                truncated = truncated.Substring(0, config.MaxLlmCodeLength);

            try
            {
                if (provider is IDisposable disposable)
                {
                    using (disposable)
                    {
                        string refined = await provider.RefineAsync(truncated, docs);
                        Console.WriteLine();
                        Console.WriteLine("// ---- Refined by LLM ----");
                        Console.WriteLine(refined);
                    }
                }
                else
                {
                    string refined = await provider.RefineAsync(truncated, docs);
                    Console.WriteLine();
                    Console.WriteLine("// ---- Refined by LLM ----");
                    Console.WriteLine(refined);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"// ---- LLM refinement failed: {ex.Message} ----");
            }
            finally
            {
                provider.Dispose();
            }
        }
    }

    /// <summary>
    /// Disassembles a Windows exported function (default: ntdll!RtlGetVersion) into C-like pseudocode
    /// by extracting its body from the PE file and passing the bytes to MsvcFunctionPseudoDecompiler.
    /// </summary>
    /// <param name="dllName">e.g., "ntdll.dll", "kernelbase.dll"</param>
    /// <param name="exportName">e.g., "RtlGetVersion"</param>
    /// <param name="maxBytes">
    /// Maximum bytes to read from the function start (bounded by end of section). The decompiler
    /// stops at the first RET anyway; this just keeps us from running off into unrelated code when RET is missing.
    /// </param>
    /// <returns>Pseudocode string with a small header (path, RVA, ImageBase)</returns>
    public static string DisassembleExportToPseudo(
        string dllName,
        string exportName,
        int maxBytes = 4096,
        int maxForwarderHops = 8)
    {
        // Resolve a *64-bit* System32 path even if this process is 32-bit (WOW64).
        string ResolveSystemDllPath(string name)
        {
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                name += ".dll";

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string dir = (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
                ? Path.Combine(windows, "Sysnative") // 32-bit process reaching 64-bit System32
                : Path.Combine(windows, "System32");

            string path = Path.Combine(dir, name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"System DLL not found: {path}");
            return path;
        }

        // --- Minimal PE reader (PE32+ only; enough for exports + sections) ----
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string curDllPath = ResolveSystemDllPath(dllName);
        string curExport = exportName;

        for (int hop = 0; hop < maxForwarderHops; hop++)
        {
            if (!visited.Add($"{curDllPath}!{curExport}"))
                throw new InvalidOperationException("Forwarder loop detected.");

            var pe = new PEReaderLite(curDllPath);
            var export = pe.FindExport(curExport);

            if (export.IsForwarder)
            {
                // Forwarder string like "KERNELBASE.GetVersionExW" or "NTDLL.#123"
                (string fwdDll, string fwdName) = ParseForwarder(export.ForwarderString);
                curDllPath = ResolveSystemDllPath(fwdDll);
                curExport = fwdName;
                continue;
            }

            // Extract body bytes bounded by end of section and maxBytes.
            int funcOff = pe.RvaToOffsetChecked(export.FunctionRva);
            var sec = pe.GetSectionForRva(export.FunctionRva)
                      ?? throw new InvalidOperationException("Function RVA not contained in any section.");
            int secEnd = checked((int)sec.PointerToRawData + (int)sec.SizeOfRawData);
            int maxAvail = Math.Max(0, secEnd - funcOff);
            int take = Math.Min(maxBytes, maxAvail);
            if (take <= 0)
                throw new InvalidOperationException("No bytes available for function body.");

            ReadOnlyMemory<byte> body = pe.Data.AsMemory(funcOff, take);
            var db = new ConstantDatabase();
            TryLoadWin32Metadata(db);

            var decompiler = new Engine();
            var options = new Engine.Options
            {
                BaseAddress = pe.ImageBase + export.FunctionRva,
                FunctionName = $"{Path.GetFileName(curDllPath)}!{curExport}",
                MaxBytes = take,
                EmitLabels = true,
                DetectPrologue = true,
                ConstantProvider = db,
            };

            string pseudo = decompiler.ToPseudoCode(body, options);

            var header = new StringBuilder();
            header.AppendLine($"// Source DLL  : {curDllPath}");
            header.AppendLine($"// Export      : {curExport}");
            header.AppendLine($"// ImageBase   : 0x{pe.ImageBase:X}");
            header.AppendLine($"// FunctionRVA : 0x{export.FunctionRva:X}");
            header.AppendLine($"// Slice bytes : {take} (bounded by section end and maxBytes={maxBytes})");
            header.AppendLine();

            return header.ToString() + pseudo;
        }

        throw new InvalidOperationException(
            "Too many forwarder hops; export may be forwarded through multiple API sets.");

        static (string dll, string name) ParseForwarder(string fwd)
        {
            // Formats:
            //   "KERNELBASE.GetVersionExW"
            //   "NTDLL.#123" (ordinal)
            int dot = fwd.IndexOf('.');
            if (dot <= 0 || dot == fwd.Length - 1)
                throw new FormatException($"Unexpected forwarder string: '{fwd}'");

            string dll = fwd[..dot];
            string sym = fwd[(dot + 1)..];

            // Normalize DLL name
            if (!dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                dll += ".dll";

            if (sym.StartsWith('#'))
                throw new NotSupportedException($"Forwarder by ordinal not supported here: '{fwd}'");

            return (dll, sym);
        }
    }


    static void TryLoadWin32Metadata(ConstantDatabase db)
    {
        try
        {
            var winmdRegistry = FileUtils.FindFile("Windows.Win32.winmd");
            if (winmdRegistry is not null)
            {
                db.LoadWin32MetadataFromWinmd(winmdRegistry);
                return;
            }

            foreach (var cache in FileUtils.GetNuGetCacheDirectories())
            {
                if (!Directory.Exists(cache)) continue;

                // First try extracted packages (more common in global packages folder)
                foreach (var packageDir in Directory.EnumerateDirectories(cache, "microsoft.windows.sdk.win32metadata*",
                             SearchOption.TopDirectoryOnly))
                {
                    foreach (var winmdFile in Directory.EnumerateFiles(packageDir, "Windows.Win32.winmd",
                                 SearchOption.AllDirectories))
                    {
                        db.LoadWin32MetadataFromWinmd(winmdFile);
                        return;
                    }
                }

                // Fallback to .nupkg files (for HTTP cache locations)
                foreach (var nupkg in Directory.EnumerateFiles(cache, "Microsoft.Windows.SDK.Win32Metadata*.nupkg",
                             SearchOption.AllDirectories))
                {
                    try
                    {
                        using var zip = ZipFile.OpenRead(nupkg);
                        var entry = zip.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith("Windows.Win32.winmd", StringComparison.OrdinalIgnoreCase));
                        if (entry is null) continue;
                        string tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".winmd");
                        entry.ExtractToFile(tempPath, true);
                        try
                        {
                            db.LoadWin32MetadataFromWinmd(tempPath);
                        }
                        finally
                        {
                            try { File.Delete(tempPath); } catch { }
                        }
                        return;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }
}
