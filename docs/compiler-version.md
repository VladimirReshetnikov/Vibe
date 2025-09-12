Below is a practical, “use-what’s-there” playbook for pulling *exact* or *heuristic* build‑provenance from a Windows DLL (native PE/COFF or .NET). It includes C# examples you can drop into a console app. I use only open‑source libraries (PeNet for PE parsing; optionally AsmResolver/dnlib), plus a few zero‑dependency helpers where library APIs are overkill.


## What you can know

**Hard / direct signals**

* **.NET assemblies**: `TargetFrameworkAttribute` tells the target framework (e.g., `.NETCoreApp,Version=v8.0`). References (e.g., `mscorlib` vs `System.Private.CoreLib`) indicate framework family and (roughly) standard library line. ([Microsoft Learn][1])
* **PDB/CodeView (RSDS)** record in the PE debug directory: precise PDB path, GUID, and age. The PDB path often exposes the **MSVC toolset folder** (e.g., `VC\Tools\MSVC\14.39.x\...`) and CI paths. ([DebugInfo][2])
* **PE Optional Header “LinkerVersion”**: strong hint of the linker family and toolset vintage (e.g., `14.3x` → VS 2022 v143 toolset). ([Microsoft for Developers][3])

**Heuristics**

* **MSVC runtime imports** (`msvcr100.dll`, `msvcr120.dll`, `vcruntime140.dll`, `msvcp140.dll`, `ucrtbase.dll`, `api-ms-win-crt-*.dll`) → MSVC / UCRT toolchain (VS 2015+ are 14.x toolsets). Exact minor version is not in the import name; combine with LinkerVersion/PDB path for precision. ([Microsoft Learn][4])
* **MinGW**: imports like `libstdc++-6.dll`, `libgcc_s_seh-1.dll` / `libgcc_s_dw2-1.dll`, `libwinpthread-1.dll` → GCC (MinGW/MinGW‑w64) toolchain. ([Reddit][5])
* **Delphi/C++Builder (Embarcadero/Borland)**: presence of resource `RCDATA\PACKAGEINFO` or strings/dep on `BORLNDMM.DLL`. ([hexacorn.com][6])
* **Clang/LLD on Windows**: absence of Microsoft “Rich” header + LinkerVersion matching LLVM lld, or `lld`/`clang` strings in the image/PDB path. (`clang-cl` with **link.exe** usually still has Rich header.) ([ROCm Documentation][7])
* **Go**: embedded **“Go build ID”** string (Windows PE stores it at the beginning of the .text equivalent) → exact Go toolchain presence; `go version -m` can decode it on the machine with Go installed. ([tip.golang.org][8])

