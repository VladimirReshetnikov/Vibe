using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Vibe.Decompiler;

namespace Vibe.Cui;

/// <summary>
/// Provides high level operations for inspecting a DLL from the console
/// interface, such as retrieving export names or decompiling functions.
/// </summary>
internal sealed class DllAnalyzer : IDisposable
{
    /// <summary>Loads a DLL from disk and computes basic metadata.</summary>
    public LoadedDll Load(string path) => new LoadedDll(path);

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
    /// Returns the IL instructions that make up the specified managed method.
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

    /// <summary>Creates a textual summary containing PE information and hashes.</summary>
    public string GetSummary(LoadedDll dll) => dll.GetSummary();

    /// <summary>
    /// Decompiles the specified exported function into pseudocode and optionally
    /// reports progress via <paramref name="progress"/>.
    /// </summary>
    public Task<string> GetDecompiledExportAsync(
        LoadedDll dll,
        string name,
        IProgress<string>? progress,
        CancellationToken token)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var export = dll.Pe.FindExport(name);
            if (export.IsForwarder)
            {
                var forwarderText = $"{name} -> {export.ForwarderString}";
                progress?.Report(forwarderText);
                return forwarderText;
            }

            int off = dll.Pe.RvaToOffsetChecked(export.FunctionRva);
            int maxLen = Math.Min(AppConfig.Current.MaxDataSizeBytes, dll.Pe.Data.Length - off);
            var engine = new Engine();
            var code = engine.ToPseudoCode(dll.Pe.Data.AsMemory(off, maxLen), new Engine.Options
            {
                BaseAddress = dll.Pe.ImageBase + export.FunctionRva,
                FunctionName = name
            });
            progress?.Report(code);
            return code;
        }, token);
    }

    /// <summary>Disposes any resources held by the analyzer.</summary>
    public void Dispose()
    {
    }
}
