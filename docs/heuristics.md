# Key Heuristics

- **Calling convention**: Assumes Microsoft x64 — parameters in `RCX`, `RDX`, `R8`, `R9`; return in `RAX`. Register aliases `p1`…`p4`, `ret`, and `fp1`…`fp4` are seeded for readability.
- **Prologue/Epilogue**: Recognizes typical MSVC patterns and collapses them to a single pseudo line while preserving the original assembly lines.
- **Conditions**:
  - Retains the recent `cmp`/`test` tuple to build the next `jcc` predicate with signed/unsigned hints.
  - Special-cases `test r,r` to simplify `je/jne` as `r == 0` / `r != 0`.
  - `bt`/`bts`/`btr`/`btc` produce `CF = bit(x, i)` pseudo notes used by subsequent `j{b,ae}`.
- **Memory addressing**:
  - RIP-relative memory → absolute address constants (helps imports and embedded pointers).
  - `gs:[0x60]` introduces a local `peb` pointer.
  - Stack slots `[rbp - k]` become `&local_k`; `rsp+const` is aliased by the frame clustering pass when a memset zeros a region.
- **Library idioms**:
  - REP string ops → `memcpy`/`memset`.
  - Zero idioms (`xorps xmm, xmm` followed by 16-byte stores) → `memset`.
  - Adjacent `movdqu/movups` load+store pairs coalesce into a single `memcpy` (>=32 bytes, 16-byte granularity).
- **Calls**:
  - Near targets become `sub_XXXXXXXX`.
  - RIP-relative memory targets are treated as IAT entries; `Options.ResolveImportName` can provide symbols.
  - Heuristic: detect `memset(rcx, edx, r8d)` call-sites and print `memset`.
