# Usage Patterns & Extensibility

- **As a library**: Call `Program.DisassembleExportToPseudo()` for a single export or `Program.DisassembleExportsToPseudo()` with a regex pattern string to process all matching exports. You can also use `PEReader` + `Engine` directly if you already have bytes.
- **Change the function under test**: In `Program.Main`, edit the DLL/export name and the `maxBytes` bound. The decompiler stops at the first `RET`.
- **Constant naming**:
  - Replace `ReturnEnumTypeFullName` if your target returns something other than `NTSTATUS`.
  - Implement your own `IConstantNameProvider` or use `ConstantDatabase.LoadFromAssembly()` to feed your own enums.
  - Future pass idea: use `ConstantDatabase.TryGetArgExpectedEnumType()` to render call arguments as symbolic flags.
- **Improve import name resolution**: Provide `Options.ResolveImportName = addr => ...` to map IAT addresses to symbols like `kernel32!CreateFileW`.
- **Add passes**: The IR is uncomplicated; adding classic DF/SSA/structuring passes is straightforward. Examples include constant folding, dead-code elimination, block structuring into `if/while/switch`, value-set analysis for switch detection, pointer/array typing, and variable recovery/naming.
