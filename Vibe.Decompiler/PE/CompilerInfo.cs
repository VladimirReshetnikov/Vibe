// SPDX-License-Identifier: MIT-0

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using PeNet;

namespace Vibe.Decompiler.PE;

// Detailed heuristics moved to docs/compiler-info.md

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
    /// <param name="StandardLibrary">Associated standard library name.</param>
    /// <param name="Notes">Additional hints discovered during analysis.</param>
    public sealed record Result(string? Compiler, string? Toolset, string? StandardLibrary, string[] Notes)
    {
        public override string ToString()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(Compiler))
                sb.AppendLine($"Compiler: {Compiler}");
            if (!string.IsNullOrEmpty(Toolset))
                sb.AppendLine($"Toolset: {Toolset}");
            if (!string.IsNullOrEmpty(StandardLibrary))
                sb.AppendLine($"Standard Library: {StandardLibrary}");
            if (Notes.Length > 0)
                sb.AppendLine(string.Join(Environment.NewLine, Notes));
            var info = sb.ToString();
            return string.IsNullOrWhiteSpace(info) ? string.Empty : $"/*\n{info}\n*/";
        }
    }

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
            .ToArray() ?? [];

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

        try
        {
            var fileBytes = File.ReadAllBytes(path);
            var text = Encoding.ASCII.GetString(fileBytes);

            if (text.IndexOf("GCC: (GNU)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                compiler ??= "GCC";
                notes.Add("Found GCC version string.");
            }

            if (text.IndexOf("mingw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("_sjlj_init", StringComparison.Ordinal) >= 0)
            {
                compiler = "GCC (MinGW)";
                notes.Add("Detected MinGW-specific runtime markers.");
            }

            if (text.IndexOf("__security_init_cookie", StringComparison.Ordinal) >= 0)
            {
                compiler ??= "MSVC";
                notes.Add("References __security_init_cookie (MSVC /GS).");
            }

            if (text.IndexOf("Microsoft (R) C/C++", StringComparison.Ordinal) >= 0)
            {
                compiler ??= "MSVC";
                notes.Add("Found 'Microsoft (R) C/C++' signature.");
            }
        }
        catch
        {
            // TODO: Log
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
                if (type != "System.Runtime.Versioning.TargetFrameworkAttribute") continue;
                var blob = reader.GetBlobReader(ca.Value);
                if (blob.ReadUInt16() == 0x0001)
                    return blob.ReadSerializedString();
            }
        }
        catch
        {
            // TODO: Log
        }
        return null;
    }

    private static string GetAttributeType(MetadataReader reader, CustomAttribute ca)
    {
        switch (ca.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var mr = reader.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                switch (mr.Parent.Kind)
                {
                    case HandleKind.TypeReference:
                    {
                        var tr = reader.GetTypeReference((TypeReferenceHandle)mr.Parent);
                        return reader.GetString(tr.Namespace) + "." + reader.GetString(tr.Name);
                    }
                    case HandleKind.TypeDefinition:
                    {
                        var td = reader.GetTypeDefinition((TypeDefinitionHandle)mr.Parent);
                        return reader.GetString(td.Namespace) + "." + reader.GetString(td.Name);
                    }
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
            // TODO: Log
        }
        return null;
    }
}

