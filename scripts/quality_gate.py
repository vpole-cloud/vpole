#!/usr/bin/env python3
"""Profiled local and CI gate. Selected suites never accept filters or skipped tests."""

import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / ".artifacts" / "quality"
RECEIPT = ARTIFACTS / "receipt.json"
MSBUILD = ["--disable-build-servers", "--nologo", "-maxcpucount:1", "-nodeReuse:false"]


class GateError(RuntimeError):
    pass


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT).decode().strip()


def source_fingerprint(root=ROOT):
    names = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=root
    ).decode().split("\0")
    digest = hashlib.sha256()
    for name in sorted(set(names) - {""}):
        path = root / name
        # Hash the tested filesystem tree, independent of staging deleted paths.
        if not path.is_symlink() and not path.is_file():
            continue
        digest.update(name.encode() + b"\0")
        if path.is_symlink():
            digest.update(b"link\0" + os.readlink(path).encode())
        elif path.is_file():
            digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def test_projects(root=ROOT):
    solution = ET.parse(root / "Pole.slnx")
    included = {p.attrib["Path"].replace("\\", "/") for p in solution.iter("Project")}
    projects = []
    for area in ("src", "tests"):
        for path in (root / area).rglob("*.*proj"):
            if path.suffix not in (".csproj", ".fsproj") or {"bin", "obj"} & set(path.parts):
                continue
            xml = ET.parse(path)
            is_test = any(p.get("Include") == "Microsoft.NET.Test.Sdk" for p in xml.iter("PackageReference"))
            is_test |= any((p.text or "").strip().lower() == "true" for p in xml.iter("IsTestProject"))
            if is_test:
                relative = path.relative_to(root).as_posix()
                if relative not in included:
                    raise GateError(f"Test project is missing from Pole.slnx: {relative}")
                projects.append(relative)
    if not projects:
        raise GateError("No backend test projects discovered")
    return sorted(projects, key=lambda p: ("E2E" in p, "Kubernetes" in p, "Integration" in p, p))



GROUPS = ("fast", "services", "kubernetes", "e2e")
PROFILES = {"pr": GROUPS[:2], "full": GROUPS}


def project_group(project):
    name = Path(project).stem
    if "E2E" in name:
        return "e2e"
    if "Kubernetes" in name:
        return "kubernetes"
    if "Integration" in name:
        return "services"
    return "fast"


def selected_projects(profile, root=ROOT):
    if profile not in PROFILES:
        raise GateError(f"Unknown test profile: {profile}")
    return [project for project in test_projects(root) if project_group(project) in PROFILES[profile]]


def parallelism(config=None):
    defaults = {"fast": 2, "services": 1, "kubernetes": 1, "e2e": 1} if os.environ.get("CI") else {
        "fast": 4, "services": 2, "kubernetes": 1, "e2e": 1}
    path = Path(config) if config else ROOT / ".quality-gate.local.json"
    if path.exists():
        values = json.loads(path.read_text())
        if not isinstance(values, dict) or set(values) - set(GROUPS):
            raise GateError("Parallelism config must contain only fast, services, kubernetes and e2e limits")
        for group, value in values.items():
            if type(value) is not int or value < 1:
                raise GateError(f"Parallelism for {group} must be a positive integer")
        defaults.update(values)
    elif config:
        raise GateError(f"Parallelism config does not exist: {path}")
    return defaults


def read_trx(path):
    tree = ET.parse(path)
    namespace = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    counter = tree.find(".//t:Counters", namespace)
    if counter is None:
        raise GateError(f"Missing test counters: {path}")
    counts = {key: int(counter.get(key, "0")) for key in ("total", "executed", "passed", "failed", "notExecuted", "aborted", "error")}
    summary = tree.find(".//t:ResultSummary", namespace)
    results = tree.findall(".//t:UnitTestResult", namespace)
    if summary is None or summary.get("outcome") not in ("Completed", "Passed"):
        raise GateError(f"Test run did not complete successfully: {path}")
    if counts["total"] == 0 or counts["passed"] != counts["total"] or counts["executed"] != counts["total"]:
        failures = [node.get("testName", "unknown") for node in tree.findall(".//t:UnitTestResult", namespace) if node.get("outcome") != "Passed"]
        raise GateError(f"Tests failed or were skipped: {counts}; {', '.join(failures)}")
    if any(counts[key] for key in ("failed", "notExecuted", "aborted", "error")):
        raise GateError(f"Unsuccessful test counters: {counts}")
    if len(results) != counts["total"] or any(node.get("outcome") != "Passed" for node in results):
        raise GateError("TRX results disagree with successful counters")
    return counts


