# Vibe — The Explaining Decompiler for Native Code

Vibe aims to turn opaque machine instructions into more readable stories. Currently supporting only x64 Windows binaries, it fuses lifting algorithms with AI refinement so you don't just see the code—you understand it.

> Vibe is a quickly vibe-coded prototype with rough edges and a limited feature set. We find its capability impressive, but it's experimental software: it only supports x64 binaries, may misinterpret code, and we don't promise any particular results.

## Why Vibe?
- **Explains as it decompiles.** Vibe narrates the intent of native code, emitting C‑style pseudocode and assembly side‑by‑side, though the interpretation might not always be accurate.
- **Combines algorithms and AI for code analysis.** The IR engine reconstructs control flow, and language models help refine the output into readable C-like pseudocode, but manual review is still required.
- **Intended for exploratory code analysis.** Useful for auditing third‑party libraries or exploring system internals, but the output can be incomplete or incorrect.

## Limitations
- Supports only PE32+ (x64) binaries.
- Forwarders by ordinal are not supported.
- Emits a linear IR with labels and gotos; region structuring is minimal.
- Output may be incomplete or inaccurate; verify against the original binaries.
- We don't guarantee any particular results.

## See it in action
Below is real output produced by Vibe for `MakeSureDirectoryPathExists` from `dbghelp.dll`:

```c
#include <windows.h>
#include <string.h>
#include <malloc.h>
#include <ctype.h>

/* Helper to check for Windows path separators */
static int is_slash(char c)
{
    return c == '\\' || c == '/';
}

/* Ensure that all directories along the given path exist.
   The parameter is expected to be a path to a file; the function creates
   intermediate directories up to the final separator. If the input ends with
   a separator, the final directory is created as well. */
BOOL WINAPI MakeSureDirectoryPathExists(PCSTR path)
{
    if (!path || !*path) return FALSE;

    size_t len = strlen(path);
    char *buf = (char*)_malloca(len + 1);
    if (!buf) return FALSE;

    memcpy(buf, path, len + 1);

    /* Normalize: we don't strictly need to convert forward slashes, but we treat both as separators. */
    /* Determine the position after the root we should start creating from. */
    char *p = buf;

    if (is_slash(p[0]) && is_slash(p[1]))
    {
        /* UNC path: \\server\share\... -> skip \\server\share */
        p += 2;                           /* past leading \\ */
        while (*p && !is_slash(*p)) p++;  /* server */
        if (*p == '\0') { _freea(buf); return TRUE; }
        p++;                               /* past backslash */
        while (*p && !is_slash(*p)) p++;  /* share */
        /* 'p' now points at the slash after share (or end) */
    }
    else if (isalpha((unsigned char)p[0]) && p[1] == ':')
    {
        /* Drive path: C:\... or C:relative */
        p += 2; /* skip drive letter and colon */
        if (is_slash(*p)) p++; /* skip root slash if present (C:\) */
    }

    /* Iterate over the path and create directories at each separator boundary,
       skipping the root portion handled above. */
    BOOL ok = TRUE;
    for (char *q = buf; *q; ++q)
    {
        if (!is_slash(*q)) continue;
        if (q <= p) continue; /* don't attempt to create the drive/UNC root */

        char save = *q;
        *q = '\0';

        if (!CreateDirectoryA(buf, NULL))
        {
            DWORD err = GetLastError();
            if (err != ERROR_ALREADY_EXISTS)
            {
                ok = FALSE;
                *q = save;
                break;
            }

            /* If it already exists, ensure it's actually a directory */
            DWORD attrs = GetFileAttributesA(buf);
            if (attrs == INVALID_FILE_ATTRIBUTES || !(attrs & FILE_ATTRIBUTE_DIRECTORY))
            {
                ok = FALSE;
                *q = save;
                break;
            }
        }

        *q = save;
    }

    if (ok)
    {
        /* If the input ends with a separator, ensure the final directory exists too. */
        size_t blen = strlen(buf);
        if (blen && is_slash(buf[blen - 1]))
        {
            /* Trim trailing separators */
            size_t i = blen;
            while (i > 0 && is_slash(buf[i - 1])) i--;

            if (i > 0)
            {
                char save = buf[i];
                buf[i] = '\0';

                if (!CreateDirectoryA(buf, NULL))
                {
                    DWORD err = GetLastError();
                    if (err != ERROR_ALREADY_EXISTS)
                    {
                        ok = FALSE;
                    }
                    else
                    {
                        DWORD attrs = GetFileAttributesA(buf);
                        if (attrs == INVALID_FILE_ATTRIBUTES || !(attrs & FILE_ATTRIBUTE_DIRECTORY))
                            ok = FALSE;
                    }
                }

                buf[i] = save;
            }
        }
    }

    _freea(buf);
    return ok;
}
```

## Getting Started
TODO

If `OPENAI_API_KEY` or `ANTHROPIC_API_KEY` is set, Vibe will refine the pseudocode with the corresponding provider.

## Documentation
- [Architecture](docs/architecture.md)
- [Key heuristics](docs/heuristics.md)
- [Usage & extensibility](docs/usage.md)
- [Examples](docs/examples.md)
- [Roadmap](docs/roadmap.md)

## License
This project is licensed under the [MIT-0 license](LICENSE). 
