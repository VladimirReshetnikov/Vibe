# Supplementary Context for LLM-Assisted Decompilation

## Overview
Large language models perform better at refining a rough decompilation when they receive
additional cues about the original binary.  The information below can be collected by a
host application and supplied alongside the preliminary function listing.

## Information From the DLL
- **Disassembly and metadata** – raw IL or assembly, calling convention, return type,
  attributes, visibility, export name or ordinal.
- **Type information** – definitions of structs, classes, and enums referenced by the
  function, including field offsets and sizes.
- **Cross references** – callers, callees, global variables, and imported APIs that
  interact with the function.
- **Constants and strings** – embedded literals, magic numbers, error codes, format
  strings, and RTTI data that hint at semantics.
- **Control-flow and exceptions** – loop and switch structure, try/catch blocks,
  stack frame layout, and optimization hints.
- **Debug artifacts** – symbol names from PDBs, source file paths, line numbers, and
  build timestamps.

## Information From the Web
- **Official documentation** – API references or manuals for any imported libraries or
  system calls observed in the function.
- **Open-source analogs** – implementations of similarly named routines on GitHub or
  other public repositories to infer intent or algorithm details.
- **Standards and specifications** – RFCs, protocol descriptions, or file-format specs
  related to constants or strings found in the binary.
- **Community discussions** – blog posts, forum threads, or Q&A entries that describe
  typical usage patterns for the API set or algorithm.
- **Error code and identifier lookups** – online tables mapping numeric values to
  meanings, such as HRESULT or errno catalogs.

## Guidance for Prompt Construction
When prompting the LLM:
1. Present the decompiled function first, then append the additional context.
2. Keep each context block concise and label its origin (e.g., `Disassembly`,
   `String references`, `Docs`).
3. Include citations or URLs for any web-sourced material.
4. Highlight relationships or hypotheses you want the model to verify.
5. Provide a target style or language if a specific output format is desired.

