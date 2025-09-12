using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Vibe.Decompiler;

namespace Vibe.Gui;

internal sealed class LoadedDll : IDisposable
{
    internal PEReaderLite Pe { get; }
    internal string Md5Hash { get; }
    internal string Sha2Hash { get; }
    internal string FileHash { get; }
    internal CancellationTokenSource Cts { get; } = new();
    internal ModuleDefinition? ManagedModule { get; }
    public bool IsManaged => ManagedModule != null;

    public LoadedDll(string path)
    {
        using var fs = File.OpenRead(path);
        using var md5 = MD5.Create();
        using var sha512 = SHA512.Create();
        using var sha256 = SHA256.Create();
        Md5Hash = Convert.ToHexString(md5.ComputeHash(fs));
        fs.Position = 0;
        Sha2Hash = Convert.ToHexString(sha512.ComputeHash(fs));
        fs.Position = 0;
        FileHash = Convert.ToHexString(sha256.ComputeHash(fs));
        Pe = new PEReaderLite(path);
        if (Pe.HasDotNetMetadata)
            ManagedModule = ModuleDefinition.ReadModule(path);
    }

    public string GetSummary()
    {
        var sb = new StringBuilder();
        sb.Append(Pe.GetSummary());
        sb.AppendLine($"MD5: {Md5Hash}");
        sb.AppendLine($"SHA2: {Sha2Hash}");
        sb.AppendLine($"SHA256: {FileHash}");
        return sb.ToString();
    }

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
