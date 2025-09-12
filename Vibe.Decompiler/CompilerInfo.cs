// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using PeNet;

namespace Vibe.Decompiler;

/// <summary>
/// Extracts a best-effort description of the compiler, toolset and
/// standard library used to build a DLL. The result is based on
/// heuristics and metadata available in the PE file.
/// </summary>
public static class CompilerInfo
{
    /// <summary>
    /// Represents the heuristic analysis result.
    /// </summary>
    /// <param name="Compiler">Vendor or family of the compiler.</param>
    /// <param name="Toolset">Version of the toolset or target framework.</param>
    /// <param name="StandardLibrary">Associated standard library line.</param>
    /// <param name="Notes">Additional hints discovered during analysis.</param>
    public sealed record Result(string? Compiler, string? Toolset, string? StandardLibrary, string[] Notes);

    /// <summary>
    /// Analyzes the specified DLL and returns information about the
    /// probable compiler and standard library.
    /// </summary>
    /// <param name="path">Path to the DLL on disk.</param>
    public static Result Analyze(string path)
    {
        var pe = new PeFile(path);
        var notes = new List<string>();
        var opt = pe.ImageNtHeaders.OptionalHeader;
        var linker = $"{opt.MajorLinkerVersion}.{opt.MinorLinkerVersion:D2}";

        if (pe.ImageComDescriptor is not null)
        {
            var tfm = TryGetTargetFramework(path);
            var stdLib = TryGetDotNetStandardLibrary(path);
            return new Result(
                Compiler: ".NET",
                Toolset: tfm ?? string.Empty,
                StandardLibrary: stdLib,
                Notes: []);
        }

        var imports = pe.ImportedFunctions?
            .Select(f => f?.DLL)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!.ToLowerInvariant())
            .Distinct()
            .ToArray() ?? Array.Empty<string>();

        string? compiler = null;
        string? toolset = linker;
        string? stdlib = null;

        if (imports.Any(n => n is "msvcr100.dll"))
        {
            compiler = "MSVC";
            toolset = "10.0";
            stdlib = "MSVCR100";
        }
        else if (imports.Any(n => n is "msvcr120.dll"))
        {
            compiler = "MSVC";
            toolset = "12.0";
            stdlib = "MSVCR120";
        }
        else if (imports.Any(n => n is "vcruntime140.dll" or "msvcp140.dll" or "ucrtbase.dll" || n.StartsWith("api-ms-win-crt-")))
        {
            compiler = "MSVC";
            toolset = linker;
            stdlib = "UCRT";
            notes.Add("UCRT/vcruntime indicates VS 2015+ toolchain.");
        }
        else if (imports.Any(n => n is "libstdc++-6.dll" or "libgcc_s_seh-1.dll" or "libgcc_s_dw2-1.dll" or "libwinpthread-1.dll"))
        {
            compiler = "GCC (MinGW)";
            stdlib = imports.Contains("libstdc++-6.dll") ? "libstdc++" : null;
        }
        else if (imports.Any(n => n is "borlndmm.dll"))
        {
            compiler = "Borland/Embarcadero";
        }

        var pdb = pe.ImageDebugDirectory?
            .FirstOrDefault(d => d.CvInfoPdb70 != null)?.CvInfoPdb70?.PdbFileName;
        if (!string.IsNullOrWhiteSpace(pdb))
        {
            var m = Regex.Match(pdb, @"MSVC\\Tools\\MSVC\\([0-9.]+)", RegexOptions.IgnoreCase);
            if (m.Success)
                notes.Add($"MSVC toolset {m.Groups[1].Value} from PDB path.");
        }

        return new Result(compiler, toolset, stdlib, [.. notes]);
    }

    private static string? TryGetTargetFramework(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata)
                return null;
            var reader = pe.GetMetadataReader();
            var def = reader.GetAssemblyDefinition();
            foreach (var handle in def.GetCustomAttributes())
            {
                var ca = reader.GetCustomAttribute(handle);
                var type = GetAttributeType(reader, ca);
                if (type == "System.Runtime.Versioning.TargetFrameworkAttribute")
                {
                    var blob = reader.GetBlobReader(ca.Value);
                    if (blob.ReadUInt16() == 0x0001)
                        return blob.ReadSerializedString();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string GetAttributeType(MetadataReader reader, CustomAttribute ca)
    {
        switch (ca.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var mr = reader.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                if (mr.Parent.Kind == HandleKind.TypeReference)
                {
                    var tr = reader.GetTypeReference((TypeReferenceHandle)mr.Parent);
                    return reader.GetString(tr.Namespace) + "." + reader.GetString(tr.Name);
                }
                if (mr.Parent.Kind == HandleKind.TypeDefinition)
                {
                    var td = reader.GetTypeDefinition((TypeDefinitionHandle)mr.Parent);
                    return reader.GetString(td.Namespace) + "." + reader.GetString(td.Name);
                }
                break;
            case HandleKind.MethodDefinition:
                var md = reader.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor);
                var td2 = reader.GetTypeDefinition(md.GetDeclaringType());
                return reader.GetString(td2.Namespace) + "." + reader.GetString(td2.Name);
        }
        return string.Empty;
    }

    private static string? TryGetDotNetStandardLibrary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var peReader = new PEReader(fs);
            if (!peReader.HasMetadata)
                return null;
            var reader = peReader.GetMetadataReader();
            foreach (var handle in reader.AssemblyReferences)
            {
                var ar = reader.GetAssemblyReference(handle);
                var name = reader.GetString(ar.Name);
                if (name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
                    return "mscorlib";
                if (name.Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("System.Runtime", StringComparison.OrdinalIgnoreCase))
                    return "System.Private.CoreLib";
            }
        }
        catch
        {
        }
        return null;
    }
}

