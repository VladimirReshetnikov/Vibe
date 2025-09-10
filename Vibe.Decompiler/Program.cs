// SPDX-License-Identifier: MIT-0

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Vibe.Decompiler;

public static class Program
{
    static async Task Main(string[] args)
    {
        bool webAccess = args.Any(a => a == "--web-access");
        string dllPath = "C:\\Windows\\System32\\Microsoft-Edge-WebView\\msedge.dll";
        string exportName = "CreateTestWebClientProxy";

        var disasm = DisassembleExportToPseudo(dllPath, exportName, 256 * 1024);
        Console.WriteLine(disasm);
        var docs = new List<string>();
        if(webAccess)
        {
            try
            {
                string? msDoc = await Win32DocFetcher.TryDownloadExportDocAsync(Path.GetFileName(dllPath), exportName);
                if (msDoc is not null)
                    docs.Add(msDoc);
            }
            catch { }
        }
        ILlmProvider? provider = null;
        string? openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        string? anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            provider = new OpenAiLlmProvider(openAiKey);
            try
            {
                using var evaluator = new OpenAiDocPageEvaluator(openAiKey);
                if (webAccess)
                {
                    var pages = await DuckDuckGoDocFetcher.FindDocumentationPagesAsync(exportName, 2, evaluator);
                    docs.AddRange(pages);
                }
            }
            catch { }
        }
        else if (!string.IsNullOrWhiteSpace(anthropicKey))
        {
            provider = new AnthropicLlmProvider(anthropicKey);
        }

        if (provider is not null)
        {
            try
            {
                if (provider is IDisposable disposable)
                {
                    using (disposable)
                    {
                        string refined = await provider.RefineAsync(disasm, docs);
                        Console.WriteLine();
                        Console.WriteLine("// ---- Refined by LLM ----");
                        Console.WriteLine(refined);
                    }
                }
                else
                {
                    string refined = await provider.RefineAsync(disasm, docs);
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
        int maxBytes = 4096)
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

        for (int hop = 0; hop < 8; hop++)
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

            byte[] body = new byte[take];
            Buffer.BlockCopy(pe.Data, funcOff, body, 0, take);
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
            string? repoRoot = FindRepoRoot();
            if (repoRoot is not null)
            {
                foreach (var file in Directory.EnumerateFiles(repoRoot, "Windows.Win32.winmd",
                             SearchOption.AllDirectories))
                {
                    db.LoadWin32MetadataFromWinmd(file);
                    return;
                }
            }

            foreach (var cache in GetNuGetCacheDirectories())
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

    static string? FindRepoRoot()
    {
        try
        {
            string dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "Vibe.sln")))
                    return dir;
                string? parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        catch { }
        return null;
    }

    static IEnumerable<string> GetNuGetCacheDirectories()
    {
        var dirs = new List<string>();
        string? env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(env))
            dirs.Add(env);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            dirs.Add(Path.Combine(profile, ".nuget", "packages"));

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            dirs.Add(Path.Combine(local, "NuGet", "Cache"));
            dirs.Add(Path.Combine(local, "NuGet", "v3-cache"));
        }

        return dirs;
    }
}
