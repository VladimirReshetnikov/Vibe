using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Vibe.Decompiler;

namespace Vibe.Cui;

public sealed class LoadedDll : IDisposable
{
    internal PeImage Pe { get; }
    public string Md5Hash { get; }
    public string Sha1Hash { get; }
    public string FileHash { get; }
    public CancellationTokenSource Cts { get; } = new();
    internal ModuleDefinition? ManagedModule { get; }
    public bool IsManaged => ManagedModule != null;

    public LoadedDll(string path)
    {
        using var fs = File.OpenRead(path);
        using var md5 = MD5.Create();
        using var sha1 = SHA1.Create();
        using var sha256 = SHA256.Create();
        Md5Hash = Convert.ToHexString(md5.ComputeHash(fs));
        fs.Position = 0;
        Sha1Hash = Convert.ToHexString(sha1.ComputeHash(fs));
        fs.Position = 0;
        FileHash = Convert.ToHexString(sha256.ComputeHash(fs));
        Pe = new PeImage(path);
        if (Pe.HasDotNetMetadata)
            ManagedModule = ModuleDefinition.ReadModule(path);
    }

    public string GetSummary()
    {
        var sb = new StringBuilder();
        sb.Append(Pe.GetSummary());
        sb.AppendLine($"MD5: {Md5Hash}");
        sb.AppendLine($"SHA1: {Sha1Hash}");
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
