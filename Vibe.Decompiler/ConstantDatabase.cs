// SPDX-License-Identifier: MIT-0

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Vibe.Decompiler;

/// <summary>
/// Represents a candidate symbolic name for a numeric constant.
/// </summary>
/// <param name="EnumFullName">Fully qualified enum or static class name.</param>
/// <param name="Formatted">Formatted member name, e.g. <c>Enum.Value</c>.</param>
public readonly record struct ConstantMatch(string EnumFullName, string Formatted);

/// <summary>
/// Exposes APIs for looking up symbolic constant names.
/// </summary>
public interface IConstantNameProvider
{
    /// <summary>
    /// Attempts to format <paramref name="value"/> as a member of the specified enum type.
    /// </summary>
    /// <returns><c>true</c> when a symbolic name was found.</returns>
    bool TryFormatValue(string enumFullName, ulong value, out string name);

    /// <summary>
    /// Finds all constants across all loaded enums that match the given value.
    /// </summary>
    /// <param name="bitWidth">Width of the underlying type; values are masked accordingly.</param>
    IEnumerable<ConstantMatch> FindByValue(ulong value, int bitWidth = 32);
}

public sealed class ConstantDatabase : IConstantNameProvider
{
    private readonly Dictionary<string, EnumDesc> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, string>> _callArgEnums = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, List<ConstantMatch>> _valueIndex = new();
    private readonly List<EnumDesc> _flagEnums = new();

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

    /// <summary>
    /// Associates a function argument with an expected enumeration type so that
    /// constants used in calls can later be formatted symbolically.
    /// </summary>
    public void MapArgEnum(string callSymbolName, int argIndex, string enumFullName)
    {
        if (!_callArgEnums.TryGetValue(callSymbolName, out var map))
            _callArgEnums[callSymbolName] = map = new Dictionary<int, string>();
        map[argIndex] = enumFullName;
    }

    /// <summary>
    /// Attempts to determine the enumeration type expected for a given call argument.
    /// </summary>
    public bool TryGetArgExpectedEnumType(string? callTargetSymbol, int argIndex, out string enumTypeFullName)
    {
        enumTypeFullName = "";
        if (string.IsNullOrEmpty(callTargetSymbol)) return false;
        var sym = callTargetSymbol!;
        int bang = sym.IndexOf('!');
        if (bang >= 0 && bang + 1 < sym.Length) sym = sym[(bang + 1)..];

        return _callArgEnums.TryGetValue(sym, out var map) &&
               map.TryGetValue(argIndex, out enumTypeFullName) &&
               _enums.ContainsKey(enumTypeFullName);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IEnumerable<ConstantMatch> FindByValue(ulong value, int bitWidth = 32)
    {
        ulong mask = bitWidth >= 64 ? ulong.MaxValue : (1UL << bitWidth) - 1UL;
        ulong masked = value & mask;

        var seen = new HashSet<ConstantMatch>();

        if (_valueIndex.TryGetValue(masked, out var list))
        {
            foreach (var m in list)
            {
                if (seen.Add(m))
                    yield return m;
            }
        }

        foreach (var ed in _flagEnums)
        {
            if (ed.UnderlyingBits > bitWidth) continue;

            ulong remaining = masked;
            var parts = new List<string>();
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
                string fmt = string.Join(" | ", parts);
                var match = new ConstantMatch(ed.FullName, fmt);
                if (seen.Add(match))
                    yield return match;
            }
        }
    }

    /// <summary>
    /// Registers an enumeration description with the database and updates
    /// the reverse lookup indexes for fast value to name resolution.
    /// </summary>
    private void AddEnum(EnumDesc desc)
    {
        _enums[desc.FullName] = desc;
        foreach (var kv in desc.ValueToName)
        {
            if (!_valueIndex.TryGetValue(kv.Key, out var list))
                list = _valueIndex[kv.Key] = new List<ConstantMatch>();
            list.Add(new ConstantMatch(desc.FullName, kv.Value));
        }

        if (desc.Flags || desc.LooksLikeFlags)
            _flagEnums.Add(desc);
    }

    /// <summary>
    /// Loads all enum definitions from a Windows metadata (<c>.winmd</c>) file
    /// and adds them to the database for constant name lookups.
    /// </summary>
    /// <param name="winmdPath">Path to the <c>.winmd</c> file.</param>
    public void LoadWin32MetadataFromWinmd(string winmdPath)
    {
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
            AddEnum(desc);
        }
    }

    /// <summary>
    /// Loads constant definitions from a managed assembly. Both enums and
    /// classes containing literal fields are supported.
    /// </summary>
    /// <param name="asm">Assembly whose types should be scanned.</param>
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
                AddEnum(desc);
            }
            else if (t.IsAbstract && t.IsSealed)
            {
                var full = t.FullName ?? t.Name;
                var desc = new EnumDesc(full) { Flags = false, UnderlyingBits = 32 };
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!f.IsLiteral) continue;
                    object? val = f.GetRawConstantValue();
                    if (val is null) continue;
                    ulong v = ConvertToUInt64(val);
                    string key = $"{full}.{f.Name}";
                    if (!desc.ValueToName.ContainsKey(v))
                        desc.ValueToName[v] = key;
                }
                if (desc.ValueToName.Count > 0)
                {
                    desc.FinalizeAfterLoad();
                    AddEnum(desc);
                }
            }
        }
    }

    /// <summary>
    /// Determines whether the specified type definition represents an enum.
    /// </summary>
    private static bool IsEnum(MetadataReader md, TypeDefinition td)
    {
        var bt = td.BaseType;
        if (bt.Kind != HandleKind.TypeReference) return false;
        var tr = md.GetTypeReference((TypeReferenceHandle)bt);
        string ns = md.GetString(tr.Namespace);
        string n = md.GetString(tr.Name);
        return ns == "System" && n == "Enum";
    }

    /// <summary>
    /// Checks if the given type definition has the <c>[Flags]</c> attribute applied.
    /// </summary>
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

    /// <summary>
    /// Attempts to resolve the namespace and name of an attribute referenced by
    /// <paramref name="ctor"/>.
    /// </summary>
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

    /// <summary>
    /// Retrieves the bit width of the enum's underlying integral type from its metadata signature.
    /// </summary>
    private static int UnderlyingBitsFromSignature(MetadataReader md, FieldDefinition f)
    {
        var sig = md.GetBlobReader(f.Signature);
        if (sig.ReadByte() != 0x06) return 32;
        var (bits, _) = ReadTypeCode(sig);
        return bits == 0 ? 32 : bits;
    }

    /// <summary>
    /// Parses a primitive type code from a metadata signature and returns its size and signedness.
    /// </summary>
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

    /// <summary>
    /// Reads a constant value from metadata and converts it to an unsigned 64-bit integer.
    /// </summary>
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

    /// <summary>
    /// Converts a boxed integral value to an unsigned 64-bit representation.
    /// </summary>
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

        /// <summary>
        /// Finalizes the descriptor after all members have been loaded,
        /// computing auxiliary data used for flag enumeration formatting.
        /// </summary>
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