def read_vitest(path):
    result = json.loads(path.read_text())
    total = result.get("numTotalTests", 0)
    if not result.get("success") or total <= 0 or result.get("numPassedTests") != total:
        raise GateError("Frontend tests failed, were skipped, or were not discovered")
    if result.get("numPendingTests", 0) or result.get("numTodoTests", 0) or result.get("numFailedTests", 0):
        raise GateError("Frontend tests include failed, pending, or todo cases")
    return {"total": total, "passed": total}


def check_backend_reports(directory, root=ROOT, profile="full"):
    expected = set()
    for project in selected_projects(profile, root):
        path = root / project
        assembly = ET.parse(path).findtext(".//AssemblyName") or path.stem
        expected.add(assembly.lower())
    observed = set()
    reports = sorted(directory.rglob("*.trx"))
    for report in reports:
        read_trx(report)
        tree = ET.parse(report)
        for method in tree.findall(".//{*}TestMethod"):
            codebase = method.get("codeBase", "").replace("\\", "/")
            if codebase:
                observed.add(Path(codebase).stem.lower())
    if observed != expected:
        raise GateError(f"Backend report inventory differs: missing={sorted(expected - observed)}, unexpected={sorted(observed - expected)}")
    print(f"Validated zero failures/skips in {len(reports)} backend reports.")


def changed_csharp(base):
    paths = set()
    for args in (("diff", "--name-only", "--diff-filter=ACMR", base), ("ls-files", "--others", "--exclude-standard")):
        paths.update(git(*args).splitlines())
    return sorted(p for p in paths if p.endswith(".cs") and (ROOT / p).is_file())


def execute(name, command, directory, steps, environment=None):
    log = directory / f"{name}.log"
    print(f"Running {name}; log: {log.relative_to(ROOT)}", flush=True)
    start = time.monotonic()
    with log.open("w") as stream:
        result = subprocess.run(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, env=environment)
    steps.append({"name": name, "command": command, "exit_code": result.returncode, "seconds": round(time.monotonic() - start, 2), "log": str(log.relative_to(ROOT))})
    if result.returncode:
        raise GateError(f"{name} failed (exit {result.returncode}); inspect {log.relative_to(ROOT)}")


def format_command(base, fix=False):
    paths = changed_csharp(base)
    if not paths:
        return None
    return ["dotnet", "format", "whitespace", "Pole.slnx", "--no-restore", *([] if fix else ["--verify-no-changes"]), "--include", *paths]


def save_receipt(receipt):
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    temporary = RECEIPT.with_suffix(".tmp")
    temporary.write_text(json.dumps(receipt, indent=2) + "\n")
    temporary.replace(RECEIPT)


def require_current_receipt():
    if not RECEIPT.exists():
        raise GateError("No full local test evidence; run scripts/quality_gate.py run")
    receipt = json.loads(RECEIPT.read_text())
    if receipt.get("status") != "passed":
        raise GateError(receipt.get("failure", "The full local gate has not passed"))
    if receipt.get("fingerprint") != source_fingerprint():
        raise GateError("Sources changed after the full local gate; rerun every suite")
    if receipt.get("base_sha") != git("rev-parse", receipt["base"]):
        raise GateError("Review base changed; rerun the full local gate")
    if receipt.get("projects") != selected_projects(receipt.get("profile", "full")):
        raise GateError("Test project inventory changed; rerun the full local gate")
    return receipt


def require_publication_base(receipt, branch):
    target = git("rev-parse", "--verify", f"refs/remotes/origin/{branch}")
    if target != receipt["base_sha"]:
        raise GateError("PR target differs from the reviewed base; use open-pr --base with the tested target, or rerun the full gate against its origin ref")


