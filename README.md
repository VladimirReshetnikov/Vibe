Vibe — x64 PE export → C‑like pseudocode
===============================================

**Vibe**

1.  Locates an exported function inside a 64‑bit Windows DLL,
2.  Slices out its machine code directly from the PE file,
3.  Decodes instructions with Iced,
4.  Lifts them to a simple intermediate representation (IR),
5.  Applies a few readability passes (memset/memcpy coalescing, stack‑frame aliasing, constant naming),
6.  Pretty‑prints C‑style pseudocode **alongside the original assembly** (as comments).

The eventual goal is to create a high-fidelity decompiler into C code comparable with human-written code.
* * *

Table of Contents
-----------------

*   [Quick Start](#quick-start)
*   [What You Get](#what-you-get)
*   [How It Works (Pipeline)](#how-it-works-pipeline)
*   [Architecture Overview](#architecture-overview)
    *   [IR (types, expressions, statements, printer)](#ir-types-expressions-statements-printer)
    *   [Decompiler (decode → IR)](#decompiler-decode--ir)
    *   [Transformations (readability passes)](#transformations-readability-passes)
    *   [ConstantDatabase (symbolic constants & flags)](#constantdatabase-symbolic-constants--flags)
    *   [PEReader (minimal PE32+ reader)](#pereader-minimal-pe32-reader)
    *   [Program entry point](#program-entry-point)
*   [Key Heuristics](#key-heuristics)
*   [Usage Patterns & Extensibility](#usage-patterns--extensibility)
*   [Limitations](#limitations)
*   [Troubleshooting](#troubleshooting)
*   [Roadmap / Ideas](#roadmap--ideas)
*   [Acknowledgments](#acknowledgments)

* * *

Quick Start
-----------

### Prerequisites

*   **Windows x64**
*   **.NET SDK 8.0** (project targets `net8.0`)
*   NuGet package: **Iced 1.21.0** (restored automatically by `dotnet`)

### Build

```bash
dotnet build -c Release
```

### Run (current default)

`Program.cs` calls:

```csharp
var disasm = DisassembleExportToPseudo(
    "dbghelp.dll",
    "MakeSureDirectoryPathExists",
    256 * 1024);
Console.WriteLine(disasm);
```

So running the built exe prints pseudocode for **`dbghelp!MakeSureDirectoryPathExists`**. Example output is included at the end of this README (truncated here).

> **Note on constants (Win32 enums):**  
> `Program.cs` tries to load Win32 metadata (`Windows.Win32.winmd`) to print return values as symbolic names (e.g., `STATUS_*`). It first searches the repo and standard NuGet caches for any `Microsoft.Windows.SDK.Win32Metadata` package, using the metadata if found. If no `.winmd` is located, decompilation still proceeds but constants remain numeric.

* * *

What You Get
------------

*   Original assembly printed as comments, every line prefixed with its **absolute IP** (image base + RVA).
*   C‑like pseudocode with conservative types (e.g., `uint64_t`, `*(uint32_t*)(addr)`).
*   Recognition of:
    *   simple prologue/epilogue boilerplate,
    *   `BT` → carry‑flag predicates,
    *   `setcc` / `cmovcc` / `jcc` with signed/unsigned condition hints,
    *   REP string ops → `memcpy`/`memset`,
    *   SSE zero‑idioms coalesced to `memset`,
    *   adjacent 16‑byte memcpy blocks coalesced to a single `memcpy`,
    *   stack frame slices aliased as locals (`frame_0x...`) when a `memset` zeroes a region,
    *   `gs:[0x60]` → a local `peb` pointer,
    *   optional **symbolic constants** for return values (e.g., `NTSTATUS` names).
*   A small set of clean IR nodes that are easy to extend with further analyses.

* * *

How It Works (Pipeline)
-----------------------

**Input** → DLL path + export name  
**Output** → Annotated C‑style pseudocode string

```
PEReader
  └─ Resolve export (follow forwarders)
  └─ Extract code slice (bounded by section + user max)

Iced.Intel Decoder
  └─ Decode x64 instructions until the first RET (or byte limit)

Decompiler
  ├─ Label analysis (targets inside slice)
  ├─ Prologue/locals detection (MSVC‑ish, rbp/rsp)
  ├─ Detect well‑knowns (gs:[0x60] → PEB alias)
  ├─ Translate each instruction → IR
  │    • keep original disasm as AsmStmt (always)
  │    • emit conservative semantic IR when safe
  └─ Build FunctionIR

Transformations
  ├─ FrameObjectClusteringAndRspAlias        (alias memset’d rsp regions)
  ├─ DropRedundantBitTestPseudo              (cleanup)
  ├─ MapNamedReturnConstants / MapRetAssign  (enum names)
  └─ SimplifyRedundantAssign                 (x = x → nop)

IR.PrettyPrinter
  └─ Signature, locals, body
  └─ Assembly as comments + pseudocode statements
```

* * *

Architecture Overview
---------------------

### IR (types, expressions, statements, printer)

File: **[`IR.cs`](IR.cs)**

*   **Types (`IrType`)**: `IntType`, `FloatType`, `PointerType`, `VectorType`, `UnknownType`, `VoidType`.  
    Pretty‑printer favors stdint names (`uint8_t`, `uint16_t`, …) by default.
*   **Expressions (`Expr`)**: registers, locals, loads/stores, constants (`Const`/`UConst`/`SymConst`), binary/unary ops, casts (several kinds), calls (`CallExpr`), intrinsics (`rotl`/`rotr`, etc.), ternary, label refs.
*   **Statements (`Stmt`)**: assignment, memory store, call, `if (cond) goto Lx`, `goto`, label, `return`, `AsmStmt` (original text), `PseudoStmt`, `Nop`.
*   **High‑level nodes (optional)**: placeholders for future structuring (`IfNode`, `WhileNode`, `SwitchNode`, …). The pretty‑printer currently emits block/linear form.
*   **Pretty printer**:
    *   Emphasizes **transparency**: emits the assembly comment line **for every decoded instruction** (with IP), then any semantic IR we inferred.
    *   Helpful knobs (see `PrettyPrinter.Options`):
        *   header comment, block labels on/off,
        *   signedness hints on comparisons,
        *   stdint type names on/off,
        *   indentation string.

> The IR is deliberately small and monomorphic to make further passes easy (constant propagation, SSA, structuring, etc.).

* * *

### Decompiler (decode → IR)

File: **[`Decompiler.cs`](Decompiler.cs)**

**Public API:**

```csharp
var decompiler = new Decompiler();
var pseudo = decompiler.ToPseudoCode(bytes, new Decompiler.Options { ... });
```

**Options**:

*   `BaseAddress` — assumed function base for RIP‑relative and label IPs.
*   `FunctionName` — pretty name in the header (`kernelbase.dll!Foo`).
*   `EmitLabels` — include `L1`, `L2`, … for branch targets.
*   `DetectPrologue` — hide low‑level stack boilerplate behind a pseudo line while still printing raw asm.
*   `CommentCompare` — print pseudo lines for `cmp`/`test`.
*   `MaxBytes` — decode limit (stop earlier on `ret`).
*   `ResolveImportName(ulong iatAddr)` — optional callback to name indirect calls fetched via RIP‑relative memory (IAT slots).
*   **Constants**: `ConstantProvider` (defaults to `ConstantDatabase`) and `ReturnEnumTypeFullName` (defaults to `Windows.Win32.Foundation.NTSTATUS`) enable **symbolic return constants**.

**Major steps inside `ToPseudoCode`**:

1.  **Decode** with Iced (64‑bit mode) until RET or `MaxBytes`.
2.  **Detect prologue/locals**:
    *   `push rbp / mov rbp, rsp / sub rsp, imm` pattern (MSVC‑ish).
    *   Track `LocalSize` for header comments.
3.  **Detect well‑knowns**:
    *   Single read from `gs:[0x60]` → introduce local `uint8_t* peb = (uint8_t*)__readgsqword(0x60);`
4.  **Label analysis**:
    *   Collect in‑range branch targets and map them to `L1`, `L2`, …
5.  **Instruction translation** → IR:
    *   Always emit `AsmStmt("0xIP: mnemonic operands")`
    *   Then translate to semantic IR when safe:
        *   Moves/loads/stores, LEA, zero idioms, arithmetic/bitwise, shift/rotate, `setcc`, `cmovcc`, `jcc`.
        *   `BT` family → cache for next `jcc` as `CF`‑based predicate.
        *   `cmp/test` → cached for next conditional (`LastCmp`).
        *   Calls:
            *   Heuristic: if call uses `(rcx, edx, r8d)` like `memset`, print `memset()` (and similar for string ops).
            *   Near targets → `sub_XXXXXXXX` name, RIP‑relative mem operand → IAT pointer (use `ResolveImportName` if provided).
        *   String ops with `rep` → `memcpy`/`memset`.
        *   SSE zero idioms + 16‑byte stores → `memset`.
6.  **Refinement passes** (see [Transformations](#transformations-readability-passes)).
7.  **Pretty print** IR.

* * *

### Transformations (readability passes)

File: **[`Transformations.cs`](Transformations.cs)**

*   **`FrameObjectClusteringAndRspAlias`**  
    Finds `memset((void*)(rsp + K), 0, N)` and creates a local pointer `frame_0xK` initialized to `(uint8_t*)(rsp + K)`.  
    Then rewrites occurrences of `(rsp + C)` inside `[K, K+N)` as `frame_0xK (+ delta)`.  
    Result: stack object accesses look like `frame_0x... + off` instead of raw `rsp ± const`.
*   **`DropRedundantBitTestPseudo`**  
    Removes internal pseudo notes like `"CF = bit(...)"` once they’ve served their purpose for condition building.
*   **`MapNamedReturnConstants` + `MapNamedRetAssignConstants`**  
    If a `return` (or `ret = ...;`) is a constant, query `IConstantNameProvider` to replace it with a `SymConst(name)` (e.g., `STATUS_SUCCESS`).  
    The default is `NTSTATUS` (configurable via `ReturnEnumTypeFullName`).
*   **`SimplifyRedundantAssign`**  
    Drops trivial `x = x;` assigns introduced by earlier steps.

All rewriters are **local** and deliberately conservative.

* * *

### ConstantDatabase (symbolic constants & flags)

File: **[`ConstantDatabase.cs`](ConstantDatabase.cs)**

Implements:

```csharp
public interface IConstantNameProvider {
    bool TryFormatValue(string enumFullName, ulong value, out string name);
}
```

**Sources**:

*   `LoadWin32MetadataFromWinmd(path)` — parses **Windows.Win32.winmd** using `System.Reflection.Metadata`.
*   `LoadFromAssembly(asm)` — reflects over .NET enums and static literal fields.

**Features**:

*   Exact value ↔ name map (`ValueToName`).
*   **Flags** support: if the enum is `[Flags]` or “looks like flags” (mostly power‑of‑two members), the formatter can **decompose a value into a `Name1 | Name2 | ...`** representation when it matches a pure OR of known bits.
*   `MapArgEnum(callName, argIndex, enumFullName)` — register **expected enum types for call-site arguments** (e.g., `VirtualProtect(arg2) → PAGE_PROTECTION_FLAGS`).
    > This mapping is wired for future use; current passes focus on **return** constants. It’s straightforward to add a pass that rewrites call arguments with `SymConst` using `TryGetArgExpectedEnumType`.

* * *

### PEReader (minimal PE32+ reader)

File: **[`PEReaderLite.cs`](PEReaderLite.cs)**

A tiny **read‑only** PE32+ parser that’s “just enough” for this tool:

*   Validates `"MZ"` and `"PE\0\0"`, checks PE32+ magic.
*   Parses sections, the **export** directory, and the **import** table.
*   Flags managed images by detecting the .NET metadata directory (CLI header) but does not parse it.
*   `FindExport(name)`:
    *   Scans export name table to find RVA for the symbol,
    *   Detects **forwarders** (RVAs that point inside the export table) → returns forwarder string (`"DLL.Symbol"`).
*   `RvaToOffsetChecked(rva)`:
    *   Maps an RVA to a file offset bounded by the host section,
    *   Throws if outside any section (preventing out‑of‑range reads).

**Limitations by design**:

*   Only **x64 PE (PE32+)**.
*   For **forwarders by ordinal** (`"NTDLL.#123"`), `Program.ParseForwarder` throws `NotSupportedException`. Name‑forwarders are supported and followed up to 8 hops.

* * *

### Program entry point

File: **[`Program.cs`](Program.cs)**

Exposes a library‑friendly method:

```csharp
public static string DisassembleExportToPseudo(
    string dllName,     // e.g., "ntdll.dll"
    string exportName,  // e.g., "RtlGetVersion"
    int maxBytes = 4096)
public static Dictionary<string,string> DisassembleExportsToPseudo(
    string dllName,           // e.g., "ntdll.dll",
    string exportNamePattern, // regex, e.g., "^Rtl.*"
    int maxBytes = 4096)
```

Steps:

1.  Resolve a **System32** path robustly (handles WOW64 via `Sysnative` for 32‑bit processes on 64‑bit OS).
2.  Use `PEReader` to locate the export. If it’s a **forwarder**, follow to the target DLL (up to 8 hops).
3.  Slice bytes from the function start up to the end of its section (capped by `maxBytes`).
4.  Create a `ConstantDatabase`, load Win32 metadata from a local `.winmd` (path is hard‑coded in the sample; adjust).
5.  Run the `Decompiler` with labels, prologue detection, constant mapping enabled.
6.  Emit a small header (source DLL path, export name, image base, function RVA, slice size) + the pretty‑printed pseudocode.

* * *

Key Heuristics
--------------

*   **Calling convention**: Assumed **Microsoft x64** — parameters in `RCX`, `RDX`, `R8`, `R9`; return in `RAX`. The decompiler seeds stable register aliases:
    *   `p1`..`p4` for `RCX`..`R9`,
    *   `ret` for `RAX`,
    *   `fp1`..`fp4` for scalar `XMM0`..`XMM3` (future FP improvements).
*   **Prologue/Epilogue**: Recognizes typical MSVC patterns and collapses them to a single pseudo line while preserving the original asm lines.
*   **Conditions**:
    *   Retains the recent `cmp`/`test` tuple to build the next `jcc` predicate with **signed/unsigned** hints.
    *   Special‑cases `test r,r` to simplify `je/jne` as `r == 0` / `r != 0`.
    *   `bt`/`bts`/`btr`/`btc` produce `CF = bit(x, i)` as a pseudo; `j{b,ae}` immediately after use that fact.
*   **Memory addressing**:
    *   RIP‑relative memory → absolute address constant (helps imports and embedded pointers).
    *   `gs:[0x60]` → introduce `peb` local.
    *   Locals: negative `[rbp - k]` → `&local_k` (for readability); on the stack, `rsp+const` is aliased by the frame clustering pass when we see a memset region.
*   **Library idioms**:
    *   REP string ops → `memcpy`/`memset`.
    *   Zero idioms (`xorps xmm, xmm` followed by 16‑byte stores) → `memset`.
    *   Adjacent `movdqu/movups` load+store **pairs** coalesced to a single `memcpy` (≥ 32 bytes, 16‑byte granularity).
*   **Calls**:
    *   **Near** target → `sub_XXXXXXXX`.
    *   **RIP‑relative mem** target → treat as **IAT**; if `Options.ResolveImportName` is set and returns a symbol, use it.
    *   Heuristic: detect `memset(rcx, edx, r8d)` call‑sites (tiny and pointer‑ish value) and print `memset`.

* * *

Usage Patterns & Extensibility
------------------------------

*   **As a library**: Call `Program.DisassembleExportToPseudo()` for a single export or `Program.DisassembleExportsToPseudo()` with a regex pattern string to process all matching exports.
    Or skip `Program` and use `PEReader` + `Decompiler` directly if you already have bytes.
*   **Change the function under test**: In `Program.Main`, edit the DLL/export name and the `maxBytes` bound. The decompiler will stop at first `RET` anyway.
*   **Constant naming**:
    *   Replace `ReturnEnumTypeFullName` if your target returns something other than `NTSTATUS`.
    *   Implement your own `IConstantNameProvider` or use `ConstantDatabase.LoadFromAssembly()` to feed your own enums.
    *   Future pass idea: use `ConstantDatabase.TryGetArgExpectedEnumType()` to render **call arguments** as symbolic flags.
*   **Improve import name resolution**:
    *   Provide `Options.ResolveImportName = addr => ...` to map IAT addresses to `kernel32!CreateFileW`, etc. (`Program` currently doesn’t wire this in; it’s a one‑liner to add if you maintain your own import table map.)
*   **Add passes**:
    *   The IR is uncomplicated; adding classic DF/SSA/structuring passes is straightforward.
    *   Examples: constant folding, dead‑code elimination, block structuring into `if/while/switch`, value‑set analysis for switch detection, pointer/array typing, var‑recovery and naming.

* * *

Limitations
-----------

*   **Platforms**: PE32+ (x64) only. No 32‑bit support at the moment.
*   **Forwarders**: Name forwarders are supported; **ordinal forwarders** are not (throws).
*   **Types**: Mostly integral types and raw pointers. No full type recovery or signature inference.
*   **Control flow**: Linear IR with labels/gotos; no region structuring beyond trivial `if (cond) goto`.
*   **Heuristics**: Memset/memcpy detection is safe but conservative. Some patterns won’t fold.
*   **Constants**: Return constants are mapped; call‑argument constants are prepared in the database but **not yet** rewritten at call sites.
