#!/usr/bin/env python3
# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

"""Check or apply pinned, formatting-only CleanupCode on selected C# files."""

import argparse
import difflib
import hashlib
import json
import re
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROFILE = "Pole Safe Cleanup"
SETTINGS = "Pole.sln.DotSettings"
TOOL = "jetbrains.resharper.globaltools"
BATCH_CHAR_LIMIT = 20000


def validate_settings(root):
    manifest = json.loads((root / ".config/dotnet-tools.json").read_text())
    tool = manifest["tools"][TOOL]
    version = tool.get("version")
    if (not isinstance(version, str)
            or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version)
            or "jb" not in tool.get("commands", [])):
        raise ValueError("CleanupCode must be pinned in .config/dotnet-tools.json")
    profiles = []
    for entry in ET.parse(root / SETTINGS).getroot():
        if "/CodeCleanup/Profiles/" in entry.get("{http://schemas.microsoft.com/winfx/2006/xaml}Key", ""):
            profile = ET.fromstring(entry.text or "")
            if profile.get("name") == PROFILE:
                profiles.append(profile)
    if len(profiles) != 1:
        raise ValueError(f"Expected one shared {PROFILE!r} profile")
    tasks = list(profiles[0])
    if (profiles[0].attrib != {"name": PROFILE}
            or len(tasks) != 1 or tasks[0].tag != "CSReformatCode"
            or (tasks[0].text or "").strip().lower() != "true"
            or tasks[0].attrib or list(tasks[0])):
        raise ValueError(f"{PROFILE} must enable only CSReformatCode")
    if not (root / ".editorconfig").is_file():
        raise ValueError("The repository .editorconfig is required")
    header = []
    for line in (root / ".editorconfig").read_text(encoding="utf-8-sig").splitlines():
        line = line.strip()
        if line.startswith("["):
            break
        if not line.startswith(("#", ";")):
            header.append(line)
    if not any(re.fullmatch(r"root\s*=\s*true", line, re.IGNORECASE) for line in header):
        raise ValueError("The repository .editorconfig must set root = true")
    return version


def git_paths(root, *args):
    raw = subprocess.check_output(["git", *args, "-z"], cwd=root)
    return set(filter(None, raw.decode().split("\0")))


def ensure_not_partially_staged(root, paths):
    mixed = (set(paths) & git_paths(root, "diff", "--cached", "--name-only")
             & git_paths(root, "diff", "--name-only"))
    if mixed:
        raise ValueError("Refusing to format partially staged files: " + ", ".join(sorted(mixed)))


def selected_files(root, paths):
    result = set()
    for name in paths:
        path = root / name
        if path.is_symlink() or not path.is_file() or path.suffix != ".cs":
            raise ValueError(f"Expected a regular C# source file: {name}")
        relative = path.resolve().relative_to(root.resolve())
        result.add(relative.as_posix())
    return sorted(result)


def editorconfigs(root, paths):
    result = {".editorconfig", SETTINGS, ".config/dotnet-tools.json"}
    if (root / "global.json").is_file():
        result.add("global.json")
    for name in paths:
        parent = Path(name).parent
        while parent != Path("."):
            config = parent / ".editorconfig"
            if (root / config).is_file():
                result.add(config.as_posix())
            parent = parent.parent
    return sorted(result)


def patch_text(before, after):
    result = []
    for path in sorted(after):
        if before[path] == after[path]:
            continue
        lines = difflib.unified_diff(
            before[path].decode("utf-8", errors="replace").splitlines(keepends=True),
            after[path].decode("utf-8", errors="replace").splitlines(keepends=True),
            fromfile="a/" + path, tofile="b/" + path)
        for line in lines:
            result.append(line if line.endswith("\n") else line + "\n\\ No newline at end of file\n")
    return "".join(result)


def batches(paths):
    batch, length = [], 0
    for path in paths:
        if batch and length + len(path) + 3 > BATCH_CHAR_LIMIT:
            yield batch
            batch, length = [], 0
        batch.append(path)
        length += len(path) + 3
    if batch:
        yield batch


