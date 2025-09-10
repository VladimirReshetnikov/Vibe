#!/usr/bin/env python3
"""
Reformat all tracked text files to:
  - UTF-8 (strip BOM if present; optionally recode from a given legacy encoding)
  - LF newlines
  - no trailing whitespace
  - one final newline at EOF
  - spaces for indentation (indent_size=4); by default only leading tabs -> spaces

Usage:
  python3 scripts/reformat_lf.py
    Rewrites files in-place.

  python3 scripts/reformat_lf.py --check
    Dry run. Exit code 1 if changes are needed.

  python3 scripts/reformat_lf.py --staged --restage
    Only process currently staged files, and re-stage if modified (useful in pre-commit).

  python3 scripts/reformat_lf.py --aggressive-tabs
    Replace ALL tabs with spaces (not just leading). Makefile-like files are still skipped.

  python3 scripts/reformat_lf.py --recode-from cp1251
    Attempt to recode non-UTF-8 files from the specified encoding into UTF-8.

Notes:
- Makefiles require literal tabs; we skip tab conversion for: Makefile, makefile, GNUmakefile, *.mk, *.mak.
- We only touch files Git tracks. Binary files are skipped.
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path
from typing import Iterable

# ----------------------------- configuration ---------------------------------

INDENT_SIZE = 4

MAKEFILE_NAMES = {"Makefile", "makefile", "GNUmakefile"}
MAKEFILE_EXTS = {".mk", ".mak"}

BINARY_EXTS = {
    ".png",".jpg",".jpeg",".gif",".bmp",".ico",".pdf",".zip",".gz",".bz2",".xz",".7z",
    ".rar",".jar",".war",".ear",".class",".exe",".dll",".pdb",".so",".dylib",".a",".o",
    ".obj",".bin",".psd",".ai",".sketch",".blend",".fbx",".glb",".glTF",".otf",".ttf",
    ".woff",".woff2",".eot",".wasm",".mp3",".mp4",".mov",".avi",".mkv",".webm",".iso"
}

# ------------------------------- helpers --------------------------------------

def run(cmd: list[str]) -> bytes:
    return subprocess.check_output(cmd)

def repo_root() -> Path:
    return Path(run(["git", "rev-parse", "--show-toplevel"]).decode().strip())

def list_tracked_files(staged: bool) -> list[Path]:
    if staged:
        out = run(["git", "diff", "--name-only", "--cached", "-z"])
    else:
        out = run(["git", "ls-files", "-z"])
    items = [p for p in out.split(b"\x00") if p]
    return [Path(x.decode("utf-8", "surrogateescape")) for x in items]

def looks_binary_bytes(sample: bytes) -> bool:
    return b"\x00" in sample

def is_potentially_text(path: Path, head: bytes) -> bool:
    if path.suffix.lower() in BINARY_EXTS:
        return False
    return not looks_binary_bytes(head)

def is_makefile_like(path: Path) -> bool:
    return (path.name in MAKEFILE_NAMES) or (path.suffix.lower() in MAKEFILE_EXTS)

def normalize_text(
    s: str,
    *,
    indent_size: int = INDENT_SIZE,
    convert_tabs: bool = True,
    aggressive_tabs: bool = False,
    keep_tabs: bool = False,
) -> str:
    # Normalize newlines
    s = s.replace("\r\n", "\n").replace("\r", "\n")

    lines = s.split("\n")
    out_lines = []
    for line in lines:
        # Leading tabs -> spaces (unless we must keep tabs, e.g. Makefiles)
        if convert_tabs and not keep_tabs:
            if aggressive_tabs:
                line = line.replace("\t", " " * indent_size)
            else:
                i = 0
                while i < len(line) and line[i] in (" ", "\t"):
                    i += 1
                leading = line[:i].replace("\t", " " * indent_size)
                line = leading + line[i:]

        # Trim trailing whitespace
        line = line.rstrip(" \t")
        out_lines.append(line)

    text = "\n".join(out_lines)

    # Ensure exactly one trailing newline
    if not text.endswith("\n"):
        text += "\n"
    return text

# --------------------------------- main ---------------------------------------

def process_file(
    path: Path,
    *,
    recode_from: str | None,
    aggressive_tabs: bool,
    check_only: bool,
    restage: bool,
) -> bool:
    # Read as bytes
    try:
        raw = path.read_bytes()
    except Exception:
        return False  # unreadable (symlink, permission, etc.)

    if not is_potentially_text(path, raw[:8192]):
        return False

    # Try UTF-8 / UTF-8-sig first
    decoded: str | None = None
    try:
        decoded = raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        if recode_from:
            try:
                decoded = raw.decode(recode_from)
            except UnicodeDecodeError:
                # Not safely recodable; skip
                return False
        else:
            return False

    keep_tabs = is_makefile_like(path)
    new_text = normalize_text(
        decoded,
        indent_size=INDENT_SIZE,
        convert_tabs=True,
        aggressive_tabs=aggressive_tabs,
        keep_tabs=keep_tabs,
    )

    new_bytes = new_text.encode("utf-8")

    if new_bytes != raw:
        if check_only:
            print(f"NEEDS-FIX {path}")
            return True
        try:
            path.write_bytes(new_bytes)
        except Exception as e:
            print(f"ERROR writing {path}: {e}", file=sys.stderr)
            return False
        if restage:
            subprocess.call(["git", "add", str(path)])
        print(f"Fixed {path}")
        return True

    return False


def main(argv: Iterable[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="dry run; exit 1 if changes would be made")
    ap.add_argument("--staged", action="store_true", help="only process staged files")
    ap.add_argument("--restage", action="store_true", help="re-stage modified files (use with --staged in pre-commit)")
    ap.add_argument("--recode-from", metavar="ENC", help="attempt to recode non-UTF-8 files from ENC to UTF-8 (e.g., cp1251)")
    ap.add_argument("--aggressive-tabs", action="store_true", help="replace ALL tabs, not only leading")
    args = ap.parse_args(list(argv) if argv is not None else None)

    # Ensure we're inside a Git repo
    try:
        root = repo_root()
    except subprocess.CalledProcessError:
        print("Error: not inside a Git repository.", file=sys.stderr)
        return 2

    os.chdir(root)

    files = list_tracked_files(staged=args.staged)
    if not files:
        return 0

    any_changes = False
    for p in files:
        if str(p).startswith(".git/"):
            continue
        changed = process_file(
            p,
            recode_from=args.recode_from,
            aggressive_tabs=args.aggressive_tabs,
            check_only=args.check,
            restage=args.restage,
        )
        any_changes = any_changes or changed

    if args.check and any_changes:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
