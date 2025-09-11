# Architecture

## IR (types, expressions, statements, printer)
File: [Vibe.Decompiler/IR.cs](../Vibe.Decompiler/IR.cs)

- **Types (`IrType`)**: `IntType`, `FloatType`, `PointerType`, `VectorType`, `UnknownType`, `VoidType`.
  The pretty printer favors stdint names like `uint8_t` and `uint16_t`.
- **Expressions (`Expr`)**: registers, locals, loads/stores, constants (`Const`/`UConst`/`SymConst`), binary/unary ops, casts, calls (`CallExpr`), intrinsics (`rotl`/`rotr`), ternary, label refs.
- **Statements (`Stmt`)**: assignment, memory store, call, `if (cond) goto Lx`, `goto`, label, `return`, `AsmStmt` (original text), `PseudoStmt`, `Nop`.
- **High-level nodes (optional)**: placeholders for future structuring (`IfNode`, `WhileNode`, `SwitchNode`, ...). The pretty printer currently emits block/linear form.
- **Pretty printer**:
  - Emphasizes transparency by emitting the assembly comment line for every decoded instruction with its IP.
  - Options include header comments, block labels, signedness hints, stdint names, and indentation string.

## Engine (decode → IR)
File: [Vibe.Decompiler/Engine.cs](../Vibe.Decompiler/Engine.cs)

```csharp
var engine = new Engine();
ReadOnlyMemory<byte> bytes = ...;
var pseudo = engine.ToPseudoCode(bytes, new Engine.Options { ... });
```

Options:
- `BaseAddress` — assumed function base for RIP-relative and label IPs.
- `FunctionName` — pretty name in the header (`kernelbase.dll!Foo`).
- `EmitLabels` — include `L1`, `L2`, ... for branch targets.
- `DetectPrologue` — hide low-level stack boilerplate behind a pseudo line while still printing raw asm.
- `CommentCompare` — print pseudo lines for `cmp`/`test`.
- `MaxBytes` — decode limit (stop earlier on `ret`).
- `ResolveImportName(ulong iatAddr)` — optional callback to name indirect calls fetched via RIP-relative memory (IAT slots).
- Constants: `ConstantProvider` (defaults to `ConstantDatabase`) and `ReturnEnumTypeFullName` (defaults to `Windows.Win32.Foundation.NTSTATUS`) enable symbolic return constants.

Major steps inside `ToPseudoCode`:
1. Decode with Iced (64-bit mode) until `RET` or `MaxBytes`.
2. Detect prologue/locals.
3. Detect well-known constructs like `peb`.
4. Collect in-range branch targets and map them to labels.
5. Translate instructions to IR and apply heuristics.
6. Run readability passes and pretty print the IR.

## Transformations (readability passes)
Directory: [Vibe.Decompiler/Transformations](../Vibe.Decompiler/Transformations)

- `FrameObjectClusteringAndRspAlias`
  - Finds `memset((void*)(rsp + K), 0, N)` and creates a local pointer `frame_0xK` initialized to `(uint8_t*)(rsp + K)`.
  - Rewrites occurrences within that range so stack object accesses use `frame_0x...` instead of raw `rsp` offsets.
- `DropRedundantBitTestPseudo` — removes internal pseudo notes like `"CF = bit(...)"` once they’re no longer needed.
- `MapNamedReturnConstants` and `MapNamedRetAssignConstants` — replace constant return values with symbolic names when available.
- `SimplifyRedundantAssign` — drops trivial `x = x;` assigns introduced by earlier steps.

All rewriters are local and deliberately conservative.

## ConstantDatabase (symbolic constants & flags)
File: [Vibe.Decompiler/ConstantDatabase.cs](../Vibe.Decompiler/ConstantDatabase.cs)

Implements:
```csharp
public interface IConstantNameProvider {
    bool TryFormatValue(string enumFullName, ulong value, out string name);
}
```

Sources:
- `LoadWin32MetadataFromWinmd(path)` — parses `Windows.Win32.winmd` using `System.Reflection.Metadata`.
- `LoadFromAssembly(asm)` — reflects over .NET enums and static literal fields.

Features:
- Exact value↔name map (`ValueToName`).
- Flags support: the formatter can decompose a value into `Name1 | Name2 | ...` when it matches a pure OR of known bits.
- `MapArgEnum(callName, argIndex, enumFullName)` — register expected enum types for call-site arguments.

## PEReader (minimal PE32+ reader)
File: [Vibe.Decompiler/PEReaderLite.cs](../Vibe.Decompiler/PEReaderLite.cs)

A tiny read-only PE32+ parser that’s just enough for this tool:
- Validates headers and parses sections, the export directory, and the import table.
- Flags managed images by detecting the .NET metadata directory but does not parse it.
- `FindExport(name)` scans the export name table and detects forwarders.
- `RvaToOffsetChecked(rva)` maps an RVA to a file offset within its section.

Limitations by design:
- Only x64 PE (PE32+).
- For forwarders by ordinal ("NTDLL.#123"), `Program.ParseForwarder` throws `NotSupportedException`. Name-forwarders are supported and followed up to 8 hops.

## Program entry point
File: [Vibe.Decompiler/Program.cs](../Vibe.Decompiler/Program.cs)

Provides library-friendly methods:
```csharp
public static string DisassembleExportToPseudo(
    string dllName,
    string exportName,
    int maxBytes = 4096)
public static Dictionary<string,string> DisassembleExportsToPseudo(
    string dllName,
    string exportNamePattern,
    int maxBytes = 4096)
```

Steps:
1. Resolve a System32 path (handles WOW64 via `Sysnative`).
2. Use `PEReader` to locate the export and follow forwarders up to eight hops.
3. Slice bytes from the function start up to the end of its section (capped by `maxBytes`).
4. Create a `ConstantDatabase` and load Win32 metadata from a local `.winmd` if available.
5. Run the `Engine` with labels, prologue detection, and constant mapping enabled.
6. Emit a header with DLL path, export name, image base, function RVA, and slice size, followed by the pretty-printed pseudocode.
