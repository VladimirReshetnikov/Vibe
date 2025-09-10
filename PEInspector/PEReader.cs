using System.Text;

public sealed class PEReader
{
    public readonly byte[] Data;
    public readonly ulong ImageBase;
    public readonly List<Section> Sections = new();
    public readonly uint ExportRva;
    public readonly uint ExportSize;

    public PEReader(string path)
    {
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
        ushort numSections = U16(coffOff + 2);
        ushort optHeaderSize = U16(coffOff + 16);
        int optOff = coffOff + 20;

        // Optional header (we expect PE32+)
        ushort magic = U16(optOff);
        if (magic != 0x20B)
            throw new NotSupportedException($"Not PE32+ (x64). Magic=0x{magic:X}");

        ImageBase = U64(optOff + 24);
        uint sizeOfHeaders = U32(optOff + 60);
        uint numberOfRvaAndSizes = U32(optOff + 108);
        int dataDirOff = optOff + 112;

        if (numberOfRvaAndSizes < 1)
            throw new BadImageFormatException("No data directories.");

        ExportRva = U32(dataDirOff + 0 * 8 + 0);
        ExportSize = U32(dataDirOff + 0 * 8 + 4);

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
        var s = GetSectionForRva(rva);
        if (s == null)
        {
            // Could be in the headers
            if (rva < U32(0x3C)) // extremely small
                return (int)rva;
            throw new InvalidOperationException($"RVA 0x{rva:X} not mapped to any section.");
        }
        uint delta = rva - s.Value.VirtualAddress;
        uint off = s.Value.PointerToRawData + delta;
        if (off >= Data.Length)
            throw new InvalidOperationException("RVA->file offset out of range.");
        return (int)off;
    }

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

    public readonly struct Section
    {
        public string Name { get; init; }
        public uint VirtualAddress { get; init; }
        public uint VirtualSize { get; init; }
        public uint SizeOfRawData { get; init; }
        public uint PointerToRawData { get; init; }
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