**MSVC “Rich header”** (between DOS stub and PE header, added by Microsoft linkers)
If present, it encodes product/tool IDs and builds used during compilation (CL, LINK, CVTRES, MASM…)—very strong evidence of MS toolchain versions. Many non‑MSVC linkers (GCC/MinGW, .NET native IL, etc.) do **not** emit it. (It can be stripped or forged.) ([0xRick's Blog][9])

---

## Step 0 — Decide if it’s .NET or native

Use the PE headers: if the **COM Descriptor (CLI) directory**/COR header exists → managed (.NET); otherwise native. You can do this easily with `System.Reflection.PortableExecutable` or PeNet. ([Microsoft Learn][10])

---

## A. Native DLLs (PE/COFF) — exact + heuristic signals

### A1) Use PeNet to read Linker version, imports, and PDB/CodeView (RSDS)

> **NuGet**: `PeNet`

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PeNet; // Install-Package PeNet

public static class NativePeProbe
{
    public sealed record ProbeResult(
        string Arch,
        string LinkerVersion,
        string? PdbPath,
        string[] Imports,
        string? LikelyToolchain,
        string[] Notes
    );

    public static ProbeResult Analyze(string path)
    {
        var pe = new PeFile(path);

        // Arch & Linker version
        var fileHdr = pe.ImageNtHeaders.FileHeader;
        var optHdr  = pe.ImageNtHeaders.OptionalHeader;
        string arch = fileHdr.Machine.ToString();
        string linker = $"{optHdr.MajorLinkerVersion}.{optHdr.MinorLinkerVersion:D2}";

        // PDB / RSDS CodeView (if any)
        string? pdb = pe.ImageDebugDirectory?
            .FirstOrDefault(d => d.CvInfoPdb70 != null)?.CvInfoPdb70?.PdbFileName;

        // Imports
        var importDlls = pe.ImageImportDescriptors?
            .Select(d => d?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        // Heuristics
        var notes = new System.Collections.Generic.List<string>();
        string? toolchain = null;

        if (importDlls.Any(n => n!.Equals("msvcr100.dll", StringComparison.OrdinalIgnoreCase))) {
            toolchain = "MSVC (VS2010 era, 10.0 CRT)";
        }
        if (importDlls.Any(n => n!.Equals("msvcr120.dll", StringComparison.OrdinalIgnoreCase))) {
            toolchain = "MSVC (VS2013 era, 12.0 CRT)";
        }
        if (importDlls.Any(n => n!.Equals("vcruntime140.dll", StringComparison.OrdinalIgnoreCase) ||
                                n!.Equals("msvcp140.dll", StringComparison.OrdinalIgnoreCase)   ||
                                n!.Equals("ucrtbase.dll", StringComparison.OrdinalIgnoreCase)   ||
                                n!.StartsWith("api-ms-win-crt-", StringComparison.OrdinalIgnoreCase)))
        {
            toolchain = "MSVC 14.x toolset (VS 2015+); combine with Linker/PDB for exact minor";
            notes.Add("UCRT/vcruntime14x indicates VS 2015+ family.");
        }

        if (importDlls.Any(n => n!.Equals("libstdc++-6.dll", StringComparison.OrdinalIgnoreCase) ||
                                n!.Equals("libgcc_s_dw2-1.dll", StringComparison.OrdinalIgnoreCase) ||
                                n!.Equals("libgcc_s_seh-1.dll", StringComparison.OrdinalIgnoreCase) ||
                                n!.Equals("libwinpthread-1.dll", StringComparison.OrdinalIgnoreCase)))
        {
            toolchain = "GCC / MinGW (MinGW/MinGW-w64)";
        }

        // Delphi / C++Builder indicators
        if (importDlls.Any(n => n!.Equals("borlndmm.dll", StringComparison.OrdinalIgnoreCase)))
        {
            toolchain = "Delphi/C++Builder (Borland/Embarcadero)";
        }

        // PDB path heuristics (MSVC toolset folder)
        if (!string.IsNullOrEmpty(pdb))
        {
            var msvc = Regex.Match(pdb, @"\\VC\\Tools\\MSVC\\(?<ver>14\.\d+\.\d+)", RegexOptions.IgnoreCase);
            if (msvc.Success)
            {
                notes.Add($"Found MSVC toolset folder in PDB path: {msvc.Groups["ver"].Value} (v143 series / VS 2022).");
            }
            if (pdb.IndexOf("lld", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pdb.IndexOf("clang", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                notes.Add("PDB path mentions clang/lld → LLVM toolchain likely.");
                if (toolchain == null) toolchain = "Clang/LLD (Windows)";
            }
        }

        // Strings heuristics (Go build id, clang/LLVM)
        var bytes = File.ReadAllBytes(path);
        var ascii = System.Text.Encoding.ASCII.GetString(bytes);
        if (ascii.Contains("Go build ID:"))
        {
            toolchain = "Go (golang) toolchain";
            notes.Add("Detected Go build ID.");
        }
        if (ascii.Contains("LLD ") || ascii.Contains("clang version") || ascii.Contains("LLVM"))
        {
            notes.Add("Found LLVM/clang markers in file strings.");
            if (toolchain == null) toolchain = "Clang/LLD (Windows)";
        }

        // Rich header (optional, below) can refine MSVC versions
        try
        {
            var rh = RichHeaderParser.TryParse(path);
            if (rh is not null && rh.Records.Count > 0)
            {
                notes.Add($"Rich header present with {rh.Records.Count} tool records (MS link.exe).");
                if (toolchain is null) toolchain = "MSVC (Rich header present)";
            }
        }
        catch { /* ignore */ }

        return new ProbeResult(arch, linker, pdb, importDlls, toolchain, notes.ToArray());
    }
}
```

**Why this works:**

* PeNet exposes the PE Optional Header (for `Major/MinorLinkerVersion`), the Import Directory (for CRT/MinGW indicators), and the Debug Directory, including **CodeView PDB v7 (RSDS)** where the PDB path lives. Microsoft linkers write RSDS records and PDB paths; analysts routinely use them to infer build environments. ([Secana][11])
* The **LinkerVersion** correlates with the MSVC toolset vintage (e.g., 14.39/14.40 → VS 2022 v143), though exact mapping changes over time; Microsoft documents current toolset numbering (14.39…14.40+) for VS 2022. ([Microsoft for Developers][3])
* **Imports** reveal CRT family (MSVC vs MinGW) and, for Delphi, Borland memory manager. ([0xRick's Blog][12])
* **Go** leaves a “Go build ID” signature; IDA/Ghidra and industry YARA rules use it for reliable detection on PE files. ([tip.golang.org][8])

### A2) (Optional) Decode the **Rich header** for MSVC tool & build IDs

This pure‑C# helper finds the “Rich” signature before the PE header, decrypts the blob with the XOR key, and returns `(ProductId, Build, Count)` tuples. (MS linker writes it; GCC/MinGW/.NET images usually don’t have it.) ([0xRick's Blog][9])

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class RichHeaderParser
{
    public sealed record RichHeaderRecord(ushort ProductId, ushort Build, uint Count);
    public sealed record RichHeader(List<RichHeaderRecord> Records);

    // Returns null if no Rich header.
    public static RichHeader? TryParse(string path)
    {
        var data = File.ReadAllBytes(path);

        // Parse PE offset from DOS header
        int peOff = BitConverter.ToInt32(data, 0x3C);
        int searchEnd = Math.Min(peOff, data.Length);

        // Find "Rich" near the end of DOS stub
        int richIdx = -1;
        for (int i = 0; i < searchEnd - 3; i++)
            if (data[i] == (byte)'R' && data[i+1] == (byte)'i' && data[i+2] == (byte)'c' && data[i+3] == (byte)'h')
                richIdx = i;

        if (richIdx < 0 || richIdx + 8 >= searchEnd) return null;

        // XOR key follows "Rich"
        uint key = BitConverter.ToUInt32(data, richIdx + 4);

        // Walk backward in 4-byte chunks until we hit "DanS"
        var recs = new List<RichHeaderRecord>();
        int p = richIdx - 4;
        while (p >= 0)
        {
            uint dw = BitConverter.ToUInt32(data, p);
            uint x = dw ^ key;
            if (x == 0x536E6144) // "DanS"
                break;

            // The rich data is a sequence of pairs: (CompId, Count)
            // CompId: low 16 = ProdId, high 16 = Build
            // We'll read pairs going backward, so capture them after we collect two.
            // Here we push raw words and pair them later.
            p -= 4;

            // Next dword is Count (also XORed)
            if (p < 0) break;
            uint cnt = BitConverter.ToUInt32(data, p) ^ key;

            ushort prodId = (ushort)(x & 0xFFFF);
            ushort build  = (ushort)(x >> 16);

            recs.Add(new RichHeaderRecord(prodId, build, cnt));
            p -= 4;
        }

        recs.Reverse();
        return new RichHeader(recs);
    }
}
```

> **Interpretation tip.** Each record says “tool ProductId (e.g., CL, LINK, MASM, CVTRES…), BuildNumber, Count”. Exact mapping tables aren’t officially documented, but researchers correlate them with Visual Studio toolchain versions (e.g., via strings in `msobj*.lib`) and use them to fingerprint builds. Treat them as strong evidence, not absolute truth. ([Virus Bulletin][13])

---

## B. Managed (.NET) DLLs — target framework & standard library line

Use the standard metadata rather than the PE heuristics:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Collections.Generic;

// Reads TFM, common references, and basic assembly info without loading into the default context.
public static class ManagedProbe
{
    public sealed record DotNetInfo(
        string? TargetFramework,
        string[] ReferencedAssemblies,
        string? LikelyStdLib,
        string[] Notes
    );

    public static DotNetInfo Analyze(string assemblyPath)
    {
        // Build a MetadataLoadContext using the runtime's TPA set + the target assembly.
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var resolver = new PathAssemblyResolver(tpa.Append(assemblyPath));
        using var mlc = new MetadataLoadContext(resolver);
        var asm = mlc.LoadFromAssemblyPath(assemblyPath);

        // TargetFrameworkAttribute tells you .NETFramework/.NETCoreApp/.NETStandard + version.
        var tfa = asm.GetCustomAttributesData()
                     .FirstOrDefault(a => a.AttributeType.FullName == typeof(TargetFrameworkAttribute).FullName);

        string? tfm = tfa?.ConstructorArguments.Count == 1
                      ? tfa.ConstructorArguments[0].Value as string
                      : null;

        // References: look for mscorlib/System.Private.CoreLib to infer stdlib family.
        var refs = asm.GetReferencedAssemblies().Select(r => r.FullName).OrderBy(s => s).ToArray();
        string? stdlib =
            refs.Any(r => r.StartsWith("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase))
            ? "CoreLib (System.Private.CoreLib) — .NET Core/5+/6+/7+/8+"
            : refs.Any(r => r.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase))
              ? "mscorlib — .NET Framework"
              : null;

        var notes = new List<string>();
        if (refs.Any(r => r.StartsWith("FSharp.Core", StringComparison.OrdinalIgnoreCase)))
            notes.Add("Contains FSharp.Core reference (F# likely).");
        if (refs.Any(r => r.StartsWith("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase)))
            notes.Add("References Microsoft.VisualBasic (VB usage possible).");

        return new DotNetInfo(tfm, refs, stdlib, notes.ToArray());
    }
}
```

* `TargetFrameworkAttribute` is *the* canonical declaration of the target framework/version. ([Microsoft Learn][1])
* `MetadataLoadContext` is the supported way to inspect external assemblies without fully loading them (solve binding by giving it a resolver). ([Microsoft Learn][14])

> **Caveat**: .NET compilers (Roslyn C#/VB, F#, C++/CLI) generally don’t stamp the *compiler version* into metadata. The TFM and corelib references tell you the *target framework and standard library line*; language/compiler identification relies on references and occasional language‑specific artifacts (e.g., F# `StartupCode$…` types). ([Stack Overflow][15])

---

## Putting it together — one function that handles both

```csharp
public static class DllBuildProvenance
{
    public sealed record Result(
        bool IsDotNet,
        NativePeProbe.ProbeResult? Native,
        ManagedProbe.DotNetInfo? Managed
    );

    public static Result AnalyzeDll(string path)
    {
        using var fs = File.OpenRead(path);
        using var peReader = new System.Reflection.PortableExecutable.PEReader(fs);
        bool hasCor = peReader.PEHeaders.CorHeader != null; // .NET?
        if (hasCor)
        {
            return new Result(true, null, ManagedProbe.Analyze(path));
        }
        else
        {
            return new Result(false, NativePeProbe.Analyze(path), null);
        }
    }
}
```

---

## Example output (interpretation guide)

Imagine a native DLL yields:

```
Arch: AMD64
LinkerVersion: 14.39
PdbPath: C:\agent\_work\1\s\build\out\bin\Release\foo.pdb
Imports: [ KERNEL32.DLL, VCRUNTIME140.DLL, UCRTBASE.DLL, MSVCP140.DLL, ...]
LikelyToolchain: MSVC 14.x (VS 2015+)
Notes:
- Found MSVC toolset folder in PDB path: 14.39.33130 (v143 / VS 2022)
- Rich header present with 5 tool records (MS link.exe).
```

Interpretation: “MSVC link.exe, VS 2022 family (v143 toolset, 14.39), UCRT‑based C/C++ runtime”. (Use Microsoft’s toolset versioning notes to align 14.39/14.40+ with specific VS 2022 releases.) ([Microsoft for Developers][3])

A .NET DLL might yield:

```
TargetFramework: .NETCoreApp,Version=v8.0
ReferencedAssemblies: [System.Private.CoreLib, System.Runtime, ...]
LikelyStdLib: CoreLib (System.Private.CoreLib) — .NET 8 line
Notes: []
```

Interpretation: “Built for .NET 8; standard library family is CoreLib (the .NET Core/5+/8+ line).” ([Microsoft Learn][1])

---

## Extras & edge cases you may care about

* **Clang/LLD vs link.exe**: MSVC front‑end vs Clang front‑end is tricky on Windows. `clang-cl` with **MSVC link.exe** → you’ll still see a Rich header and MSVC LinkerVersion. Using **lld‑link** may lack a Rich header and show lld/clang clues in strings or PDB paths. Combine signals (LinkerVersion, Rich, strings, PDB). ([ROCm Documentation][7])
* **MinGW**: Presence of `libstdc++-6.dll`/`libgcc_s_*`/`libwinpthread-1.dll` imports is a strong GCC signal. ([Reddit][5])
* **Delphi**: `RCDATA\PACKAGEINFO` resource or `BORLNDMM.DLL` usage point to Delphi/C++Builder. ([hexacorn.com][6])
* **Go**: “Go build ID” string embedded in PE files is a dead‑giveaway; many tools/YARA rules key off it. ([docs.hex-rays.com][16])
* **PDB stripping**: Some builds null the PDB path despite RSDS record (you’ll still get GUID/age). ([Google Cloud][17])
* **No single source of truth**: *Exact* “compiler version” for native code is best triangulated from (a) **LinkerVersion**, (b) **Rich header build IDs** (if present), (c) **PDB path** (toolset folder), and (d) **imports**. Microsoft documents the current **14.3x/14.4x** MSVC toolset numbering for VS 2022. ([Microsoft for Developers][3])

---

## References (why these signals are trusted)

* **PE/COFF RSDS & PDB path**: format + where it’s stored; PeNet example pulling `CvInfoPdb70`. ([DebugInfo][2])
* **Rich header**: structure, purpose, Microsoft toolchain provenance, prevalence/absence in non‑MSVC builds. ([0xRick's Blog][9])
* **MSVC toolset numbering** (14.39/14.40… → VS 2022 v143 series). ([Microsoft for Developers][3])
* **MinGW import heuristics**. ([Reddit][5])
* **Delphi indicators**. ([hexacorn.com][6])
* **Go “build ID” marker on Windows PEs**. ([tip.golang.org][8])
* **TargetFrameworkAttribute** for .NET. ([Microsoft Learn][1])
* **MetadataLoadContext** for safe out‑of‑context inspection. ([Microsoft Learn][14])

---

## Variations / extensions

* **AsmResolver** (`AsmResolver.PE`) also surfaces Debug Directory and PE imports if you prefer it to PeNet. ([GitHub][18])
* **System.Reflection.Metadata** (`PEReader`, `MetadataReader`) lets you parse IL metadata directly if you want to avoid loading with `MetadataLoadContext`. ([NuGet][19])
* **YARA** signatures\*\*:\*\* you can codify many of the string/section/RSDS heuristics to bulk‑scan files at scale (typical in DFIR pipelines). ([GitHub][20])

---

### TL;DR checklist

1. **Is it .NET?** If yes → read `TargetFrameworkAttribute` and referenced corelib. ([Microsoft Learn][1])
2. **If native**:

    * Read **LinkerVersion** (PE Optional Header). ([Microsoft Learn][21])
    * Extract **RSDS/PDB path** (Debug Directory). Look for `VC\Tools\MSVC\14.xx` or `clang/lld`. ([Secana][11])
    * Enumerate **imports** (MSVC CRT vs MinGW) and special telltales (Delphi, Go). ([0xRick's Blog][12])
    * Try **Rich header** for MSVC tools/builds (if present). ([0xRick's Blog][9])

Use multiple signals together for the strongest attribution.

[1]: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.versioning.targetframeworkattribute?view=net-9.0&utm_source=chatgpt.com "TargetFrameworkAttribute Class (System.Runtime. ..."
[2]: https://www.debuginfo.com/articles/debuginfomatch.html?utm_source=chatgpt.com "Matching debug information - DebugInfo.com"
[3]: https://devblogs.microsoft.com/cppblog/msvc-toolset-minor-version-number-14-40-in-vs-2022-v17-10/?utm_source=chatgpt.com "MSVC Toolset Minor Version Number 14.40 in VS 2022 ..."
[4]: https://learn.microsoft.com/en-us/cpp/build/building-on-the-command-line?view=msvc-170&utm_source=chatgpt.com "Use the Microsoft C++ toolset from the command line"
[5]: https://www.reddit.com/r/eclipse/comments/p1ca17/libstdc6dll_was_not_found_libgcc_s_dw21dll_was/?utm_source=chatgpt.com "\"libstdc++-6.dll was not found\" & \"libgcc_s_dw2-1.dll was ..."
[6]: https://www.hexacorn.com/blog/2020/04/24/re-sauce-part-1/?utm_source=chatgpt.com "Re-sauce, Part 1"
[7]: https://rocm.docs.amd.com/projects/llvm-project/en/docs-6.3.1/LLVM/lld/html/?utm_source=chatgpt.com "The LLVM Linker — lld 18.0.0git documentation"
[8]: https://tip.golang.org/src/cmd/link/internal/ld/data.go?utm_source=chatgpt.com "data.go"
[9]: https://0xrick.github.io/win-internals/pe3/?utm_source=chatgpt.com "PE file structure - Part 2: DOS Header, DOS Stub and Rich ..."
[10]: https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.peheaders?view=net-9.0&utm_source=chatgpt.com "PEHeaders Class (System.Reflection.PortableExecutable)"
[11]: https://secana.github.io/PeNet/articles/pdb.html "PDB & Debug Information "
[12]: https://0xrick.github.io/win-internals/pe6/?utm_source=chatgpt.com "Part 5: PE Imports (Import Directory Table, ILT, IAT) - 0xRick's ..."
[13]: https://www.virusbulletin.com/uploads/pdf/magazine/2019/VB2019-Kalnai-Poslusny.pdf?utm_source=chatgpt.com "Rich Headers"
[14]: https://learn.microsoft.com/en-us/dotnet/standard/assembly/inspect-contents-using-metadataloadcontext?utm_source=chatgpt.com "How to: Inspect assembly contents using ..."
[15]: https://stackoverflow.com/questions/63847374/how-determine-that-assembly-was-compiled-from-f-project?utm_source=chatgpt.com "How determine that assembly was compiled from f# project?"
[16]: https://docs.hex-rays.com/user-guide/plugins/plugins-shipped-with-ida/golang-plugin?utm_source=chatgpt.com "Golang plugin"
[17]: https://cloud.google.com/blog/topics/threat-intelligence/definitive-dossier-of-devilish-debug-details-part-one-pdb-paths-malware?utm_source=chatgpt.com "Part One: PDB Paths and Malware | Mandiant"
[18]: https://github.com/Washi1337/AsmResolver?utm_source=chatgpt.com "Washi1337/AsmResolver: A library for creating, reading ..."
[19]: https://www.nuget.org/packages/System.Reflection.Metadata?utm_source=chatgpt.com "System.Reflection.Metadata 9.0.9"
[20]: https://github.com/Yara-Rules/rules?utm_source=chatgpt.com "Repository of yara rules"
[21]: https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.peheader?view=net-9.0&utm_source=chatgpt.com "PEHeader Class (System.Reflection.PortableExecutable)"
