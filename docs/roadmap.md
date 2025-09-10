# Product Roadmap

This document tracks Vibe's direction.  It lists the work already completed and
the next set of goals.  Items are grouped by dependency rather than calendar so
we can execute quickly.

## Progress Since the Initial Roadmap

The original roadmap was introduced in September 2025.  Since then, several
pull requests have landed that change the shape of the project:

* **#44** – Renamed the solution structure and updated documentation to match
  new naming conventions.
* **#45** – Added an architecture analysis for the transformation class,
  preparing for later pipeline work.
* **#46** – Updated the transformations architecture documentation with clearer
  pass descriptions.
* **#47** – Introduced a minimalist WPF application capable of decompiling DLL
  exports for quick testing.
* **#48** – Produced a code duplication analysis report to guide refactoring and
  future pass development.
* **#49** – Performed a sweeping rename of core types (`Decompiler` → `Engine`,
  `Vibe` → `Vibe.Decompiler`) to normalize namespaces.
* **#50** – Integrated an LLM‑assisted documentation fetcher to refine
  decompiled output with external references.
* **#51** – Refactored the project to implement a formal transformation
  pipeline aligning with proposal docs.
* **#52** – Brought in xUnit‑based test infrastructure and seeded the repository
  with meaningful tests.
* **#53** – Fixed `ObjectDisposedException` in
  `TryDownloadExportDocAsync` and cloned search results to avoid premature
  `JsonDocument` disposal.
* **#54** – Expanded the WPF interface with a tree view for DLLs and exports and
  introduced conditional web access to respect offline scenarios.

These steps provide context for the remaining work.

## Phase 1 – Core Pipeline and Usability

Goals for polishing the core decompilation pipeline and ensuring a smooth
contributor experience.

- [x] Implement structured error handling across translation stages, returning
  `Result` types and surfacing failures in CLI exit codes.
- [x] Add a `vibe` CLI built with `System.CommandLine` to decompile binaries and
  run IR passes; include runnable examples.
- [x] Map call‑argument constants to imported function names using a small
  signature database and resolve common import aliases automatically.
- [x] Establish regression tests covering the pipeline and publish contributor
  docs so new passes always land with tests.  (xUnit infrastructure introduced in
  PR #52.)
- [x] Provide a minimalist WPF app for decompiling DLL exports for exploratory
  usage (PRs #47 and #54).
- [ ] Harden the documentation fetcher with caching and throttling to reduce
  dependency on live web queries.
- [ ] Offer conditional web access flags in both CLI and GUI to fully support
  offline usage.
- [ ] Expand duplicated‑code reports into automated tooling that comments on
  pull requests.
- [ ] Package the core CLI and GUI as downloadable releases with CI‑generated
  artifacts.

## Phase 2 – IR and Control Flow Analysis

The next stage focuses on richer intermediate representations and restructuring
control flow.

- [ ] Introduce an SSA‑based IR and add constant folding and dead‑code
  elimination passes.
- [ ] Build a control‑flow graph and transform it into `if`/`while`/`switch`
  constructs for readable output.
- [ ] Support PE32 binaries by implementing a 32‑bit loader, unifying 32/64‑bit
  pipelines and running tests on Windows and Linux.
- [ ] Surface IR dumps and CFG visualizations directly in the GUI for rapid
  debugging.
- [ ] Automate regression tests that verify CFG transformations across a suite
  of sample binaries.
- [ ] Investigate tail‑call and exception‑handling patterns for inclusion in IR
  lowering.
- [ ] Document the IR specification and transformation pipeline for plugin
  authors.

## Phase 3 – Advanced Analysis and Extensibility

Once the IR stabilizes, deeper analysis and extensibility features can be
layered on.

- [ ] Implement type propagation to infer pointer and array shapes and recover
  function signatures, feeding better variable names.
- [ ] Expose a plugin API for custom analysis passes and load them dynamically;
  start a minimal GUI or language‑server frontend on top of the same backend.
- [ ] Integrate a symbolic execution engine and value‑set analysis to approach
  human‑quality decompilation.
- [ ] Explore hybrid fuzzing and decompilation to recover opaque constructs.
- [ ] Allow plugins to contribute new naming heuristics and external symbol
  providers.
- [ ] Provide scripting support for batch analyses and automated report
  generation.
- [ ] Offer extension points for alternate frontends (e.g., a VS Code extension)
  that reuse the core engine.

## Phase 4 – Ecosystem and Community

To turn Vibe into a sustainable project, community and ecosystem features are
essential.

- [ ] Establish contribution guidelines and a code of conduct.
- [ ] Publish design write‑ups for major components and maintain an architecture
  decision log.
- [ ] Create sample repositories and tutorials that show off the CLI and GUI in
  realistic workflows.
- [ ] Set up automated benchmarking to track decompilation performance across
  releases.
- [ ] Integrate GitHub Actions or similar CI to run tests and produce nightly
  builds.
- [ ] Host public issue triage and roadmap review sessions to prioritize
  community needs.
- [ ] Provide a website with searchable documentation, including generated API
  references.
- [ ] Encourage third‑party plugin development via contests or featured
  showcases.

This roadmap will continue to evolve as the project matures.  Contributions that
advance these milestones are very welcome.

