using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;
using Vibe.Decompiler;
using Vibe.Decompiler.PE;

namespace Vibe.Cui;

/// <summary>
/// Represents a DLL loaded from disk along with various metadata and hash values
/// that describe its contents. The class also exposes convenience methods for
/// enumerating exports and summarising the image.
/// </summary>
public sealed class LoadedDll : IDisposable
{
    /// <summary>PE parser used to inspect the image.</summary>
    internal PeImage Pe { get; }

    /// <summary>MD5 hash of the DLL file.</summary>
    public string Md5Hash { get; }

    /// <summary>SHA-1 hash of the DLL file.</summary>
    public string Sha1Hash { get; }

    /// <summary>SHA-256 hash of the DLL file.</summary>
    public string FileHash { get; }

    /// <summary>Cancellation token for long running operations initiated on this DLL.</summary>
    public CancellationTokenSource Cts { get; } = new();

    internal ModuleDefinition? ManagedModule { get; }

    /// <summary>Gets a value indicating whether the DLL contains .NET metadata.</summary>
    public bool IsManaged => ManagedModule != null;

    /// <summary>
    /// Loads a DLL from disk and computes cryptographic hashes for its contents.
    /// </summary>
    /// <param name="path">Path to the DLL.</param>
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

    /// <summary>
    /// Builds a textual summary of the DLL including PE header information and hashes.
    /// </summary>
    public string GetSummary()
    {
        var sb = new StringBuilder();
        sb.Append(Pe.GetSummary());
        sb.AppendLine($"MD5: {Md5Hash}");
        sb.AppendLine($"SHA1: {Sha1Hash}");
        sb.AppendLine($"SHA256: {FileHash}");
        return sb.ToString();
    }

    /// <summary>
    /// Asynchronously enumerates the names of exported functions defined in the DLL.
    /// </summary>
    public Task<System.Collections.Generic.List<string>> GetExportNamesAsync(CancellationToken token)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return Pe.EnumerateExportNames().OrderBy(n => n).ToList();
        }, token);
    }

    /// <summary>Releases resources held by this instance.</summary>
    public void Dispose()
    {
        Cts.Dispose();
        ManagedModule?.Dispose();
    }
}
