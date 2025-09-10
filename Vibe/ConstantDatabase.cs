// SPDX-License-Identifier: MIT-0
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

public interface IConstantNameProvider
{
    bool TryFormatValue(string enumFullName, ulong value, out string name);
}

public sealed class ConstantDatabase : IConstantNameProvider
{
    private readonly Dictionary<string, EnumDesc> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, string>> _callArgEnums = new(StringComparer.OrdinalIgnoreCase);

    public ConstantDatabase()
    {
        MapArgEnum("VirtualAlloc", 2, "Windows.Win32.System.Memory.MEMORY_ALLOCATION_TYPE");
        MapArgEnum("VirtualAlloc", 3, "Windows.Win32.System.Memory.PAGE_PROTECTION_FLAGS");
        MapArgEnum("VirtualProtect", 2, "Windows.Win32.System.Memory.PAGE_PROTECTION_FLAGS");
        MapArgEnum("OpenProcess", 1, "Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS");
        MapArgEnum("LoadLibraryExW", 2, "Windows.Win32.System.LibraryLoader.LOAD_LIBRARY_FLAGS");
        MapArgEnum("CreateFileW", 1, "Windows.Win32.Storage.FileSystem.FILE_ACCESS_RIGHTS");
        MapArgEnum("CreateFileW", 2, "Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE");
        MapArgEnum("CreateFileW", 5, "Windows.Win32.Storage.FileSystem.FILE_FLAGS_AND_ATTRIBUTES");
    }

    public void MapArgEnum(string callSymbolName, int argIndex, string enumFullName)
    {
        if (!_callArgEnums.TryGetValue(callSymbolName, out var map))
            _callArgEnums[callSymbolName] = map = new Dictionary<int, string>();
        map[argIndex] = enumFullName;
    }

    public bool TryGetArgExpectedEnumType(string? callTargetSymbol, int argIndex, out string enumTypeFullName)
    {
        enumTypeFullName = "";
        if (string.IsNullOrEmpty(callTargetSymbol)) return false;
        var sym = callTargetSymbol!;
        int bang = sym.IndexOf('!');
        if (bang >= 0 && bang + 1 < sym.Length) sym = sym[(bang + 1)..];

        if (_callArgEnums.TryGetValue(sym, out var map) && map.TryGetValue(argIndex, out enumTypeFullName))
            return _enums.ContainsKey(enumTypeFullName);
        return false;
    }

    public bool TryFormatValue(string enumTypeFullName, ulong value, out string formatted)
    {
        formatted = "";
        if (!_enums.TryGetValue(enumTypeFullName, out var ed)) return false;

        if (ed.ValueToName.TryGetValue(value, out var exact))
        {
            formatted = exact;
            return true;
        }

        if (ed.Flags || ed.LooksLikeFlags)
        {
            var parts = new List<string>();
            ulong remaining = value;
            foreach (var p in ed.FlagParts)
            {
                if ((remaining & p.Mask) == p.Mask)
                {
                    parts.Add(p.Name);
                    remaining &= ~p.Mask;
                }
            }
            if (parts.Count > 0 && remaining == 0)
            {
                formatted = string.Join(" | ", parts);
                return true;
            }
        }

        formatted = $"0x{value:X}";
        return false; // important: hex fallback is not a symbolic match
    }

    public void LoadWin32MetadataFromWinmd(string winmdPath)
    {
        Console.WriteLine($"Loading metadata from ${winmdPath}");
        using var fs = File.OpenRead(winmdPath);
        using var pe = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
        var md = pe.GetMetadataReader();

        foreach (var tdHandle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(tdHandle);
            string ns = md.GetString(td.Namespace);
            string name = md.GetString(td.Name);
            string full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

            if (!IsEnum(md, td)) continue;

            var desc = new EnumDesc(full)
            {
                Flags = HasFlagsAttribute(md, td)
            };

            int bits = 32;
            foreach (var fHandle in td.GetFields())
            {
                var f = md.GetFieldDefinition(fHandle);
                string fName = md.GetString(f.Name);
                if (fName == "value__")
                {
                    bits = UnderlyingBitsFromSignature(md, f);
                    break;
                }
            }
            desc.UnderlyingBits = bits;

            foreach (var fHandle in td.GetFields())
            {
                var f = md.GetFieldDefinition(fHandle);
                if (f.GetDefaultValue().IsNil) continue;
                string fName = md.GetString(f.Name);
                ulong val = ReadConstantValueAsUInt64(md, f.GetDefaultValue());
                if (!desc.ValueToName.ContainsKey(val))
                    desc.ValueToName[val] = $"{full}.{fName}";
            }

            desc.FinalizeAfterLoad();
            _enums[full] = desc;
        }
    }

