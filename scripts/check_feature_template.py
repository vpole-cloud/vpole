#!/usr/bin/env python3
"""Build and test a generated feature with normal production roles in an owned source snapshot."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

from create_feature import create_feature
from quality_gate import GateError, MSBUILD, ROOT, read_trx, source_fingerprint

FEATURE = "FeatureTemplateProbe"
RESOURCE = "TemplateEntry"


def snapshot_sources(destination):
    paths = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=ROOT
    ).decode().split("\0")
    for name in sorted(set(paths) - {""}):
        source = ROOT / name
        if not source.is_file() and not source.is_symlink():
            continue
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target, follow_symlinks=False)


def wire_snapshot(root):
    feature = create_feature(FEATURE, RESOURCE, "feature-template-probe", root=root,
                             template=root / "templates/feature")
    projects = sorted(feature.glob("*/*.csproj"))
    architecture = root / "tests/Pole.Tests.Architecture/Pole.Tests.Architecture.csproj"
    # Register the generated feature's ordinary role, exactly as a new production feature requires.
    roles = root / "tests/Pole.Tests.Architecture/ModuleBoundaryArchitectureTests.cs"
    text = roles.read_text()
    marker = '["ComputeProfiles"] = FeatureRole.Platform,'
    if text.count(marker) != 1:
        raise GateError("Architecture role registration changed; update the template validation wiring.")
    roles.write_text(text.replace(marker, f'["{FEATURE}"] = FeatureRole.Platform,\n            ' + marker))
    solution = ET.parse(root / "Pole.slnx")
    for project in projects:
        ET.SubElement(solution.getroot(), "Project", Path=project.relative_to(root).as_posix())
    solution.write(root / "Pole.slnx")
    selected = ET.Element("Solution")
    for project in [*projects, architecture]:
        ET.SubElement(selected, "Project", Path=project.relative_to(root).as_posix())
    ET.ElementTree(selected).write(root / "FeatureTemplate.slnx")
    return [feature / f"{FEATURE}.Tests/{FEATURE}.Tests.csproj", architecture,
            feature / f"{FEATURE}.Tests.Integration/{FEATURE}.Tests.Integration.csproj"]


def validation_environment(root=ROOT, inherited=None):
    environment = dict(os.environ if inherited is None else inherited)
    git_directory = subprocess.check_output(
        ["git", "rev-parse", "--absolute-git-dir"], cwd=root, text=True).strip()
    common_directory = subprocess.check_output(
        ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=root, text=True).strip()
    environment.setdefault("POLE_TEST_WORKTREE_ID", hashlib.sha256(git_directory.encode()).hexdigest()[:12])
    environment.setdefault("POLE_TEST_RUN_ID", uuid.uuid4().hex[:12])
    state = Path(common_directory).parent / ".tmp/kubernetes-lab/state.json"
    if state.is_file():
        environment.setdefault("POLE_K8S_LAB_STATE", str(state))
    return environment


def execute(name, command, root, directory, environment=None):
    log = directory / f"{name}.log"
    print(f"Template {name}; log: {log}", flush=True)
    with log.open("w") as output:
        result = subprocess.run(command, cwd=root, env=environment, stdout=output, stderr=subprocess.STDOUT)
    if result.returncode:
        print("\n".join(log.read_text().splitlines()[-35:]), flush=True)
        raise GateError(f"Generated feature {name} failed; see {log}")


def check_format(root, directory, sources):
    execute("tool-restore", ["dotnet", "tool", "restore"], root, directory)
    execute("format", [sys.executable, "scripts/cleanupcode.py", "--check",
                       *(source.relative_to(root).as_posix() for source in sources)], root, directory)


def check(directory):
    directory = directory.resolve()
    directory.mkdir(parents=True, exist_ok=False)
    fingerprint = source_fingerprint()
    root = directory / "repository"
    report = {"status": "running", "fingerprint": fingerprint, "tests": {}}
    try:
        snapshot_sources(root)
        projects = wire_snapshot(root)
        environment = validation_environment()
        report["worktree_id"] = environment["POLE_TEST_WORKTREE_ID"]
        report["run_id"] = environment["POLE_TEST_RUN_ID"]
        execute("restore", ["dotnet", "restore", "FeatureTemplate.slnx", *MSBUILD], root, directory)
        execute("build", ["dotnet", "build", "FeatureTemplate.slnx", "--no-restore", *MSBUILD], root, directory)
        feature = root / "src/features" / FEATURE
        sources = sorted(path for path in feature.rglob("*.cs")
                         if not {"bin", "obj"} & set(path.relative_to(feature).parts))
        for source in sources:
            for number, line in enumerate(source.read_text().splitlines(), 1):
                if len(line) > 120:
                    raise GateError(f"Generated C# exceeds 120 characters: {source.relative_to(root)}:{number}")
        check_format(root, directory, sources)
        for index, project in enumerate(projects):
            results = directory / f"tests-{index}"
            execute(f"tests-{index}", ["dotnet", "test", str(project), "--no-build", "--no-restore",
                    "--nologo", "-maxcpucount:1", "-nodeReuse:false", "--collect:Code Coverage;Format=Cobertura",
                    "--logger", "trx;LogFileName=results.trx", "--results-directory", str(results)],
                    root, directory, environment)
            report["tests"][project.stem] = read_trx(results / "results.trx")
            coverage = list(results.rglob("*.cobertura.xml"))
            if not coverage or any(ET.parse(path).getroot().tag != "coverage" for path in coverage):
                raise GateError(f"Generated feature coverage missing or invalid for {project.stem}")
        if fingerprint != source_fingerprint():
            raise GateError("Template or repository inputs changed during validation.")
        report["status"] = "passed"
    except BaseException:
        report["status"] = "failed"
        raise
    finally:
        (directory / "result.json").write_text(json.dumps(report, indent=2) + "\n")
    print("Generated feature build, formatting, architecture, unit and Mongo/API tests passed with zero skips.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-directory", type=Path,
                        default=ROOT / ".artifacts/feature-template" / str(time.time_ns()))
    args = parser.parse_args()
    try:
        check(args.results_directory)
    except (GateError, OSError, ValueError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
