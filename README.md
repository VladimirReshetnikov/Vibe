# Vibe — x64 PE export → C-like pseudocode

Vibe locates an exported function in a 64-bit Windows DLL, lifts its machine code to a small IR, applies readability passes, and prints C-style pseudocode alongside the original assembly. It can optionally refine the output with an LLM for more human-like C code.

The eventual goal is to create a high-fidelity decompiler into C code comparable with human-written code.

This project is licensed under the [MIT-0 license](LICENSE).

## Quick Start

### Prerequisites
- Windows x64
- .NET SDK 8.0 (project targets `net8.0`)

### Build
```bash
dotnet build -c Release
```

### Run
`Program.cs` calls:
```csharp
var disasm = DisassembleExportToPseudo(
    "C:\\Windows\\System32\\Microsoft-Edge-WebView\\msedge.dll",
    "CreateTestWebClientProxy");
Console.WriteLine(disasm);
```
If an `OPENAI_API_KEY` or `ANTHROPIC_API_KEY` environment variable is set, the tool sends the pseudocode to that provider and prints a refined version that reads much closer to hand-written C. Example output lives in [docs/examples.md](docs/examples.md).

## Features
- Original assembly printed as comments with absolute addresses.
- C-like pseudocode with conservative types and basic readability passes.
- Optional LLM refinement for more natural code.

## Limitations
- PE32+ (x64) only.
- Forwarders by ordinal are not supported.
- Linear IR with labels/gotos; no region structuring beyond trivial `if (cond) goto`.

## Documentation
- [Architecture](docs/architecture.md)
- [Key heuristics](docs/heuristics.md)
- [Usage & extensibility](docs/usage.md)
- [Examples](docs/examples.md)
- [Roadmap](docs/roadmap.md)

## Contributing
See [docs/coding-guidelines.md](docs/coding-guidelines.md) for coding conventions and formatting rules.
