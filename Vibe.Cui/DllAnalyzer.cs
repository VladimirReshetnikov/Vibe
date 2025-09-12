using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Vibe.Decompiler;

namespace Vibe.Cui;

internal sealed class DllAnalyzer : IDisposable
{
    public LoadedDll Load(string path) => new LoadedDll(path);

    public Task<List<string>> GetExportNamesAsync(LoadedDll dll, CancellationToken token)
        => dll.GetExportNamesAsync(token);

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

    public void Dispose()
    {
    }
}
