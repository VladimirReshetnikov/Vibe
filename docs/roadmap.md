# Product Roadmap

This document outlines an aggressive development path for Vibe. Work items are
grouped by dependency rather than timeline so we can execute quickly.

## Phase 1 – Core Pipeline and Usability

* Implement structured error handling across all translation stages, returning
  `Result` types and bubbling failures to the CLI exit code.
* Add a `vibe` CLI built with `System.CommandLine` to decompile binaries and
  run IR passes; include runnable examples.
* Map call‑argument constants to imported function names using a small
  signature database and resolve common import aliases automatically.
* Establish regression tests covering the pipeline and publish contributor docs
  so new passes always land with tests.

## Phase 2 – IR and Control Flow Analysis

* Introduce an SSA‑based IR and add constant folding and dead‑code elimination
  passes.
* Build a control‑flow graph and transform it into `if`/`while`/`switch`
  constructs for readable output.
* Support PE32 binaries by implementing a 32‑bit loader, unifying 32/64‑bit
  pipelines and running tests on Windows and Linux.

## Phase 3 – Advanced Analysis and Extensibility

* Implement type propagation to infer pointer and array shapes and recover
  function signatures, feeding better variable names.
* Expose a plugin API for custom analysis passes and load them dynamically;
  start a minimal GUI or language‑server frontend on top of the same backend.
* Integrate a symbolic execution engine and value‑set analysis to approach
  human‑quality decompilation.

This roadmap will evolve quickly—contributions that deliver these steps are
welcome.
