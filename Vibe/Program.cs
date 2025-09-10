using System.Text;

public static class Program
{
    static async Task Main(string[] args)
    {
        var disasm = DisassembleExportToPseudo("C:\\Windows\\System32\\Microsoft-Edge-WebView\\msedge.dll", "CreateTestWebClientProxy", 256 * 1024);
        Console.WriteLine(disasm);

        ILlmProvider? provider = null;
        string? openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        string? anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (!string.IsNullOrWhiteSpace(openAiKey))
            provider = new OpenAiLlmProvider(openAiKey);
        else if (!string.IsNullOrWhiteSpace(anthropicKey))
            provider = new AnthropicLlmProvider(anthropicKey);

        if (provider is not null)
        {
            try
            {
                string refined = await provider.RefineAsync(disasm);
                Console.WriteLine();
                Console.WriteLine("// ---- Refined by LLM ----");
                Console.WriteLine(refined);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"// ---- LLM refinement failed: {ex.Message} ----");
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
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
            db.LoadWin32MetadataFromWinmd(@"Microsoft.Windows.SDK.Win32Metadata.63.0.31-preview\Windows.Win32.winmd");
           
            var decompiler = new Decompiler();
            var options = new Decompiler.Options
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

        throw new InvalidOperationException("Too many forwarder hops; export may be forwarded through multiple API sets.");

        static (string dll, string name) ParseForwarder(string fwd)
        {
            // Formats:
            //   "KERNELBASE.GetVersionExW"
            //   "NTDLL.#123" (ordinal)
            int dot = fwd.IndexOf('.');
            if (dot <= 0 || dot == fwd.Length - 1)
                throw new FormatException($"Unexpected forwarder string: '{fwd}'");

            string dll = fwd.Substring(0, dot);
            string sym = fwd.Substring(dot + 1);

            // Normalize DLL name
            if (!dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                dll += ".dll";

            if (sym.StartsWith("#", StringComparison.Ordinal))
                throw new NotSupportedException($"Forwarder by ordinal not supported here: '{fwd}'");

            return (dll, sym);
        }
    }
}