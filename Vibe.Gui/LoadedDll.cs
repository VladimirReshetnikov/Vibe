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

/// <summary>
/// Represents a DLL loaded by the application and exposes metadata and cryptographic hashes
/// that describe its contents.
/// </summary>
internal sealed class LoadedDll : IDisposable
{
    /// <summary>
    /// Gets the PE reader used to inspect low-level information about the loaded DLL.
    /// </summary>
    internal PeImage Pe { get; }

    /// <summary>
    /// Gets the MD5 hash of the DLL's raw bytes.
    /// </summary>
    internal string Md5Hash { get; }

    /// <summary>
    /// Gets the SHA-1 hash of the DLL's contents.
    /// </summary>
    internal string Sha1Hash { get; }

    /// <summary>
    /// Gets the SHA-256 hash of the DLL's contents.
    /// </summary>
    internal string FileHash { get; }

    /// <summary>
    /// Gets the <see cref="CancellationTokenSource"/> used to cancel asynchronous operations
    /// related to this DLL.
    /// </summary>
    internal CancellationTokenSource Cts { get; } = new();

    /// <summary>
    /// Gets the managed module representation if the DLL contains .NET metadata; otherwise, <c>null</c>.
    /// </summary>
    internal ModuleDefinition? ManagedModule { get; }

    /// <summary>
    /// Gets a value indicating whether the loaded DLL is a managed .NET assembly.
    /// </summary>
    public bool IsManaged => ManagedModule != null;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoadedDll"/> class by loading the specified DLL,
    /// computing its cryptographic hashes and parsing metadata.
    /// </summary>
    /// <param name="path">The path to the DLL on disk.</param>
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
    /// Builds a textual summary of the DLL including PE information and cryptographic hashes.
    /// </summary>
    /// <returns>A string containing details about the loaded DLL.</returns>
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
    /// Asynchronously retrieves the names of the exported functions from the DLL.
    /// </summary>
    /// <param name="token">A cancellation token that can be used to abort the operation.</param>
    /// <returns>A task producing an alphabetically ordered list of export names.</returns>
    public Task<System.Collections.Generic.List<string>> GetExportNamesAsync(CancellationToken token)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return Pe.EnumerateExportNames().OrderBy(n => n).ToList();
        }, token);
    }

    /// <summary>
    /// Releases resources associated with this <see cref="LoadedDll"/> instance.
    /// </summary>
    public void Dispose()
    {
        Cts.Dispose();
        ManagedModule?.Dispose();
    }
}
