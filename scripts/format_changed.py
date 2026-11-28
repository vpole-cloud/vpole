#!/usr/bin/env python3
# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

"""Format selected working-tree files without changing the index or Git settings."""

import argparse
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
PRETTIER_SUFFIXES = {".ts", ".tsx", ".js", ".jsx", ".json", ".md", ".yaml", ".yml", ".css", ".html"}


def git_paths(root, *args):
    output = subprocess.check_output(["git", *args, "-z"], cwd=root)
    return set(filter(None, output.decode().split("\0")))


def selected_paths(root, base):
    changed = git_paths(root, "diff", "--name-only", "--diff-filter=ACMR", base)
    changed |= git_paths(root, "ls-files", "--others", "--exclude-standard")
    return sorted(path for path in changed if (root / path).is_file())


def ensure_not_partially_staged(root, paths):
    staged = git_paths(root, "diff", "--cached", "--name-only")
    unstaged = git_paths(root, "diff", "--name-only")
    mixed = sorted(set(paths) & staged & unstaged)
    if mixed:
        raise ValueError("Refusing to format partially staged files: " + ", ".join(mixed))


def commands(root, paths, write):
    csharp = [path for path in paths if Path(path).suffix == ".cs"]
    prettier = [path for path in paths if Path(path).suffix in PRETTIER_SUFFIXES]
    template_docs = [path for path in paths if path.endswith(".md.tpl")]
    result = []
    if csharp:
        result.append([sys.executable, str(root / "scripts/cleanupcode.py"),
                       "--write" if write else "--check", "--", *csharp])
    if prettier or template_docs:
        executable = root / "src/api/frontend/node_modules/.bin/prettier"
        if not executable.is_file():
            raise ValueError("Install the repository's frontend dependencies before formatting; no packages are downloaded automatically.")
        if prettier:
            result.append([str(executable), "--write" if write else "--check", *prettier])
        if template_docs:
            result.append([str(executable), "--parser", "markdown", "--write" if write else "--check", *template_docs])
    return result


def run(root, base, write, execute=subprocess.run):
    paths = selected_paths(root, base)
    if write:
        ensure_not_partially_staged(root, paths)
    # Resolve every prerequisite before allowing any formatter to write.
    planned = commands(root, paths, write)
    for command in planned:
        result = execute(command, cwd=root, check=False)
        if result.returncode:
            return result.returncode
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="origin/master")
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--write", action="store_true", help="Format the working tree; refuse partially staged files.")
    modes.add_argument("--check", action="store_true", help="Check only (the default).")
    args = parser.parse_args()
    try:
        return run(ROOT, args.base, args.write)
    except (ValueError, subprocess.CalledProcessError, FileNotFoundError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
