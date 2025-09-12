// SPDX-License-Identifier: MIT-0

using System.Text;
using System.Diagnostics;
using System.IO;
using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Vibe.Decompiler;

/// <summary>
/// Lightweight reader for Portable Executable (PE) files that extracts structural
/// information, imports, exports and basic metadata without the overhead of full
/// metadata libraries.
/// </summary>
public sealed class PEReaderLite
{
    /// <summary>
    /// Raw bytes of the loaded PE file.
    /// </summary>
    public readonly byte[] Data;

    /// <summary>
    /// Preferred load address encoded in the PE headers.
    /// </summary>
    public readonly ulong ImageBase;

    /// <summary>
    /// Collection of section headers describing the layout of the image.
    /// </summary>
    public readonly List<Section> Sections = new();

    /// <summary>
    /// Array of data directory entries as defined by the PE specification.
    /// </summary>
    public readonly DataDirectory[] DataDirectories;

    /// <summary>
    /// Relative virtual address of the export directory, if present.
    /// </summary>
    public readonly uint ExportRva;

    /// <summary>
    /// Size of the export directory in bytes.
    /// </summary>
    public readonly uint ExportSize;

    /// <summary>
    /// Relative virtual address of the CLI header for managed assemblies.
    /// </summary>
    public readonly uint CliHeaderRva;

    /// <summary>
    /// Size of the CLI header structure.
    /// </summary>
    public readonly uint CliHeaderSize;

    public readonly Guid? Mvid;

    /// <summary>
    /// Gets a value indicating whether the PE file contains .NET metadata.
    /// </summary>
    public bool HasDotNetMetadata => CliHeaderRva != 0;

    /// <summary>
    /// List of modules and symbols imported by the image.
    /// </summary>
    public readonly List<ImportModule> Imports = new();

    /// <summary>
    /// Total size of the PE headers, used for mapping RVAs that fall within header ranges.
    /// </summary>
    public readonly uint SizeOfHeaders;

    /// <summary>
    /// Indicates whether the image uses the PE32+ (64-bit) format.
    /// </summary>
    public readonly bool IsPe32Plus;

    // Basic information about the module
    /// <summary>
    /// File system path of the loaded image.
    /// </summary>
    public readonly string FilePath;

    /// <summary>
    /// The machine type for which the image was built.
    /// </summary>
    public readonly ushort Machine;

    /// <summary>
    /// COFF timestamp representing when the image was linked.
    /// </summary>
    public readonly uint TimeDateStamp;

    /// <summary>
    /// COFF characteristics flags.
    /// </summary>
    public readonly ushort Characteristics;

    /// <summary>
    /// Total size of the loaded image in memory.
    /// </summary>
    public readonly uint SizeOfImage;

    /// <summary>
    /// Subsystem the executable expects to run under.
    /// </summary>
    public readonly ushort Subsystem;

    /// <summary>
    /// Additional DLL characteristics flags.
    /// </summary>
    public readonly ushort DllCharacteristics;

    /// <summary>
    /// Major version of the image as specified in the optional header.
    /// </summary>
    public readonly ushort MajorImageVersion;

    /// <summary>
    /// Minor version of the image as specified in the optional header.
    /// </summary>
    public readonly ushort MinorImageVersion;

    /// <summary>
    /// Major version of the subsystem required to run the image.
    /// </summary>
    public readonly ushort MajorSubsystemVersion;

    /// <summary>
    /// Minor version of the subsystem required to run the image.
    /// </summary>
    public readonly ushort MinorSubsystemVersion;