def capture_headroom(directory, phase, include_lab=True):
    """Record scalar capacity/condition evidence, never kubeconfigs or native payloads."""
    disk = shutil.disk_usage(ROOT)
    memory = {}
    try:
        for line in Path("/proc/meminfo").read_text().splitlines():
            key, value = line.split(":", 1)
            if key in ("MemTotal", "MemAvailable"):
                memory[key] = int(value.split()[0]) * 1024
    except (OSError, ValueError):
        pass
    result = {"phase": phase, "disk_total": disk.total, "disk_free": disk.free, "memory": memory, "targets": []}
    state_path = Path(os.environ.get("POLE_K8S_LAB_STATE", ROOT / ".tmp/kubernetes-lab/state.json"))
    if include_lab:
        try:
            state = json.loads(state_path.read_text())
            for target in state.get("targets", [])[:8]:
                item = {"id": hashlib.sha256(target["key"].encode()).hexdigest(), "nodes": []}
                result["targets"].append(item)
                try:
                    command = [state["tools"]["kubectlPath"], "--kubeconfig", target["kubeconfigPath"],
                               "--context", target["context"], "get", "nodes", "-o", "json", "--request-timeout=3s"]
                    completed = subprocess.run(command, cwd=ROOT, capture_output=True, timeout=5)
                    item["exit_code"] = completed.returncode
                    if completed.returncode == 0:
                        for node in json.loads(completed.stdout).get("items", [])[:32]:
                            conditions = {condition.get("type"): condition.get("status") for condition in node.get("status", {}).get("conditions", [])}
                            item["nodes"].append({name: conditions[name] == "True" for name in ("Ready", "DiskPressure", "MemoryPressure")
                                                  if conditions.get(name) in ("True", "False")})
                    else:
                        item["unavailable"] = True
                except (OSError, ValueError, KeyError, subprocess.TimeoutExpired):
                    item["unavailable"] = True
        except (OSError, ValueError, KeyError):
            result["lab_unavailable"] = True
    (directory / f"headroom-{phase}.json").write_text(json.dumps(result, indent=2) + "\n")



def sample_host_memory(stop, samples):
    """Track host capacity, including simultaneously running containers and test hosts."""
    while not stop.is_set():
        try:
            memory = dict(line.split(":", 1) for line in Path("/proc/meminfo").read_text().splitlines())
            total = int(memory["MemTotal"].split()[0]) * 1024
            available = int(memory["MemAvailable"].split()[0]) * 1024
            samples["peak_used_bytes"] = max(samples.get("peak_used_bytes", 0), total - available)
            samples["minimum_available_bytes"] = min((samples.get("minimum_available_bytes") or available), available)
        except (OSError, KeyError, ValueError):
            pass
        stop.wait(0.5)


