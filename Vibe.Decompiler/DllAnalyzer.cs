// SPDX-License-Identifier: MIT-0

using System.Text;
using System.Diagnostics;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using Vibe.Decompiler.Models;
using Vibe.Decompiler.PE;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;

namespace Vibe.Decompiler;

/// <summary>
/// Provides high level operations for inspecting a DLL, such as retrieving
/// export names or decompiling functions. Optional caching and LLM-based
/// refinement can be supplied via constructor parameters.
/// </summary>
public sealed class DllAnalyzer : IDisposable
{
    private readonly IModelProvider? _provider;
    private readonly Func<string, string, string?>? _cacheGet;
    private readonly Action<string, string, string>? _cacheSave;

    /// <summary>Gets a value indicating whether an LLM provider is available.</summary>
    public bool HasLlmProvider => _provider != null;

    /// <summary>
    /// Initializes a new instance of the analyzer.
    /// </summary>
    public DllAnalyzer(
        IModelProvider? provider = null,
        Func<string, string, string?>? cacheGet = null,
        Action<string, string, string>? cacheSave = null)
    {
        _provider = provider;
        _cacheGet = cacheGet;
        _cacheSave = cacheSave;
    }

    /// <summary>Loads a DLL from disk and computes basic metadata.</summary>
    public LoadedDll Load(string path) => new(path);

    /// <summary>Asynchronously retrieves the export names from the DLL.</summary>
    public Task<List<string>> GetExportNamesAsync(LoadedDll dll, CancellationToken token)
        => dll.GetExportNamesAsync(token);

    /// <summary>
    /// Enumerates all managed types defined in the DLL, including nested types.
    /// </summary>
    public Task<List<TypeDefinition>> GetManagedTypesAsync(LoadedDll dll, CancellationToken token)
    {
        if (!dll.IsManaged || dll.ManagedModule == null)
            return Task.FromResult(new List<TypeDefinition>());

        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var result = new List<TypeDefinition>();
            void Collect(TypeDefinition type)
            {
                result.Add(type);
                foreach (var nested in type.NestedTypes)
                    Collect(nested);
            }
            foreach (var type in dll.ManagedModule.Types)
                Collect(type);
            return result;
        }, token);
    }

    /// <summary>
    /// Returns decompiled C# code for the specified managed method.
    /// </summary>
    public async Task<string> GetManagedMethodBodyAsync(LoadedDll dll, MethodDefinition method)
    {
        if (!method.HasBody)
            return "// Method has no body";

        try
        {
            var modulePath = method.Module.FileName;
            if (string.IsNullOrEmpty(modulePath))
                return "// Cannot locate module file";

            var decompiler = new CSharpDecompiler(modulePath, new DecompilerSettings());
            var handle = MetadataTokens.EntityHandle(method.MetadataToken.ToInt32());
            var code = decompiler.DecompileAsString(handle);

            if (_provider != null && AppConfig.Current.MaxLlmCodeLength > 0 && code.Length > AppConfig.Current.MaxLlmCodeLength)
                code = code[..AppConfig.Current.MaxLlmCodeLength];

            if (_provider != null)
            {
                var context = BuildLlmContext(dll);
                code = await _provider.RefineAsync(context + code, "C#", null, CancellationToken.None);
            }

            return code;
        }
        catch (Exception ex)
        {
            return $"// Error decompiling method: {ex.Message}";
        }
    }

    /// <summary>Creates a textual summary containing PE information and hashes.</summary>
    public string GetSummary(LoadedDll dll) => dll.GetSummary();

    /// <summary>
    /// Decompiles the specified exported function into pseudocode and optionally
    /// refines and caches the result.
    /// </summary>
    public async Task<string> GetDecompiledExportAsync(
        LoadedDll dll,
        string name,
        IProgress<string>? progress,
        CancellationToken token)
    {
        var hash = dll.FileHash;
        var cached = _cacheGet?.Invoke(hash, name);
        if (cached != null)
            return cached;

        var export = dll.Pe.FindExport(name);
        if (export.IsForwarder)
        {
            var forwarderText = $"{name} -> {export.ForwarderString}";
            _cacheSave?.Invoke(hash, name, forwarderText);
            return forwarderText;
        }

        var code = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            int off = dll.Pe.RvaToOffsetChecked(export.FunctionRva);
            int maxLen = Math.Min(AppConfig.Current.MaxDataSizeBytes, dll.Pe.Data.Length - off);
            var engine = new Engine();
            return engine.ToPseudoCode(dll.Pe.Data.AsMemory(off, maxLen), new Engine.Options
            {
                BaseAddress = dll.Pe.ImageBase + export.FunctionRva,
                FunctionName = name,
                FilePath = dll.Pe.FilePath
            });
        }, token);

        if (_provider != null && AppConfig.Current.MaxLlmCodeLength > 0 && code.Length > AppConfig.Current.MaxLlmCodeLength)
            code = code[..AppConfig.Current.MaxLlmCodeLength];

        progress?.Report(code);

        var output = code;
        if (_provider != null)
        {
            var context = BuildLlmContext(dll);
            output = await _provider.RefineAsync(context + code, "C/C++", null, token);
        }

        _cacheSave?.Invoke(hash, name, output);
        return output;
    }

    private static string BuildLlmContext(LoadedDll dll)
    {
        var path = dll.Pe.FilePath;
        var vi = FileVersionInfo.GetVersionInfo(path);
        var sb = new StringBuilder();
        sb.AppendLine("/*");
        sb.AppendLine($"File: {Path.GetFileName(path)}");
        sb.AppendLine($"Path: {path}");
        sb.AppendLine($"Timestamp: 0x{dll.Pe.TimeDateStamp:X8} ({DateTimeOffset.FromUnixTimeSeconds(dll.Pe.TimeDateStamp).UtcDateTime:u})");
        if (!string.IsNullOrWhiteSpace(vi.FileVersion))
            sb.AppendLine($"File version: {vi.FileVersion}");
        if (!string.IsNullOrWhiteSpace(vi.ProductVersion))
            sb.AppendLine($"Informational version: {vi.ProductVersion}");
        if (!string.IsNullOrWhiteSpace(vi.ProductName))
            sb.AppendLine($"Product: {vi.ProductName}");
        if (!string.IsNullOrWhiteSpace(vi.CompanyName))
            sb.AppendLine($"Company: {vi.CompanyName}");
        sb.AppendLine("*/");
        var compilerInfo = CompilerInfo.Analyze(path).ToString();
        if (!string.IsNullOrWhiteSpace(compilerInfo))
            sb.AppendLine(compilerInfo);
        return sb.ToString();
    }

    public string? FileName { get; set; }

    /// <summary>Releases resources including any LLM provider.</summary>
    public void Dispose()
    {
        _provider?.Dispose();
    }
}