    /// <summary>
    /// Initializes a new instance of <see cref="PEReaderLite"/> by loading and parsing the
    /// specified PE file.
    /// </summary>
    /// <param name="path">Path to the PE file on disk.</param>
    public PEReaderLite(string path)
    {
        FilePath = path;
        Data = File.ReadAllBytes(path);
        // DOS header
        if (U16(0) != 0x5A4D) // "MZ"
            throw new BadImageFormatException("Not an MZ image.");
        int peOff = (int)U32(0x3C);
        if (peOff <= 0 || peOff >= Data.Length - 4)
            throw new BadImageFormatException("e_lfanew invalid.");
        if (U32(peOff) != 0x00004550) // "PE\0\0"
            throw new BadImageFormatException("PE signature missing.");

        int coffOff = peOff + 4;
        Machine = U16(coffOff);
        ushort numSections = U16(coffOff + 2);
        TimeDateStamp = U32(coffOff + 4);
        Characteristics = U16(coffOff + 18);
        ushort optHeaderSize = U16(coffOff + 16);
        int optOff = coffOff + 20;

        // Optional header: support PE32 and PE32+
        ushort magic = U16(optOff);
        bool isPe32Plus;
        if (magic == 0x20B) // PE32+
        {
            isPe32Plus = true;
        }
        else if (magic == 0x10B) // PE32
        {
            isPe32Plus = false;
        }
        else
        {
            throw new NotSupportedException($"Unsupported PE magic 0x{magic:X}");
        }

        IsPe32Plus = isPe32Plus;

        ImageBase = isPe32Plus ? U64(optOff + 24) : U32(optOff + 28);
        MajorImageVersion = U16(optOff + 44);
        MinorImageVersion = U16(optOff + 46);
        MajorSubsystemVersion = U16(optOff + 48);
        MinorSubsystemVersion = U16(optOff + 50);
        SizeOfImage = U32(optOff + 56);
        uint sizeOfHeaders = U32(optOff + 60);
        SizeOfHeaders = sizeOfHeaders; // capture SizeOfHeaders from optional header
        Subsystem = U16(optOff + 68);
        DllCharacteristics = U16(optOff + 70);
        uint numberOfRvaAndSizes = U32(optOff + (isPe32Plus ? 108 : 92));
        int dataDirOff = optOff + (isPe32Plus ? 112 : 96);

        if (numberOfRvaAndSizes < 1)
            throw new BadImageFormatException("No data directories.");

        int dirCount = (int)Math.Min(numberOfRvaAndSizes, 16);

        // Ensure we don't read beyond the optional header bounds
        int dirStart = isPe32Plus ? 112 : 96;
        int maxDirCount = (optHeaderSize - dirStart) / 8;
        dirCount = Math.Min(dirCount, maxDirCount);

        DataDirectories = new DataDirectory[dirCount];
        for (int i = 0; i < dirCount; i++)
        {
            uint rva = U32(dataDirOff + i * 8 + 0);
            uint size = U32(dataDirOff + i * 8 + 4);
            DataDirectories[i] = new DataDirectory { VirtualAddress = rva, Size = size };
        }

        if (dirCount > 0)
        {
            ExportRva = DataDirectories[0].VirtualAddress;
            ExportSize = DataDirectories[0].Size;
        }

        uint importRva = dirCount > 1 ? DataDirectories[1].VirtualAddress : 0;

        if (dirCount > 14)
        {
            CliHeaderRva = DataDirectories[14].VirtualAddress;
            CliHeaderSize = DataDirectories[14].Size;
        }

        // Sections
        int secOff = optOff + optHeaderSize;
        for (int i = 0; i < numSections; i++)
        {
            int off = secOff + i * 40; // IMAGE_SECTION_HEADER
            string name = ReadAsciiFixed(off, 8);
            uint virtualSize = U32(off + 8);
            uint virtualAddress = U32(off + 12);
            uint sizeOfRawData = U32(off + 16);
            uint ptrToRawData = U32(off + 20);

            Sections.Add(new Section
            {
                Name = name,
                VirtualAddress = virtualAddress,
                VirtualSize = virtualSize,
                SizeOfRawData = sizeOfRawData,
                PointerToRawData = ptrToRawData
            });
        }

        if (importRva != 0)
            ParseImports(importRva);

        if (HasDotNetMetadata)
        {
            try
            {
                using var ms = new MemoryStream(Data, writable: false);
                using var peReader = new PEReader(ms);
                var mdReader = peReader.GetMetadataReader();
                var moduleDef = mdReader.GetModuleDefinition();
                Mvid = mdReader.GetGuid(moduleDef.Mvid);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Finds the section that contains the specified relative virtual address (RVA).
    /// </summary>
    /// <param name="rva">The RVA to locate.</param>
    /// <returns>The section containing the RVA, or <c>null</c> if none match.</returns>
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
    /// Converts an RVA into a file offset, validating that it maps to a known region
    /// of the image. RVAs that point into the header range are also supported.
    /// </summary>
    /// <param name="rva">The relative virtual address to translate.</param>
    /// <returns>The corresponding file offset.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="rva"/> is zero.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the RVA cannot be mapped.</exception>
    public int RvaToOffsetChecked(uint rva)
    {
        if (rva == 0)
            throw new ArgumentOutOfRangeException(nameof(rva));
        var s = GetSectionForRva(rva);
        if (s == null)
        {
            // Allow RVAs that point into headers (e.g., import/export arrays or strings)
            if (rva < SizeOfHeaders)
                return (int)rva;
            throw new InvalidOperationException($"RVA 0x{rva:X} not mapped to any section.");
        }
        uint delta = rva - s.Value.VirtualAddress;
        uint off = s.Value.PointerToRawData + delta;
        if (off >= Data.Length)
            throw new InvalidOperationException("RVA->file offset out of range.");
        return (int)off;
    }

    /// <summary>
    /// Locates an exported function by name and returns information about it.
    /// </summary>
    /// <param name="name">Name of the export to search for.</param>
    /// <returns>Details of the export, including RVA or forwarder string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the module has no export directory.</exception>
    public ExportInfo FindExport(string name)
    {
        if (ExportRva == 0 || ExportSize == 0)
            throw new InvalidOperationException("Module has no export directory.");

        int expDirOff = RvaToOffsetChecked(ExportRva);

        uint NumberOfFunctions = U32(expDirOff + 0x14);
        uint NumberOfNames = U32(expDirOff + 0x18);
        uint AddressOfFunctions = U32(expDirOff + 0x1C);
        uint AddressOfNames = U32(expDirOff + 0x20);
        uint AddressOfNameOrds = U32(expDirOff + 0x24);

        int namesOff = RvaToOffsetChecked(AddressOfNames);
        int ordsOff = RvaToOffsetChecked(AddressOfNameOrds);
        int funcsOff = RvaToOffsetChecked(AddressOfFunctions);

        for (uint i = 0; i < NumberOfNames; i++)
        {
            uint nameRva = U32(namesOff + (int)(i * 4));
            int nameOff = RvaToOffsetChecked(nameRva);
            string nm = ReadAsciiZ(nameOff);

            if (string.Equals(nm, name, StringComparison.Ordinal))
            {
                ushort ordIndex = U16(ordsOff + (int)(i * 2)); // index into functions[]
                if (ordIndex >= NumberOfFunctions)
                    throw new BadImageFormatException("Export ordinal index out of range.");
                uint funcRva = U32(funcsOff + ordIndex * 4);

                // Forwarder check: if funcRva falls inside the export directory range,
                // it points to a forwarder string (RVA to ASCII "DLL.Name").
                bool isForwarder = funcRva >= ExportRva && funcRva < (ExportRva + ExportSize);

                if (isForwarder)
                {
                    int fwdOff = RvaToOffsetChecked(funcRva);
                    string fwdStr = ReadAsciiZ(fwdOff);
                    return ExportInfo.Forwarder(fwdStr);
                }

                return ExportInfo.Direct(funcRva);
            }
        }

        throw new EntryPointNotFoundException($"Export '{name}' not found in module.");
    }

    private void ParseImports(uint importRva)
    {
        int descOff = RvaToOffsetChecked(importRva);
        int descCount = 0;
        while (true)
        {
            if (descCount++ >= 1000)
                throw new BadImageFormatException("Too many import descriptors.");

            // Check bounds before reading import descriptor (20 bytes)
            if (descOff + 20 > Data.Length)
                break;

            uint originalFirstThunk = U32(descOff + 0);
            uint timeDateStamp = U32(descOff + 4);
            uint forwarderChain = U32(descOff + 8);
            uint nameRva = U32(descOff + 12);
            uint firstThunk = U32(descOff + 16);

            if (originalFirstThunk == 0 && timeDateStamp == 0 &&
                forwarderChain == 0 && nameRva == 0 && firstThunk == 0)
                break;

            if (nameRva == 0)
            {
                descOff += 20;
                continue; // Skip malformed import descriptor
            }

            string moduleName = ReadAsciiZ(RvaToOffsetChecked(nameRva));
            uint thunkRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;

            if (thunkRva == 0)
            {
                descOff += 20;
                continue; // Skip import descriptor with no thunks
            }

            int thunkOff = RvaToOffsetChecked(thunkRva);

            var module = new ImportModule { Name = moduleName };

            int symbolCount = 0;
            int entrySize = IsPe32Plus ? 8 : 4;
            while (true)
            {
                if (symbolCount++ >= 10000)
                    throw new BadImageFormatException("Too many import symbols in module.");

                // Check bounds before reading thunk entry
                if (thunkOff + entrySize > Data.Length)
                    break;

                ulong entry = IsPe32Plus ? U64(thunkOff) : U32(thunkOff);
                if (entry == 0)
                    break;

                bool byOrdinal = IsPe32Plus ?
                    (entry & 0x8000000000000000UL) != 0 :
                    (entry & 0x80000000) != 0;

                if (byOrdinal)
                {
                    ushort ord = (ushort)(entry & 0xFFFF);
                    module.Symbols.Add(ImportSymbol.FromOrdinal(ord));
                }
                else
                {
                    uint hintNameRva = (uint)entry;
                    if (hintNameRva == 0)
                        break;
                    int hnOff = RvaToOffsetChecked(hintNameRva);
                    if (hnOff + 2 >= Data.Length)
                        break;
                    string funcName = ReadAsciiZ(hnOff + 2); // skip hint
                    module.Symbols.Add(ImportSymbol.FromName(funcName));
                }

                thunkOff += entrySize;
            }

            Imports.Add(module);
            descOff += 20;
        }
    }

    /// <summary>
    /// Enumerates the names of all exported functions from the image in the order
    /// they appear in the export table.
    /// </summary>
    /// <returns>An enumerable sequence of export names.</returns>
    public IEnumerable<string> EnumerateExportNames()
    {
        if (ExportRva == 0 || ExportSize == 0)
            yield break;

        int expDirOff = RvaToOffsetChecked(ExportRva);

        uint NumberOfNames = U32(expDirOff + 0x18);
        uint AddressOfNames = U32(expDirOff + 0x20);

        int namesOff = RvaToOffsetChecked(AddressOfNames);

        for (uint i = 0; i < NumberOfNames; i++)
        {
            uint nameRva = U32(namesOff + (int)(i * 4));
            int nameOff = RvaToOffsetChecked(nameRva);
            yield return ReadAsciiZ(nameOff);
        }
    }

    /// <summary>
    /// Builds a human-readable summary of the PE file including version information
    /// and section layout details.
    /// </summary>
    /// <returns>A string containing descriptive information about the image.</returns>
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
        if (HasDotNetMetadata && Mvid.HasValue)
            sb.AppendLine($"MVID: {Mvid}");
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

    // ------------------ local data helpers ------------------

    private ushort U16(int off) =>
        (ushort)(Data[off] | (Data[off + 1] << 8));

    private uint U32(int off) =>
        (uint)(Data[off] |
               (Data[off + 1] << 8) |
               (Data[off + 2] << 16) |
               (Data[off + 3] << 24));

    private ulong U64(int off) =>
        (ulong)U32(off) | ((ulong)U32(off + 4) << 32);

    private string ReadAsciiZ(int off)
    {
        int i = off;
        while (i < Data.Length && Data[i] != 0) i++;
        return Encoding.ASCII.GetString(Data, off, i - off);
    }

    private string ReadAsciiFixed(int off, int len)
    {
        int end = off + len;
        int i = off;
        while (i < end && Data[i] != 0) i++;
        return Encoding.ASCII.GetString(Data, off, i - off);
    }

    /// <summary>
    /// Represents a single entry in the PE data directory table.
    /// </summary>
    public readonly struct DataDirectory
    {
        /// <summary>
        /// RVA of the table or data structure referenced by the directory entry.
        /// </summary>
        public uint VirtualAddress { get; init; }

        /// <summary>
        /// Size in bytes of the referenced table or data structure.
        /// </summary>
        public uint Size { get; init; }
    }

    /// <summary>
    /// Describes a section within the PE image.
    /// </summary>
    public readonly struct Section
    {
        /// <summary>
        /// Name of the section as stored in the header.
        /// </summary>
        public string Name { get; init; }

        /// <summary>
        /// RVA at which the section is loaded in memory.
        /// </summary>
        public uint VirtualAddress { get; init; }

        /// <summary>
        /// Actual size of the section's data in memory.
        /// </summary>
        public uint VirtualSize { get; init; }

        /// <summary>
        /// Size of the section's data stored in the file.
        /// </summary>
        public uint SizeOfRawData { get; init; }

        /// <summary>
        /// File offset to the section's raw data.
        /// </summary>
        public uint PointerToRawData { get; init; }
    }

    /// <summary>
    /// Represents a module imported by the image along with its symbols.
    /// </summary>
    public sealed class ImportModule
    {
        /// <summary>
        /// Name of the imported module.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Symbols imported from the module.
        /// </summary>
        public List<ImportSymbol> Symbols { get; } = new();
    }

    /// <summary>
    /// Describes a single imported symbol which may be identified by name or ordinal.
    /// </summary>
    public readonly struct ImportSymbol
    {
        /// <summary>
        /// Name of the symbol, or <c>null</c> if imported by ordinal.
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// Ordinal value used when the symbol is imported by ordinal.
        /// </summary>
        public ushort Ordinal { get; }

        /// <summary>
        /// Gets a value indicating whether the symbol is imported by ordinal rather than by name.
        /// </summary>
        public bool IsOrdinal => Name == null;

        private ImportSymbol(string? name, ushort ord)
        {
            Name = name;
            Ordinal = ord;
        }

        /// <summary>
        /// Creates an <see cref="ImportSymbol"/> representing an import by name.
        /// </summary>
        public static ImportSymbol FromName(string name) => new(name, 0);

        /// <summary>
        /// Creates an <see cref="ImportSymbol"/> representing an import by ordinal.
        /// </summary>
        public static ImportSymbol FromOrdinal(ushort ord) => new(null, ord);
    }

    /// <summary>
    /// Provides information about an exported function.
    /// </summary>
    public readonly struct ExportInfo
    {
        /// <summary>
        /// Gets a value indicating whether the export is a forwarder to another module.
        /// </summary>
        public bool IsForwarder { get; }

        /// <summary>
        /// RVA of the exported function if it is not a forwarder.
        /// </summary>
        public uint FunctionRva { get; }

        /// <summary>
        /// Forwarder string in the form "module.func" when <see cref="IsForwarder"/> is <c>true</c>.
        /// </summary>
        public string ForwarderString { get; }

        private ExportInfo(bool fwd, uint rva, string s)
        {
            IsForwarder = fwd;
            FunctionRva = rva;
            ForwarderString = s;
        }

        /// <summary>
        /// Creates an <see cref="ExportInfo"/> representing a forwarder to another module.
        /// </summary>
        public static ExportInfo Forwarder(string s) => new(true, 0, s);

        /// <summary>
        /// Creates an <see cref="ExportInfo"/> representing a direct export at the specified RVA.
        /// </summary>
        public static ExportInfo Direct(uint rva) => new(false, rva, "");
    }
}