def run_backend(directory, steps=None, tests=None, profile="full", limits=None):
    """Run every selected project with bounded per-group concurrency and coverage."""
    directory = directory.resolve()
    if not directory.is_relative_to(ROOT):
        raise GateError("Backend results must be inside the workspace")
    directory.mkdir(parents=True, exist_ok=True)
    manifest_path = directory / "backend-manifest.json"
    if manifest_path.exists() or any(directory.rglob("*.trx")):
        raise GateError("Backend results require a fresh directory; previous evidence must be retained")
    steps = steps if steps is not None else []
    tests = tests if tests is not None else {}
    projects = selected_projects(profile)
    limits = limits or dict.fromkeys(GROUPS, 1)
    manifest = {"status": "running", "coverage": True, "profile": profile, "parallelism": limits, "projects": projects, "steps": steps, "tests": tests}
    stop_sampling = threading.Event()
    manifest["host_memory"] = {"peak_used_bytes": 0, "minimum_available_bytes": 0}
    sampler = threading.Thread(target=sample_host_memory, args=(stop_sampling, manifest["host_memory"]), daemon=True)
    sampler.start()
    environment = dict(os.environ)
    environment.setdefault("POLE_TEST_EVIDENCE_DIRECTORY", str(directory / "phases"))
    try:
        capture_headroom(directory, "before-backend", include_lab=profile == "full")
        def run_project(index, project):
            name = f"backend-{index}"
            output = directory / name
            command = ["dotnet", "test", project, "--no-build", "--no-restore", "--nologo", "-maxcpucount:1", "-nodeReuse:false",
                       "--collect:Code Coverage;Format=Cobertura", "--logger", "trx;LogFileName=results.trx", "--results-directory", str(output)]
            execute(name, command, directory, steps, environment)
            result = read_trx(output / "results.trx")
            coverage = list(output.rglob("*.cobertura.xml"))
            if not coverage or any(ET.parse(report).getroot().tag != "coverage" for report in coverage):
                raise GateError(f"Missing or invalid coverage report for {project}")
            return result

        for group in GROUPS:
            jobs = [(index, project) for index, project in enumerate(projects) if project_group(project) == group]
            if not jobs:
                continue
            capture_headroom(directory, f"before-{group}", include_lab=profile == "full")
            failures = []
            with ThreadPoolExecutor(max_workers=limits[group]) as pool:
                pending = {pool.submit(run_project, index, project): project for index, project in jobs}
                for future in as_completed(pending):
                    project = pending[future]
                    try:
                        tests[project] = future.result()
                    except Exception as error:
                        failures.append(f"{project}: {error}")
                    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
            if failures:
                raise GateError("\n".join(failures))
        check_backend_reports(directory, ROOT, profile)
        manifest["status"] = "passed"
        return tests
    finally:
        stop_sampling.set()
        sampler.join()
        failing = sys.exc_info()[0] is not None
        if manifest["status"] != "passed":
            manifest["status"] = "failed"
        try:
            capture_headroom(directory, "after-backend" if manifest["status"] == "passed" else "failed-backend", include_lab=profile == "full")
        except OSError:
            # A diagnostic write failure must not replace the failing test command.
            manifest["headroom_unavailable"] = True
        try:
            manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
        except OSError:
            if not failing:
                raise


