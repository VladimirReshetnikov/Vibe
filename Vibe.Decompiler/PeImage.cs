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

    /// <summary>
    /// Raw bytes of the PE file as read from disk.
    /// </summary>
    public readonly byte[] Data;

    /// <summary>
    /// Preferred load address of the image as specified in the optional header.
    /// </summary>
    public readonly ulong ImageBase;

    /// <summary>
    /// Collection of section headers describing the layout of the image.
    /// </summary>
    public readonly List<Section> Sections = new();

    /// <summary>
    /// Entries from the PE data directory table.
    /// </summary>
    public readonly DataDirectory[] DataDirectories;

    /// <summary>
    /// Relative virtual address of the export directory if present.
    /// </summary>
    public readonly uint ExportRva;

    /// <summary>
    /// Size in bytes of the export directory.
    /// </summary>
    public readonly uint ExportSize;

    /// <summary>
    /// Relative virtual address of the CLI header when the image is a managed assembly.
    /// </summary>
    public readonly uint CliHeaderRva;

    /// <summary>
    /// Size in bytes of the CLI header.
    /// </summary>
    public readonly uint CliHeaderSize;

    /// <summary>
    /// Gets a value indicating whether the image contains a CLI header and therefore .NET metadata.
    /// </summary>
    public bool HasDotNetMetadata => CliHeaderRva != 0;

    /// <summary>
    /// Imported modules and symbols referenced by this image.
    /// </summary>
    public readonly List<ImportModule> Imports = new();

    /// <summary>
    /// Combined size of all headers in the file.
    /// </summary>
    public readonly uint SizeOfHeaders;

    // Basic information about the module

    /// <summary>
    /// Full path to the file on disk from which the image was loaded.
    /// </summary>
    public readonly string FilePath;

    /// <summary>
    /// Machine architecture identifier from the file header.
    /// </summary>
    public readonly ushort Machine;

    /// <summary>
    /// Time-stamp of when the image was built, expressed as seconds since the Unix epoch.
    /// </summary>
    public readonly uint TimeDateStamp;

    /// <summary>
    /// Characteristics flags from the file header.
    /// </summary>
    public readonly ushort Characteristics;

    /// <summary>
    /// Total size of the image in memory when loaded by the operating system.
    /// </summary>
    public readonly uint SizeOfImage;

    /// <summary>
    /// Subsystem required to run this image.
    /// </summary>
    public readonly ushort Subsystem;

    /// <summary>
    /// DLL characteristics flags from the optional header.
    /// </summary>
    public readonly ushort DllCharacteristics;

    /// <summary>
    /// Major part of the image version number.
    /// </summary>
    public readonly ushort MajorImageVersion;

    /// <summary>
    /// Minor part of the image version number.
    /// </summary>
    public readonly ushort MinorImageVersion;

    /// <summary>
    /// Major subsystem version required by the image.
    /// </summary>
    public readonly ushort MajorSubsystemVersion;

    /// <summary>
    /// Minor subsystem version required by the image.
    /// </summary>
    public readonly ushort MinorSubsystemVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeImage"/> class by loading the
    /// specified PE file and extracting relevant metadata.
    /// </summary>
    /// <param name="path">Path to the PE file on disk.</param>
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

    /// <summary>
    /// Finds the section that contains a given relative virtual address (RVA).
    /// </summary>
    /// <param name="rva">The RVA to look up.</param>
    /// <returns>The section that encloses the RVA, or <c>null</c> if none matches.</returns>
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

    /// <summary>
    /// Converts a relative virtual address to a file offset, validating that the result falls within the file.
    /// </summary>
    /// <param name="rva">The RVA to translate.</param>
    /// <returns>The file offset corresponding to the RVA.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="rva"/> is zero.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the RVA does not map to any section.</exception>
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

    /// <summary>
    /// Locates an exported function by name and determines whether it is forwarded or implemented directly.
    /// </summary>
    /// <param name="name">The exact export name to search for.</param>
    /// <returns>Information describing the export.</returns>
    /// <exception cref="EntryPointNotFoundException">Thrown when the export is not found.</exception>
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

    /// <summary>
    /// Enumerates the names of exported functions defined by the image.
    /// </summary>
    /// <returns>A sequence of export names. Exports without names are skipped.</returns>
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

    /// <summary>
    /// Produces a multi-line textual summary of the image including version and section information.
    /// </summary>
    /// <returns>A formatted string describing the image.</returns>
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

    /// <summary>
    /// Converts a machine type value to a human-readable string.
    /// </summary>
    private static string MachineToString(ushort m) => m switch
    {
        0x8664 => "x64",
        0x14C => "x86",
        0x1C0 => "ARM",
        0xAA64 => "ARM64",
        _ => "unknown"
    };

    /// <summary>
    /// Converts a subsystem value to a descriptive string.
    /// </summary>
    private static string SubsystemToString(ushort s) => s switch
    {
        2 => "Windows GUI",
        3 => "Windows CUI",
        _ => "unknown"
    };

    /// <summary>
    /// Formats the characteristics bit field into a comma separated list of flags.
    /// </summary>
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

    /// <summary>
    /// Represents an entry in the PE data directory table.
    /// </summary>
    public readonly struct DataDirectory
    {
        /// <summary>
        /// Gets the relative virtual address of the referenced data.
        /// </summary>
        public uint VirtualAddress { get; init; }

        /// <summary>
        /// Gets the size in bytes of the referenced data.
        /// </summary>
        public uint Size { get; init; }
    }

    /// <summary>
    /// Describes a single section header in the image.
    /// </summary>
    public readonly struct Section
    {
        /// <summary>
        /// Gets the section name, padded or truncated to eight characters.
        /// </summary>
        public string Name { get; init; }

        /// <summary>
        /// Gets the relative virtual address where the section is loaded.
        /// </summary>
        public uint VirtualAddress { get; init; }

        /// <summary>
        /// Gets the size of the section when loaded into memory.
        /// </summary>
        public uint VirtualSize { get; init; }

        /// <summary>
        /// Gets the size of the section's raw data on disk.
        /// </summary>
        public uint SizeOfRawData { get; init; }

        /// <summary>
        /// Gets the file offset to the beginning of the section's raw data.
        /// </summary>
        public uint PointerToRawData { get; init; }
    }

    /// <summary>
    /// Represents a module from which symbols are imported.
    /// </summary>
    public sealed class ImportModule
    {
        /// <summary>
        /// Gets the module name without path information.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Gets the list of symbols imported from the module.
        /// </summary>
        public List<ImportSymbol> Symbols { get; } = new();
    }

    /// <summary>
    /// Represents a single imported symbol, either by name or by ordinal.
    /// </summary>
    public readonly struct ImportSymbol
    {
        /// <summary>
        /// Gets the symbol name when imported by name; otherwise <c>null</c>.
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// Gets the ordinal value when imported by ordinal.
        /// </summary>
        public ushort Ordinal { get; }

        /// <summary>
        /// Gets a value indicating whether the import is by ordinal.
        /// </summary>
        public bool IsOrdinal => Name == null;

        private ImportSymbol(string? name, ushort ord)
        {
            Name = name;
            Ordinal = ord;
        }

        /// <summary>
        /// Creates an <see cref="ImportSymbol"/> representing a named import.
        /// </summary>
        public static ImportSymbol FromName(string name) => new(name, 0);

        /// <summary>
        /// Creates an <see cref="ImportSymbol"/> representing an import by ordinal.
        /// </summary>
        public static ImportSymbol FromOrdinal(ushort ord) => new(null, ord);
    }

    /// <summary>
    /// Describes an exported function and whether it is forwarded to another module.
    /// </summary>
    public readonly struct ExportInfo
    {
        /// <summary>
        /// Gets a value indicating whether the export is forwarded to another module.
        /// </summary>
        public bool IsForwarder { get; }

        /// <summary>
        /// Gets the function RVA when the export is not forwarded.
        /// </summary>
        public uint FunctionRva { get; }

        /// <summary>
        /// Gets the raw forwarder string when the export is forwarded.
        /// </summary>
        public string ForwarderString { get; }

        private ExportInfo(bool fwd, uint rva, string s)
        {
            IsForwarder = fwd; FunctionRva = rva; ForwarderString = s;
        }

        /// <summary>
        /// Creates an <see cref="ExportInfo"/> representing a forwarder entry.
        /// </summary>
        /// <param name="s">The forwarder string describing the target module and symbol.</param>
        public static ExportInfo Forwarder(string s) => new(true, 0, s);

        /// <summary>
        /// Creates an <see cref="ExportInfo"/> representing a direct export.
        /// </summary>
        /// <param name="rva">The relative virtual address of the exported function.</param>
        public static ExportInfo Direct(uint rva) => new(false, rva, string.Empty);
    }
}

