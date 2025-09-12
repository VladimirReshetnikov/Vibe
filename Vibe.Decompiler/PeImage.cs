// SPDX-License-Identifier: MIT-0

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PeNet;

namespace Vibe.Decompiler;

/// <summary>
/// Lightweight wrapper around the PeNet library exposing the bits of
/// PE metadata the decompiler needs.  Unlike the previous hand rolled
/// reader this supports both PE32 and PE32+ images and tolerates
/// managed assemblies.
/// </summary>
public sealed class PeImage
{
    private readonly PeFile _pe;

    public readonly byte[] Data;
    public readonly ulong ImageBase;
    public readonly List<Section> Sections = new();
    public readonly DataDirectory[] DataDirectories;
    public readonly uint ExportRva;
    public readonly uint ExportSize;
    public readonly uint CliHeaderRva;
    public readonly uint CliHeaderSize;
    public bool HasDotNetMetadata => CliHeaderRva != 0;
    public readonly List<ImportModule> Imports = new();
    public readonly uint SizeOfHeaders;

    // Basic information about the module
    public readonly string FilePath;
    public readonly ushort Machine;
    public readonly uint TimeDateStamp;
    public readonly ushort Characteristics;
    public readonly uint SizeOfImage;
    public readonly ushort Subsystem;
    public readonly ushort DllCharacteristics;
    public readonly ushort MajorImageVersion;
    public readonly ushort MinorImageVersion;
    public readonly ushort MajorSubsystemVersion;
    public readonly ushort MinorSubsystemVersion;

    public PeImage(string path)
    {
        FilePath = path;
        Data = File.ReadAllBytes(path);
        _pe = new PeFile(path);

        Machine = (ushort)_pe.ImageNtHeaders.FileHeader.Machine;
        TimeDateStamp = _pe.ImageNtHeaders.FileHeader.TimeDateStamp;
        Characteristics = (ushort)_pe.ImageNtHeaders.FileHeader.Characteristics;

        var opt = _pe.ImageNtHeaders.OptionalHeader;
        ImageBase = opt.ImageBase;
        SizeOfImage = opt.SizeOfImage;
        SizeOfHeaders = opt.SizeOfHeaders;
        Subsystem = (ushort)opt.Subsystem;
        DllCharacteristics = (ushort)opt.DllCharacteristics;
        MajorImageVersion = opt.MajorImageVersion;
        MinorImageVersion = opt.MinorImageVersion;
        MajorSubsystemVersion = opt.MajorSubsystemVersion;
        MinorSubsystemVersion = opt.MinorSubsystemVersion;

        DataDirectories = opt.DataDirectory
            .Select(dd => new DataDirectory { VirtualAddress = dd.VirtualAddress, Size = dd.Size })
            .ToArray();

        if (DataDirectories.Length > 0)
        {
            ExportRva = DataDirectories[0].VirtualAddress;
            ExportSize = DataDirectories[0].Size;
        }

        if (DataDirectories.Length > 14)
        {
            CliHeaderRva = DataDirectories[14].VirtualAddress;
            CliHeaderSize = DataDirectories[14].Size;
        }

        foreach (var sh in _pe.ImageSectionHeaders)
        {
            Sections.Add(new Section
            {
                Name = sh.Name ?? string.Empty,
                VirtualAddress = sh.VirtualAddress,
                VirtualSize = sh.VirtualSize,
                SizeOfRawData = sh.SizeOfRawData,
                PointerToRawData = sh.PointerToRawData
            });
        }

        if (_pe.ImportedFunctions != null)
        {
            foreach (var group in _pe.ImportedFunctions
                     .GroupBy(f => f.DLL ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                var module = new ImportModule { Name = group.Key };
                foreach (var f in group)
                {
                    if (!string.IsNullOrEmpty(f.Name))
                        module.Symbols.Add(ImportSymbol.FromName(f.Name));
                    else
                        module.Symbols.Add(ImportSymbol.FromOrdinal(f.Hint));
                }
                Imports.Add(module);
            }
        }
    }

    public Section? GetSectionForRva(uint rva)
    {
        foreach (var s in Sections)
        {
            uint vsz = s.VirtualSize != 0 ? s.VirtualSize : s.SizeOfRawData;
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + vsz)
                return s;
        }
        return null;
    }

    public int RvaToOffsetChecked(uint rva)
    {
        if (rva == 0)
            throw new ArgumentOutOfRangeException(nameof(rva));

        if (ExtensionMethods.TryRvaToOffset(rva, _pe.ImageSectionHeaders, out uint off))
        {
            if (off < Data.Length)
                return (int)off;
        }

        if (rva < SizeOfHeaders && rva < Data.Length)
            return (int)rva;

        throw new InvalidOperationException($"RVA 0x{rva:X} not mapped to any section.");
    }

    public ExportInfo FindExport(string name)
    {
        var exp = _pe.ExportedFunctions?
            .FirstOrDefault(f => f.HasName && string.Equals(f.Name, name, StringComparison.Ordinal));
        if (exp == null)
            throw new EntryPointNotFoundException($"Export '{name}' not found in module.");

        if (exp.HasForward && !string.IsNullOrEmpty(exp.ForwardName))
            return ExportInfo.Forwarder(exp.ForwardName);

        return ExportInfo.Direct(exp.Address);
    }

