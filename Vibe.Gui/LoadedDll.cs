using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Vibe.Decompiler;

namespace Vibe.Gui;

internal sealed class LoadedDll : IDisposable
{
    internal PEReaderLite Pe { get; }
    internal string FileHash { get; }
    internal CancellationTokenSource Cts { get; } = new();
    internal ModuleDefinition? ManagedModule { get; }
    public bool IsManaged => ManagedModule != null;

    public LoadedDll(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        FileHash = Convert.ToHexString(sha.ComputeHash(fs));
        Pe = new PEReaderLite(path);
        if (Pe.HasDotNetMetadata)
            ManagedModule = ModuleDefinition.ReadModule(path);
    }

    public string GetSummary() => Pe.GetSummary();

    public Task<System.Collections.Generic.List<string>> GetExportNamesAsync(CancellationToken token)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return Pe.EnumerateExportNames().OrderBy(n => n).ToList();
        }, token);
    }

    public void Dispose()
    {
        Cts.Dispose();
        ManagedModule?.Dispose();
    }
}