def run(root, paths, write=False, execute=subprocess.run):
    root = Path(root).resolve()
    paths = selected_files(root, paths)
    version = validate_settings(root)
    if write:
        ensure_not_partially_staged(root, paths)
    context = editorconfigs(root, paths)
    before = {path: (root / path).read_bytes() for path in paths}
    configuration = {path: (root / path).read_bytes() for path in context}
    output = root / ".artifacts/cleanupcode" / uuid.uuid4().hex
    output.mkdir(parents=True)
    report = {"tool_version": version, "profile": PROFILE, "mode": "write" if write else "check",
              "files": paths, "status": "running", "changed": [],
              "settings_sha256": {p: hashlib.sha256(data).hexdigest() for p, data in configuration.items()}}
    try:
        with tempfile.TemporaryDirectory(prefix="pole-cleanupcode-") as temporary:
            copied = Path(temporary)
            for path, data in {**before, **configuration}.items():
                target = copied / path
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
            # Without a solution CleanupCode can only format. It cannot remove casts,
            # imports, argument names or reorder members, even if another profile drifts.
            options = ["--settings=" + str(copied / SETTINGS), "--profile=" + PROFILE, "--no-updates", "--no-build",
                       "--verbosity=WARN", "--disable-settings-layers=GlobalAll;GlobalPerProduct;"
                       "SolutionShared;SolutionPersonal;ProjectShared;ProjectPersonal"]
            planned = list(batches([str(copied / path) for path in paths]))
            report["batches"] = len(planned)
            print(f"CleanupCode {version}: {len(paths)} C# files, {len(planned)} batches.", flush=True)
            with (output / "tool.log").open("wb") as log:
                for index, batch in enumerate(planned, 1):
                    print(f"CleanupCode batch {index}/{len(planned)}", flush=True)
                    command = ["dotnet", "tool", "run", "jb", "--", "cleanupcode", *batch, *options]
                    result = execute(command, cwd=root, stdout=log, stderr=subprocess.STDOUT, check=False)
                    report["tool_exit_code"] = result.returncode
                    if result.returncode:
                        report["status"] = "tool-failed"
                        diagnostics = (output / "tool.log").read_text(errors="replace").splitlines()[-20:]
                        print("\n".join(diagnostics), file=sys.stderr)
                        print(f"CleanupCode failed; inspect {output / 'tool.log'}.", file=sys.stderr)
                        return result.returncode
            after = {path: (copied / path).read_bytes() for path in paths}
            if any(not (root / p).is_file() or (root / p).is_symlink()
                   or (root / p).read_bytes() != data for p, data in {**before, **configuration}.items()):
                raise ValueError("Sources or formatting settings changed during CleanupCode; retry")
            changed = [path for path in paths if before[path] != after[path]]
            report["changed"] = changed
            if changed:
                (output / "format.patch").write_text(patch_text(before, after), encoding="utf-8")
                if write:
                    ensure_not_partially_staged(root, paths)
                    for path in changed:
                        (root / path).write_bytes(after[path])
                    report["status"] = "formatted"
                else:
                    report["status"] = "needs-formatting"
                print(f"CleanupCode: {len(changed)} C# files {'formatted' if write else 'need formatting'}.")
                for path in changed[:30]:
                    print("  " + path)
                print(f"Patch: {output / 'format.patch'}")
                if not write:
                    print("Apply with python3 scripts/quality_gate.py format, then review and stage the diff.")
                    return 1
            else:
                report["status"] = "passed"
                print(f"CleanupCode: {len(paths)} C# files checked; no formatting changes.")
            return 0
    finally:
        if report["status"] == "running":
            report["status"] = "failed"
        (output / "report.json").write_text(json.dumps(report, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--write", action="store_true")
    modes.add_argument("--check", action="store_true", help="Check temporary copies (the default).")
    parser.add_argument("files", nargs="+")
    args = parser.parse_args()
    try:
        return run(ROOT, args.files, args.write)
    except (ValueError, KeyError, OSError, ET.ParseError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