def run_full(base, profile="full", config=None):
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    directory = ARTIFACTS / time.strftime("%Y%m%d-%H%M%S")
    directory.mkdir()
    receipt = {"status": "running", "profile": profile, "base": base, "base_sha": git("rev-parse", base), "fingerprint": source_fingerprint(), "steps": [], "tests": {}}
    save_receipt(receipt)  # An interrupted run invalidates older success evidence.
    steps = receipt["steps"]
    try:
        projects = selected_projects(profile)
        receipt["projects"] = projects
        execute("automation", [sys.executable, "scripts/run_script_tests.py", str(directory / "scripts.json")], directory, steps)
        for index, shell_test in enumerate(sorted((ROOT / "infrastructure").glob("**/tests/*.sh"))):
            execute(f"shell-{index}", ["bash", str(shell_test)], directory, steps)
        if profile == "full":
            capture_headroom(directory, "before-lab", include_lab=False)
            execute("lab-verify", ["bash", "infrastructure/kubernetes-lab/lab.sh", "verify"], directory, steps)
            capture_headroom(directory, "after-lab")
        execute("frontend-install", ["npm", "ci", "--prefix", "src/api/frontend"], directory, steps)
        execute("restore", ["dotnet", "restore", "Pole.slnx", *MSBUILD], directory, steps)
        execute("build", ["dotnet", "build", "Pole.slnx", "--no-restore", *MSBUILD], directory, steps)
        command = format_command(base)
        if command:
            execute("format", command, directory, steps)
        run_backend(directory, steps, receipt["tests"], profile, parallelism(config))
        frontend_report = directory / "frontend.json"
        execute("frontend-tests", ["npm", "test", "--prefix", "src/api/frontend", "--", "--reporter=json", f"--outputFile={frontend_report}"], directory, steps)
        receipt["tests"]["frontend"] = read_vitest(frontend_report)
        execute("frontend-build", ["npm", "run", "build", "--prefix", "src/api/frontend"], directory, steps)
        execute("frontend-lint", ["npm", "run", "lint", "--prefix", "src/api/frontend"], directory, steps)
        if profile == "full":
            execute("resource-audit", ["bash", "infrastructure/kubernetes-lab/lab.sh", "audit-tests"], directory, steps)
        execute("diff-check", ["git", "diff", "--check", receipt["base_sha"]], directory, steps)
        if receipt["fingerprint"] != source_fingerprint():
            raise GateError("Sources changed while tests ran; full evidence is stale")
        receipt["status"] = "passed"
        print("All local test projects and frontend tests passed with zero skips.")
    except (GateError, OSError, ValueError, ET.ParseError) as error:
        receipt["status"] = "failed"
        receipt["failure"] = str(error)
        raise GateError(str(error)) from error
    finally:
        if receipt["status"] != "passed":
            try:
                capture_headroom(directory, "failed-run", include_lab=profile == "full")
            except OSError:
                pass
        save_receipt(receipt)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("run", "format", "check-format"):
        sub = commands.add_parser(name)
        sub.add_argument("--base", default="origin/master")
        if name == "run":
            sub.add_argument("--profile", choices=PROFILES, default="full")
            sub.add_argument("--config", type=Path)
    commands.add_parser("verify")
    backend_lane = commands.add_parser("backend")
    backend_lane.add_argument("--results-directory", type=Path, required=True)
    backend_lane.add_argument("--profile", choices=PROFILES, default="full")
    backend_lane.add_argument("--config", type=Path)
    headroom = commands.add_parser("headroom")
    headroom.add_argument("--results-directory", type=Path, required=True)
    headroom.add_argument("--phase", choices=("before-lab", "after-lab", "failed-run"), required=True)
    backend = commands.add_parser("check-backend-reports")
    backend.add_argument("--directory", type=Path, required=True)
    backend.add_argument("--profile", choices=PROFILES, default="full")
    frontend = commands.add_parser("check-frontend-report")
    frontend.add_argument("--report", type=Path, required=True)
    review = commands.add_parser("record-review")
    review.add_argument("--agent", required=True)
    review.add_argument("--summary-file", type=Path, required=True)
    publish = commands.add_parser("open-pr")
    publish.add_argument("--base", default="master")
    publish.add_argument("--title", required=True)
    publish.add_argument("--body-file", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "run":
            run_full(args.base, args.profile, args.config)
        elif args.command == "backend":
            run_backend(args.results_directory, profile=args.profile, limits=parallelism(args.config))
        elif args.command == "headroom":
            args.results_directory.mkdir(parents=True, exist_ok=True)
            capture_headroom(args.results_directory, args.phase, include_lab=args.phase != "before-lab")
        elif args.command in ("format", "check-format"):
            command = format_command(args.base, fix=args.command == "format")
            if command:
                subprocess.run(command, cwd=ROOT, check=True)
        elif args.command == "check-backend-reports":
            check_backend_reports(args.directory, profile=args.profile)
        elif args.command == "check-frontend-report":
            print(read_vitest(args.report))
        elif args.command == "record-review":
            receipt = require_current_receipt()
            summary = args.summary_file.read_text().strip()
            if not summary or not args.agent.strip():
                raise GateError("Review summary is empty")
            receipt["review"] = {"agent": args.agent, "summary": summary, "fingerprint": receipt["fingerprint"]}
            save_receipt(receipt)
        else:
            receipt = require_current_receipt()
            if args.command == "open-pr":
                require_publication_base(receipt, args.base)
                if not receipt.get("review"):
                    raise GateError("Record the completed review required by AGENTS.md before opening a PR")
                if git("status", "--porcelain"):
                    raise GateError("Commit the reviewed changes before opening a PR")
                branch = git("branch", "--show-current")
                if not branch or branch == "master":
                    raise GateError("A task branch is required")
                remote = git("ls-remote", "--exit-code", "origin", f"refs/heads/{branch}").split()
                if not remote or remote[0] != git("rev-parse", "HEAD"):
                    raise GateError("Push the tested commit before opening a PR; remote branch differs")
                subprocess.run(["gh", "pr", "create", "--base", args.base, "--head", branch, "--title", args.title, "--body-file", str(args.body_file.resolve())], cwd=ROOT, check=True)
            else:
                print("Full local test evidence matches the current source tree.")
    except (GateError, OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"BLOCKED: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
