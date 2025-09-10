# Testing Strategy Proposal

This document outlines a strategy for introducing automated tests into the Vibe project and keeping them maintainable as the code base evolves.

## Test framework
- Adopt **xUnit** as the primary test framework and run it through `dotnet test`.
- xUnit integrates well with the existing .NET tooling, offers rich assertion helpers, and supports parallel runs.
- For property-based testing of transformation invariants, add **FsCheck** or a similar library.

## Making the code testable
- Favor small, focused classes over large static helpers so that behavior can be unit tested.
- Introduce dependency injection or pass interfaces into constructors instead of using global state, which enables mocking in tests.
- Break transformation passes into composable units that can run in isolation, enabling targeted unit tests.
- Ensure I/O such as reading binaries or writing output is abstracted behind interfaces so that tests can supply in‑memory streams.

## Keeping tests stable
- Test individual transformation passes and intermediate representations rather than the final decompiled output, which changes frequently.
- Where output comparisons are necessary, normalize results (e.g., removing whitespace or ordering) before asserting equality.
- Use golden files sparingly and regenerate them only when intentional changes are made, reviewing diffs as part of code review.
- Run deterministic builds in CI to avoid environmental differences.

## What to test with automation
- Unit tests for IR manipulation, constant propagation, and other transformation logic.
- Integration tests that feed small sample binaries through a subset of passes and validate high‑level properties (e.g., no unhandled exceptions, consistent instruction counts).
- Regression tests for bugs that have been fixed to prevent reintroductions.
- Fast performance smoke tests to ensure new passes do not drastically increase run time.

## What not to test
- Full decompiled text output: it churns as passes are added or tweaked and is better suited for manual review or high‑level sanity scripts.
- UI or console formatting, which can vary across environments and adds little confidence.
- Non‑deterministic behaviors such as timing‑dependent code without first enforcing determinism.

## Additional considerations
- Add a `tests` directory with parallel project structure to the main source tree when tests are introduced.
- Configure continuous integration to run `dotnet test` on every pull request.
- Use code coverage tools to highlight untested areas and drive further refactoring.
- Document new test utilities and patterns so contributors can easily follow established practices.

