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

internal sealed class DllAnalyzer : IDisposable
{
    private readonly IModelProvider? _provider;
    public bool HasLlmProvider => _provider != null;

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

    public LoadedDll Load(string path)
    {
        return new LoadedDll(path);
    }

    public Task<List<string>> GetExportNamesAsync(LoadedDll dll, CancellationToken token)
    {
        return dll.GetExportNamesAsync(token);
    }

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

    public string GetSummary(LoadedDll dll) => dll.GetSummary();

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
                FunctionName = name
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

    public void Dispose()
    {
        _provider?.Dispose();
    }
}