    public IEnumerable<string> EnumerateExportNames()
    {
        if (_pe.ExportedFunctions == null)
            yield break;

        foreach (var f in _pe.ExportedFunctions)
        {
            if (f.HasName && f.Name != null)
                yield return f.Name;
        }
    }

    public string GetSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File: {FilePath}");
        var fi = new FileInfo(FilePath);
        sb.AppendLine($"Size: {fi.Length} bytes");
        sb.AppendLine($"Date modified: {fi.LastWriteTimeUtc:u}");
        var vi = FileVersionInfo.GetVersionInfo(FilePath);
        sb.AppendLine($"File description: {vi.FileDescription}");
        var type = Path.GetExtension(FilePath).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? "DLL File" : "Unknown";
        sb.AppendLine($"Type: {type}");
        sb.AppendLine($"File version: {vi.FileVersion}");
        sb.AppendLine($"Product name: {vi.ProductName}");
        sb.AppendLine($"Product version: {vi.ProductVersion}");
        sb.AppendLine($"Copyright: {vi.LegalCopyright}");
        sb.AppendLine($"Language: {vi.Language}");
        sb.AppendLine($"Original filename: {vi.OriginalFilename}");
        sb.AppendLine($"Machine: 0x{Machine:X4} {MachineToString(Machine)}");
        sb.AppendLine($"TimeDateStamp: 0x{TimeDateStamp:X8} ({DateTimeOffset.FromUnixTimeSeconds(TimeDateStamp).UtcDateTime:u})");
        sb.AppendLine($"Characteristics: 0x{Characteristics:X4} {FormatCharacteristics(Characteristics)}");
        sb.AppendLine($"ImageBase: 0x{ImageBase:X}");
        sb.AppendLine($"ImageVersion: {MajorImageVersion}.{MinorImageVersion}");
        sb.AppendLine($"SubsystemVersion: {MajorSubsystemVersion}.{MinorSubsystemVersion}");
        sb.AppendLine($"Subsystem: {Subsystem} {SubsystemToString(Subsystem)}");
        sb.AppendLine($"DllCharacteristics: 0x{DllCharacteristics:X4}");
        sb.AppendLine($"Number of Sections: {Sections.Count}");
        sb.AppendLine("Sections:");
        foreach (var s in Sections)
        {
            sb.AppendLine($"  {s.Name,-8} RVA 0x{s.VirtualAddress:X8} VSz 0x{s.VirtualSize:X8} File 0x{s.PointerToRawData:X8} Size 0x{s.SizeOfRawData:X8}");
        }
        return sb.ToString();
    }

    private static string MachineToString(ushort m) => m switch
    {
        0x8664 => "x64",
        0x14C => "x86",
        0x1C0 => "ARM",
        0xAA64 => "ARM64",
        _ => "unknown"
    };

    private static string SubsystemToString(ushort s) => s switch
    {
        2 => "Windows GUI",
        3 => "Windows CUI",
        _ => "unknown"
    };

    private static string FormatCharacteristics(ushort c)
    {
        var flags = new List<string>();
        if ((c & 0x0002) != 0) flags.Add("EXECUTABLE");
        if ((c & 0x2000) != 0) flags.Add("DLL");
        if ((c & 0x0020) != 0) flags.Add("LARGE_ADDRESS_AWARE");
        if ((c & 0x0004) != 0) flags.Add("LINE_NUMS_STRIPPED");
        if ((c & 0x0008) != 0) flags.Add("LOCAL_SYMS_STRIPPED");
        if ((c & 0x0100) != 0) flags.Add("32BIT");
        return string.Join(", ", flags);
    }

    public readonly struct DataDirectory
    {
        public uint VirtualAddress { get; init; }
        public uint Size { get; init; }
    }

    public readonly struct Section
    {
        public string Name { get; init; }
        public uint VirtualAddress { get; init; }
        public uint VirtualSize { get; init; }
        public uint SizeOfRawData { get; init; }
        public uint PointerToRawData { get; init; }
    }

    public sealed class ImportModule
    {
        public string Name { get; init; } = string.Empty;
        public List<ImportSymbol> Symbols { get; } = new();
    }

    public readonly struct ImportSymbol
    {
        public string? Name { get; }
        public ushort Ordinal { get; }
        public bool IsOrdinal => Name == null;

        private ImportSymbol(string? name, ushort ord)
        {
            Name = name;
            Ordinal = ord;
        }

        public static ImportSymbol FromName(string name) => new(name, 0);
        public static ImportSymbol FromOrdinal(ushort ord) => new(null, ord);
    }

    public readonly struct ExportInfo
    {
        public bool IsForwarder { get; }
        public uint FunctionRva { get; }
        public string ForwarderString { get; }

        private ExportInfo(bool fwd, uint rva, string s)
        {
            IsForwarder = fwd; FunctionRva = rva; ForwarderString = s;
        }

        public static ExportInfo Forwarder(string s) => new(true, 0, s);
        public static ExportInfo Direct(uint rva) => new(false, rva, "");
    }
}

