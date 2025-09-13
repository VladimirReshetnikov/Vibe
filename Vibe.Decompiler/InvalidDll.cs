// SPDX-License-Identifier: MIT-0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Vibe.Decompiler;

/// <summary>
/// Represents a file that could not be parsed as a PE image but still
/// exposes basic information such as size and cryptographic hashes.
/// </summary>
public sealed class InvalidDll
{
    /// <summary>Full path to the problematic file.</summary>
    public string FilePath { get; }

    /// <summary>Size of the file in bytes.</summary>
    public long FileSize { get; }

    /// <summary>MD5 hash of the file contents.</summary>
    public string Md5Hash { get; }

    /// <summary>SHA-1 hash of the file contents.</summary>
    public string Sha1Hash { get; }

    /// <summary>SHA-256 hash of the file contents.</summary>
    public string Sha256Hash { get; }

    /// <summary>Message describing why parsing failed.</summary>
    public string ErrorMessage { get; }

    /// <summary>
    /// Initializes a new instance for the specified file and failure reason.
    /// </summary>
    public InvalidDll(string path, Exception error)
    {
        FilePath = path;
        ErrorMessage = error.Message;

        using var fs = File.OpenRead(path);
        FileSize = fs.Length;

        using var md5 = MD5.Create();
        Md5Hash = Convert.ToHexString(md5.ComputeHash(fs));
        fs.Position = 0;

        using var sha1 = SHA1.Create();
        Sha1Hash = Convert.ToHexString(sha1.ComputeHash(fs));
        fs.Position = 0;

        using var sha256 = SHA256.Create();
        Sha256Hash = Convert.ToHexString(sha256.ComputeHash(fs));
    }

    /// <summary>
    /// Builds a textual summary including available information and the error
    /// that prevented the file from being parsed.
    /// </summary>
    public string GetSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File: {FilePath}");
        sb.AppendLine($"Size: {FileSize} bytes");
        sb.AppendLine($"MD5: {Md5Hash}");
        sb.AppendLine($"SHA1: {Sha1Hash}");
        sb.AppendLine($"SHA256: {Sha256Hash}");
        sb.AppendLine($"Error: {ErrorMessage}");
        return sb.ToString();
    }
}

