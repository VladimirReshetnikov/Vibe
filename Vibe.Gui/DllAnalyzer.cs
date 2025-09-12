using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Vibe.Decompiler;
using Vibe.Decompiler.Models;

namespace Vibe.Gui;

/// <summary>
/// Provides high level analysis features for the WPF UI including optional
/// LLM based refinement of decompiled code.
/// </summary>
internal sealed class DllAnalyzer : IDisposable
{
    private readonly IModelProvider? _provider;
    /// <summary>Gets a value indicating whether an LLM provider is available.</summary>
    public bool HasLlmProvider => _provider != null;

    /// <summary>
    /// Initializes the analyzer and, if an API key is present, sets up an
    /// <see cref="OpenAiModelProvider"/> for code refinement.
    /// </summary>
    public DllAnalyzer()
    {
        var apiKey = App.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var cfg = AppConfig.Current;
            string model = string.IsNullOrWhiteSpace(cfg.LlmVersion) ? "gpt-4o-mini" : cfg.LlmVersion;
            _provider = new OpenAiModelProvider(apiKey, model, reasoningEffort: cfg.LlmReasoningEffort);
        }
    }

    /// <summary>Loads a DLL from disk and prepares it for analysis.</summary>
    public LoadedDll Load(string path)
    {
        return new LoadedDll(path);
    }

    /// <summary>Retrieves the list of exported function names.</summary>
    public Task<List<string>> GetExportNamesAsync(LoadedDll dll, CancellationToken token)
    {
        return dll.GetExportNamesAsync(token);
    }

    /// <summary>
    /// Enumerates all managed types defined in the assembly, including nested types.
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
    /// Returns the IL body of a managed method as a string.
    /// </summary>
    public string GetManagedMethodBody(MethodDefinition method)
    {
        if (!method.HasBody)
            return "// Method has no body";

        var sb = new StringBuilder();
        sb.AppendLine(method.FullName);
        foreach (var instr in method.Body.Instructions)
            sb.AppendLine(instr.ToString());
        return sb.ToString();
    }

    /// <summary>Builds a textual summary of the DLL and its hashes.</summary>
    public string GetSummary(LoadedDll dll) => dll.GetSummary();

    /// <summary>
    /// Decompiles an exported function, optionally refining the result using an LLM
    /// and caching the outcome for future requests.
    /// </summary>
    public async Task<string> GetDecompiledExportAsync(
        LoadedDll dll,
        string name,
        IProgress<string>? progress,
        CancellationToken token)
    {
        var hash = dll.FileHash;
        var cached = DecompiledCodeCache.TryGet(hash, name);
        if (cached != null)
            return cached;

        var export = dll.Pe.FindExport(name);
        if (export.IsForwarder)
        {
            var forwarderText = $"{name} -> {export.ForwarderString}";
            DecompiledCodeCache.Save(hash, name, forwarderText);
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
            output = await _provider.RefineAsync(code, null, token);

        DecompiledCodeCache.Save(hash, name, output);
        return output;
    }

    public string? FileName { get; set; }

    /// <summary>Releases resources including any LLM provider.</summary>
    public void Dispose()
    {
        _provider?.Dispose();
    }
}