    public void LoadFromAssembly(Assembly asm)
    {
        foreach (var t in asm.GetTypes())
        {
            if (t.IsEnum)
            {
                var full = t.FullName ?? t.Name;
                var desc = new EnumDesc(full)
                {
                    Flags = t.GetCustomAttributes(typeof(FlagsAttribute), inherit: false).Any(),
                    UnderlyingBits = Math.Max(8, Math.Min(64, Marshal.SizeOf(Enum.GetUnderlyingType(t)) * 8))
                };
                foreach (var name in Enum.GetNames(t))
                {
                    var valObj = Convert.ChangeType(Enum.Parse(t, name), Enum.GetUnderlyingType(t));
                    ulong v = ConvertToUInt64(valObj);
                    if (!desc.ValueToName.ContainsKey(v))
                        desc.ValueToName[v] = $"{full}.{name}";
                }
                desc.FinalizeAfterLoad();
                _enums[full] = desc;
            }
            else if (t.IsAbstract && t.IsSealed)
            {
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!f.IsLiteral) continue;
                    object? val = f.GetRawConstantValue();
                    if (val is null) continue;
                    var full = t.FullName ?? t.Name;
                    if (!_enums.TryGetValue(full, out var desc))
                    {
                        desc = new EnumDesc(full) { Flags = false, UnderlyingBits = 32 };
                        _enums[full] = desc;
                    }
                    ulong v = ConvertToUInt64(val);
                    string key = $"{full}.{f.Name}";
                    desc.ValueToName.TryAdd(v, key);
                }
            }
        }
        foreach (var e in _enums.Values) e.FinalizeAfterLoad();
    }

    private static bool IsEnum(MetadataReader md, TypeDefinition td)
    {
        var bt = td.BaseType;
        if (bt.Kind != HandleKind.TypeReference) return false;
        var tr = md.GetTypeReference((TypeReferenceHandle)bt);
        string ns = md.GetString(tr.Namespace);
        string n = md.GetString(tr.Name);
        return ns == "System" && n == "Enum";
    }

    private static bool HasFlagsAttribute(MetadataReader md, TypeDefinition td)
    {
        foreach (var caHandle in td.GetCustomAttributes())
        {
            var ca = md.GetCustomAttribute(caHandle);
            if (TryGetAttributeTypeName(md, ca.Constructor, out var attNs, out var attName))
            {
                if (attNs == "System" && attName == "FlagsAttribute") return true;
            }
        }
        return false;
    }

    private static bool TryGetAttributeTypeName(MetadataReader md, EntityHandle ctor, out string ns, out string name)
    {
        ns = ""; name = "";
        switch (ctor.Kind)
        {
            case HandleKind.MemberReference:
            {
                var mr = md.GetMemberReference((MemberReferenceHandle)ctor);
                switch (mr.Parent.Kind)
                {
                    case HandleKind.TypeReference:
                        var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                        ns = md.GetString(tr.Namespace);
                        name = md.GetString(tr.Name);
                        return true;
                    case HandleKind.TypeDefinition:
                        var td = md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent);
                        ns = md.GetString(td.Namespace);
                        name = md.GetString(td.Name);
                        return true;
                }

                return false;
            }
            case HandleKind.MethodDefinition:
            {
                var mdh = (MethodDefinitionHandle)ctor;
                var mdDef = md.GetMethodDefinition(mdh);
                var td = md.GetTypeDefinition(mdDef.GetDeclaringType());
                ns = md.GetString(td.Namespace);
                name = md.GetString(td.Name);
                return true;
            }
        }
        return false;
    }

    private static int UnderlyingBitsFromSignature(MetadataReader md, FieldDefinition f)
    {
        var sig = md.GetBlobReader(f.Signature);
        if (sig.ReadByte() != 0x06) return 32;
        var (bits, _) = ReadTypeCode(sig);
        return bits == 0 ? 32 : bits;
    }

    private static (int bits, bool isSigned) ReadTypeCode(BlobReader br)
    {
        var code = (SignatureTypeCode)br.ReadCompressedInteger();
        return code switch
        {
            SignatureTypeCode.SByte => (8, true),
            SignatureTypeCode.Byte => (8, false),
            SignatureTypeCode.Int16 => (16, true),
            SignatureTypeCode.UInt16 => (16, false),
            SignatureTypeCode.Int32 => (32, true),
            SignatureTypeCode.UInt32 => (32, false),
            SignatureTypeCode.Int64 => (64, true),
            SignatureTypeCode.UInt64 => (64, false),
            _ => (0, false)
        };
    }

    private static ulong ReadConstantValueAsUInt64(MetadataReader md, ConstantHandle ch)
    {
        var c = md.GetConstant(ch);
        var br = md.GetBlobReader(c.Value);
        return c.TypeCode switch
        {
            ConstantTypeCode.SByte => unchecked((ulong)(sbyte)br.ReadSByte()),
            ConstantTypeCode.Byte => br.ReadByte(),
            ConstantTypeCode.Int16 => unchecked((ulong)br.ReadInt16()),
            ConstantTypeCode.UInt16 => br.ReadUInt16(),
            ConstantTypeCode.Int32 => unchecked((ulong)br.ReadInt32()),
            ConstantTypeCode.UInt32 => br.ReadUInt32(),
            ConstantTypeCode.Int64 => unchecked((ulong)br.ReadInt64()),
            ConstantTypeCode.UInt64 => br.ReadUInt64(),
            _ => 0UL
        };
    }

    private static ulong ConvertToUInt64(object v)
        => v switch
        {
            sbyte a => unchecked((ulong)a),
            byte b => b,
            short s => unchecked((ulong)s),
            ushort us => us,
            int i => unchecked((ulong)i),
            uint ui => ui,
            long l => unchecked((ulong)l),
            ulong ul => ul,
            _ => 0UL
        };

    private sealed class EnumDesc
    {
        public string FullName { get; }
        public int UnderlyingBits { get; set; } = 32;
        public bool Flags { get; set; }
        public bool LooksLikeFlags { get; private set; }
        public readonly Dictionary<ulong, string> ValueToName = new();
        public readonly List<(ulong Mask, string Name)> FlagParts = new();

        public EnumDesc(string full) { FullName = full; }

        public void FinalizeAfterLoad()
        {
            var singles = ValueToName.Keys.Where(v => v != 0 && (v & (v - 1)) == 0).ToList();
            LooksLikeFlags = Flags || singles.Count >= Math.Max(1, ValueToName.Count / 2);
            if (!LooksLikeFlags) return;
            foreach (var s in singles) FlagParts.Add((s, ValueToName[s]));
            FlagParts.Sort((a, b) => b.Mask.CompareTo(a.Mask));
        }
    }
}
