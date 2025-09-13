# AGENTS Instructions

## Project Overview
- **Vibe** is an explaining decompiler for native Windows x64 binaries. It lifts machine code to a readable intermediate representation and uses language models to refine the output into C‑style pseudocode with inline commentary.
- Solution projects:
  - `Vibe.Decompiler` – core library and public API.
  - `Vibe.Cui` – console interface mimicking the GUI layout.
  - `Vibe.Gui` – WPF desktop interface for decompiling exports.
  - `Vibe.Utils` – shared helpers.
  - `tests/Vibe.Tests` and `Vibe.Decompiler.Tests` – xUnit test suites.

## Architecture Highlights
- **IR (`IR.cs`)** defines types, expressions, and statements; a pretty printer emits pseudocode with original assembly comments.
- **Engine (`Engine.cs`)** decodes bytes to IR and pseudocode. Options control base address, function naming, label emission, prologue detection, constant mapping, and import resolution.
- **Transformations (`Transformations/`)** apply readability passes such as `FrameObjectClusteringAndRspAlias`, `DropRedundantBitTestPseudo`, `MapNamedReturnConstants`, and `SimplifyRedundantAssign`.
- **ConstantDatabase** maps numeric constants and flags to symbolic names, loading data from Win32 metadata or reflected enums.
- **PeImage** wraps the PeNet library to read sections, imports, exports, and other PE metadata.
- **Program** exposes helper methods like `DisassembleExportToPseudo` and `DisassembleExportsToPseudo` for library or CLI use.

## Development Practices
- Follow the [coding guidelines](docs/coding-guidelines.md): use `var` for obvious types, early returns, underscore‑prefixed private fields, four‑space indentation, PascalCase for types/methods, camelCase for locals, and modern C# features such as pattern matching and expression‑bodied members.
- Line endings are LF and files are UTF‑8 with a final newline (`.editorconfig`).
- Commit messages use a conventional style (e.g., `feat(gui): ...`, `fix:`) and reference pull requests when appropriate.
- Add or update tests when modifying behavior. Place new tests under `tests/` or `Vibe.Decompiler.Tests` matching the project being changed.
- Update documentation in `docs/` when architecture or user‑facing behavior changes.

## Testing
- Run the full test suite before committing: `dotnet test`.
- Tests use xUnit; keep them deterministic and focused on individual passes or helpers rather than complete decompilation output.

## Roadmap & History
- The roadmap in `docs/roadmap.md` tracks milestones such as pipeline hardening, richer IR/CFG work, advanced analysis, and community features.
- Recent commits focus on GUI usability improvements, background decompilation, syntax highlighting modes, and bug fixes in decompilation helpers.

