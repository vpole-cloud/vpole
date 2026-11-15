#!/usr/bin/env python3
"""Profiled local and CI gate. Selected suites never accept filters or skipped tests."""

import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import time
import xml.etree.ElementTree as ET

import verification_scope as scope
from template_evidence import load_report as load_template_report

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / ".artifacts" / "quality"
RECEIPT = ARTIFACTS / "receipt.json"
MSBUILD = ["--disable-build-servers", "--nologo", "-nodeReuse:false"]


class GateError(RuntimeError):
    pass


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT).decode().strip()


def source_fingerprint(root=ROOT):
    return scope.fingerprint(scope.working_tree(root))


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


def gate_settings(config=None):
    defaults = {"fast": 2, "services": 1, "kubernetes": 1, "e2e": 1} if os.environ.get("CI") else {
        "fast": 4, "services": 2, "kubernetes": 1, "e2e": 1}
    defaults.update(buildMaxCpuCount=2 if os.environ.get("CI") else "auto",
                    integrationMaxParallelThreads=2 if os.environ.get("CI") else 4)
    path = Path(config) if config else ROOT / ".quality-gate.local.json"
    if path.exists():
        values = json.loads(path.read_text())
        if not isinstance(values, dict) or set(values) - set(defaults):
            raise GateError("Unknown gate configuration keys")
        for group, value in values.items():
            if group == "buildMaxCpuCount" and value == "auto":
                continue
            if type(value) is not int or value < 1:
                raise GateError(f"Parallelism for {group} must be a positive integer")
        defaults.update(values)
    elif config:
        raise GateError(f"Parallelism config does not exist: {path}")
    return defaults


def parallelism(config=None):
    settings = gate_settings(config)
    return {group: settings[group] for group in GROUPS}


def build_options(max_cpu_count):
    # The dotnet CLI already supplies -maxcpucount; omitting a number uses the CPU count.
    return [*MSBUILD, *([] if max_cpu_count == "auto" else [f"-maxcpucount:{max_cpu_count}"])]


def project_files(root):
    ignored = {"bin", "obj", ".artifacts", ".tmp", ".git", "node_modules"}
    return sorted(path.resolve() for area in ("src", "tests") if (root / area).exists()
                  for path in (root / area).rglob("*.*proj")
                  if path.suffix in (".csproj", ".fsproj") and not ignored.intersection(path.relative_to(root).parts))


def write_coverage_settings(items, path):
    """Include evaluated product assemblies, including migrations and non-Pole names."""
    projects = [item for item in items if item["Kind"] == "Project"]
    invalid_roles = {item["Role"] for item in projects} - {"Production", "Migration", "TestSupport"}
    if invalid_roles:
        raise GateError(f"Unknown architecture roles for coverage: {sorted(invalid_roles)}")
    assemblies = sorted({item["AssemblyName"] for item in projects
                         if item["Role"] != "TestSupport" and item["IsTestProject"].lower() != "true"})
    if not assemblies or any(not name or Path(name).name != name for name in assemblies):
        raise GateError("Coverage requires evaluated production assembly names")
    root = ET.Element("RunSettings")
    collectors = ET.SubElement(ET.SubElement(root, "DataCollectionRunSettings"), "DataCollectors")
    configuration = ET.SubElement(ET.SubElement(collectors, "DataCollector", friendlyName="Code Coverage"), "Configuration")
    ET.SubElement(configuration, "Format").text = "Cobertura"
    ET.SubElement(configuration, "IncludeTestAssembly").text = "False"
    coverage = ET.SubElement(configuration, "CodeCoverage")
    includes = ET.SubElement(ET.SubElement(coverage, "ModulePaths"), "Include")
    for assembly in assemblies:
        ET.SubElement(includes, "ModulePath").text = r"(^|.*[/\\])" + re.escape(assembly) + r"\.(dll|exe)$"
    ET.SubElement(coverage, "CollectFromChildProcesses").text = "True"
    ET.indent(root)
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
    return {"settings": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "assemblies": assemblies}


def prepare_coverage(directory, steps):
    paths = project_files(ROOT)
    list_file = directory / "coverage-projects.txt"
    list_file.write_text("\n".join(str(path) for path in paths) + "\n")
    command = ["dotnet", "msbuild", "tests/Pole.Tests.Architecture/ProjectInputs.proj",
               "-getTargetResult:CollectArchitectureInputs", "-nologo", "-maxcpucount:1", "-nodeReuse:false",
               f"-property:PoleProjectListFile={list_file}", "-property:PoleCollectCompilerInputs=false"]
    execute("coverage-metadata", command, directory, steps)
    try:
        data = json.loads((directory / "coverage-metadata.log").read_text())
        items = data["TargetResults"]["CollectArchitectureInputs"]["Items"]
        observed = [Path(item["ProjectPath"]).resolve() for item in items if item["Kind"] == "Project"]
    except (ValueError, KeyError, TypeError) as error:
        raise GateError("Invalid coverage metadata; inspect coverage-metadata.log") from error
    if sorted(observed) != paths:
        raise GateError("Coverage metadata inventory differs from the discovered projects")
    settings = directory / "coverage.runsettings"
    evidence = write_coverage_settings(items, settings)
    (directory / "coverage-settings.json").write_text(json.dumps(evidence, indent=2) + "\n")
    return settings, evidence


def read_coverage_reports(directory, allowed_assemblies):
    reports = list(directory.rglob("*.cobertura.xml"))
    modules = set()
    if not reports:
        raise GateError(f"Missing coverage report for {directory.name}")
    for report in reports:
        root = ET.parse(report).getroot()
        if root.tag != "coverage":
            raise GateError(f"Invalid coverage report for {directory.name}")
        modules.update(package.get("name", "") for package in root.findall("./packages/package"))
    unexpected = modules - set(allowed_assemblies)
    if unexpected:
        raise GateError(f"Coverage contains excluded modules: {sorted(unexpected)}")
    return sorted(modules)


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
    stop = threading.Event()
    samples = {"peak_used_bytes": 0, "minimum_available_bytes": 0}
    sampler = threading.Thread(target=sample_host_memory, args=(stop, samples), daemon=True) if name == "build" else None
    if sampler:
        sampler.start()
    try:
        with log.open("w") as stream:
            result = subprocess.run(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, env=environment)
    finally:
        stop.set()
        if sampler:
            sampler.join()
    step = {"name": name, "command": command, "exit_code": result.returncode, "seconds": round(time.monotonic() - start, 2), "log": str(log.relative_to(ROOT))}
    if sampler:
        step["host_memory"] = samples
    steps.append(step)
    if result.returncode:
        raise GateError(f"{name} failed (exit {result.returncode}); inspect {log.relative_to(ROOT)}")


def format_command(base, fix=False):
    paths = changed_csharp(base)
    if not paths:
        return None
    return [sys.executable, "scripts/cleanupcode.py", "--write" if fix else "--check", "--", *paths]


def save_receipt(receipt):
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    temporary = RECEIPT.with_suffix(".tmp")
    temporary.write_text(json.dumps(receipt, indent=2) + "\n")
    temporary.replace(RECEIPT)


def require_current_receipt():
    if not RECEIPT.exists():
        raise GateError("No current test evidence; run scripts/quality_gate.py run with the required profile")
    receipt = json.loads(RECEIPT.read_text())
    if receipt.get("status") != "passed":
        raise GateError(receipt.get("failure", "The selected local gate has not passed"))
    if receipt.get("fingerprint") != source_fingerprint():
        raise GateError("Sources changed; run the gate to validate the delta or rerun the selected suites")
    if receipt.get("base_sha") != git("rev-parse", receipt["base"]):
        raise GateError("Review base changed; rerun the full local gate")
    if receipt.get("mode", "all") == "reduced":
        required = scope.plan(receipt["base"], ROOT)
        if required["behavioral"] or (required["template"] and not receipt.get("validated", {}).get("template")):
            raise GateError("Reduced evidence does not cover the complete PR diff")
    elif receipt.get("projects") != selected_projects(receipt.get("profile", "full")):
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


def run_backend(directory, steps=None, tests=None, profile="full", limits=None,
                integration_max_parallel_threads=None):
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
    if integration_max_parallel_threads is None:
        integration_max_parallel_threads = gate_settings()["integrationMaxParallelThreads"]
    manifest = {"status": "running", "coverage": True, "profile": profile, "parallelism": limits, "projects": projects, "steps": steps, "tests": tests}
    manifest["integrationMaxParallelThreads"] = integration_max_parallel_threads
    stop_sampling = threading.Event()
    manifest["host_memory"] = {"peak_used_bytes": 0, "minimum_available_bytes": 0}
    sampler = threading.Thread(target=sample_host_memory, args=(stop_sampling, manifest["host_memory"]), daemon=True)
    sampler.start()
    environment = dict(os.environ)
    environment.setdefault("POLE_TEST_EVIDENCE_DIRECTORY", str(directory / "phases"))
    try:
        coverage_settings, coverage_evidence = prepare_coverage(directory, steps)
        manifest["coverage_settings"] = coverage_evidence
        manifest["coverage_modules"] = {}
        capture_headroom(directory, "before-backend", include_lab=profile == "full")
        def run_project(index, project):
            name = f"backend-{index}"
            output = directory / name
            command = ["dotnet", "test", project, "--no-build", "--no-restore", "--nologo", "-maxcpucount:1", "-nodeReuse:false",
                       "--collect:Code Coverage;Format=Cobertura", "--settings", str(coverage_settings),
                       "--logger", "trx;LogFileName=results.trx", "--results-directory", str(output)]
            if Path(project).stem == "Pole.Tests.Integration":
                command.extend(["--", f"xUnit.MaxParallelThreads={integration_max_parallel_threads}"])
            execute(name, command, directory, steps, environment)
            result = read_trx(output / "results.trx")
            modules = read_coverage_reports(output, coverage_evidence["assemblies"])
            return result, modules

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
                        tests[project], manifest["coverage_modules"][project] = future.result()
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


def run_shell_tests(profile, directory, steps):
    if profile == "pr":
        # Only these scripts exercise fixtures without starting or connecting to a cluster.
        lab_tests = ROOT / "infrastructure/kubernetes-lab/tests"
        scripts = [lab_tests / name for name in (
            "harbor-dns.sh", "lifecycle-lock.sh", "recovery.sh", "seaweedfs-overlay.sh", "verification-port-forward.sh",
        )]
    else:
        scripts = sorted((ROOT / "infrastructure").glob("**/tests/*.sh"))
    for index, script in enumerate(scripts):
        execute(f"shell-{index}-{script.stem}", ["bash", str(script)], directory, steps)


def run_full(base, profile="full", config=None):
    started = time.monotonic()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    directory = ARTIFACTS / time.strftime("%Y%m%d-%H%M%S")
    directory.mkdir()
    receipt = {"status": "running", "profile": profile, "base": base, "base_sha": git("rev-parse", base), "fingerprint": source_fingerprint(), "steps": [], "tests": {}}
    receipt.update(version=2, mode="all", tested_head=git("rev-parse", "HEAD"),
                   committed=git("status", "--porcelain") == "",
                   validated={"behavioral": True, "template": True})
    save_receipt(receipt)  # An interrupted run invalidates older success evidence.
    steps = receipt["steps"]
    try:
        settings = gate_settings(config)
        receipt["configuration"] = settings
        projects = selected_projects(profile)
        receipt["projects"] = projects
        execute("automation", [sys.executable, "scripts/run_script_tests.py", str(directory / "scripts.json")], directory, steps)
        if profile == "full":
            capture_headroom(directory, "before-lab", include_lab=False)
            execute("lab-verify", ["bash", "infrastructure/kubernetes-lab/lab.sh", "verify"], directory, steps)
            capture_headroom(directory, "after-lab")
        run_shell_tests(profile, directory, steps)
        execute("frontend-install", ["npm", "ci", "--prefix", "src/api/frontend"], directory, steps)
        execute("restore", ["dotnet", "restore", "Pole.slnx", *build_options(settings["buildMaxCpuCount"])], directory, steps)
        execute("build", ["dotnet", "build", "Pole.slnx", "--no-restore", *build_options(settings["buildMaxCpuCount"])], directory, steps)
        execute("dotnet-tools-restore", ["dotnet", "tool", "restore"], directory, steps)
        diff_base = git("merge-base", receipt["base_sha"], "HEAD")
        execute("documents", [sys.executable, "scripts/check_change_documents.py", "--base", diff_base], directory, steps)
        execute("format", [sys.executable, "scripts/format_changed.py", "--base", diff_base, "--check"], directory, steps)
        template_directory = ROOT / ".artifacts/feature-template" / directory.name
        execute("feature-template", [sys.executable, "scripts/check_feature_template.py",
                "--results-directory", str(template_directory)], directory, steps)
        generated = load_template_report(template_directory / "result.json", "complete", receipt["fingerprint"])
        receipt["generated_tests"] = generated["tests"]
        run_backend(directory, steps, receipt["tests"], profile,
                    {group: settings[group] for group in GROUPS}, settings["integrationMaxParallelThreads"])
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
        print(f"All {profile}-profile projects and frontend tests passed with zero skips.")
    except (GateError, OSError, ValueError, ET.ParseError) as error:
        receipt["status"] = "failed"
        receipt["failure"] = str(error)
        raise GateError(str(error)) from error
    finally:
        receipt["seconds"] = round(time.monotonic() - started, 2)
        if receipt["status"] != "passed":
            try:
                capture_headroom(directory, "failed-run", include_lab=profile == "full")
            except OSError:
                pass
        save_receipt(receipt)


def run_structural_checks(selection, directory, steps):
    execute("documents", [sys.executable, "scripts/check_change_documents.py", "--base",
                          selection["diff_base_sha"]], directory, steps)
    execute("frontend-install", ["npm", "ci", "--prefix", "src/api/frontend"], directory, steps)
    if any(item["path"].endswith(".cs") for item in selection["files"]):
        execute("dotnet-tools-restore", ["dotnet", "tool", "restore"], directory, steps)
    execute("format", [sys.executable, "scripts/format_changed.py", "--base",
                       selection["diff_base_sha"], "--check"], directory, steps)
    execute("diff-check", ["git", "diff", "--check", selection["diff_base_sha"]], directory, steps)


def reusable_receipt(previous, selection):
    if (not previous or previous.get("status") != "passed" or not previous.get("committed")
            or previous.get("base_sha") != selection["base_sha"]):
        return None
    try:
        tested = scope.committed_tree(ROOT, previous["tested_head"])
        if scope.fingerprint(tested) != previous["fingerprint"]:
            return None
        current = scope.working_tree(ROOT)
        if selection.get("fingerprint") and scope.fingerprint(current) != selection["fingerprint"]:
            return None
        delta = scope.classify_trees(tested, current)
    except (KeyError, ValueError, subprocess.CalledProcessError):
        return None
    if any(item["kind"] not in {"documentation", "formatting", "template-format"} for item in delta["files"]):
        return None
    validated = previous.get("validated", {})
    if selection["behavioral"] and not validated.get("behavioral"):
        return None
    if selection["template"] and not validated.get("template"):
        return None
    return delta


def run_selected(base, profile="pr", config=None):
    previous = json.loads(RECEIPT.read_text()) if RECEIPT.exists() else None
    save_receipt({"status": "running", "base": base})
    selection = scope.plan(base, ROOT, force_full=profile == "full")
    reuse = None if profile == "full" else reusable_receipt(previous, selection)
    if selection["behavioral"] and reuse is None:
        run_full(base, profile, config)
        return
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    directory = ARTIFACTS / str(time.time_ns())
    directory.mkdir()
    receipt = {"version": 2, "mode": "all" if reuse and selection["behavioral"] else "reduced",
               "profile": profile, "selection": selection, "base": base, "base_sha": selection["base_sha"],
               "fingerprint": selection["fingerprint"], "status": "running", "steps": [], "tests": {},
               "projects": [], "tested_head": git("rev-parse", "HEAD"),
               "committed": git("status", "--porcelain") == "", "validated": {}}
    if reuse is not None:
        receipt["profile"] = previous["profile"]
        for key in ("tests", "projects", "validated"):
            receipt[key] = previous[key]
        if "generated_tests" in previous:
            receipt["generated_tests"] = previous["generated_tests"]
        receipt["reused"] = {"from_head": previous["tested_head"], "from_fingerprint": previous["fingerprint"],
                             "to_head": receipt["tested_head"], "delta": reuse["files"]}
    save_receipt(receipt)
    try:
        if source_fingerprint() != receipt["fingerprint"]:
            raise GateError("Inputs changed after classification")
        run_structural_checks(selection, directory, receipt["steps"])
        if selection["automation"] and reuse is None:
            report = directory / "scripts.json"
            execute("automation", [sys.executable, "scripts/run_script_tests.py", str(report)], directory, receipt["steps"])
            receipt["tests"]["automation"] = json.loads(report.read_text())
        if (selection["template"] or selection["template_format"]):
            output = ROOT / ".artifacts/feature-template" / directory.name
            command = [sys.executable, "scripts/check_feature_template.py", "--results-directory", str(output)]
            if reuse is not None or not selection["template"]:
                command.append("--format-only")
            execute("feature-template", command, directory, receipt["steps"])
            mode = "format-only" if "--format-only" in command else "complete"
            generated = load_template_report(output / "result.json", mode, receipt["fingerprint"])
            if mode == "complete":
                receipt["generated_tests"] = generated["tests"]
                receipt["validated"]["template"] = True
        if source_fingerprint() != receipt["fingerprint"]:
            raise GateError("Inputs changed during selected verification")
        receipt["status"] = "passed"
    except (GateError, OSError, ValueError, subprocess.CalledProcessError) as error:
        receipt.update(status="failed", failure=str(error))
        raise
    finally:
        save_receipt(receipt)
    print("Selected checks passed. Unselected suites were not required, not reported as passed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("run", "format", "check-format"):
        sub = commands.add_parser(name)
        sub.add_argument("--base", default="origin/master")
        if name == "run":
            sub.add_argument("--profile", choices=PROFILES, default="pr")
            sub.add_argument("--config", type=Path)
    shell = commands.add_parser("shell-tests")
    shell.add_argument("--profile", choices=PROFILES, required=True)
    commands.add_parser("verify")
    commands.add_parser("check-ci")
    for name in ("plan", "checks"):
        sub = commands.add_parser(name)
        sub.add_argument("--base", default="origin/master")
        sub.add_argument("--force-full", action="store_true")
        if name == "plan":
            sub.add_argument("--output", type=Path)
            sub.add_argument("--github-output", type=Path)
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
    template = commands.add_parser("check-template-report")
    template.add_argument("--report", type=Path, required=True)
    template.add_argument("--mode", choices=("complete", "format-only"), required=True)
    frontend = commands.add_parser("check-frontend-report")
    frontend.add_argument("--report", type=Path, required=True)
    coverage = commands.add_parser("coverage-settings")
    coverage.add_argument("--results-directory", type=Path, required=True)
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
            run_selected(args.base, args.profile, args.config)
        elif args.command in ("plan", "checks"):
            selection = scope.plan(args.base, ROOT, force_full=args.force_full)
            if args.command == "plan":
                rendered = json.dumps(selection, indent=2) + "\n"
                if args.output:
                    args.output.parent.mkdir(parents=True, exist_ok=True)
                    args.output.write_text(rendered)
                else:
                    print(rendered)
                if args.github_output:
                    with args.github_output.open("a") as stream:
                        for key in ("behavioral", "template", "template_format", "automation"):
                            stream.write(f"{key}={str(selection[key]).lower()}\n")
            else:
                directory = ARTIFACTS / str(time.time_ns())
                directory.mkdir(parents=True)
                run_structural_checks(selection, directory, [])
                if source_fingerprint() != selection["fingerprint"]:
                    raise GateError("Inputs changed during CI structural checks")
        elif args.command == "check-ci":
            required = scope.validate_ci_needs(json.loads(os.environ["GATE_CI_NEEDS"]))
            print(json.dumps({job: "passed" if selected else "not required" for job, selected in required.items()}))
        elif args.command == "shell-tests":
            directory = ARTIFACTS / str(time.time_ns())
            directory.mkdir(parents=True)
            run_shell_tests(args.profile, directory, [])
        elif args.command == "backend":
            settings = gate_settings(args.config)
            run_backend(args.results_directory, profile=args.profile,
                        limits={group: settings[group] for group in GROUPS},
                        integration_max_parallel_threads=settings["integrationMaxParallelThreads"])
        elif args.command == "coverage-settings":
            directory = args.results_directory.resolve()
            if not directory.is_relative_to(ROOT):
                raise GateError("Coverage results must be inside the workspace")
            directory.mkdir(parents=True, exist_ok=True)
            if any(directory.iterdir()):
                raise GateError("Coverage settings require a fresh results directory")
            prepare_coverage(directory, [])
        elif args.command == "headroom":
            args.results_directory.mkdir(parents=True, exist_ok=True)
            capture_headroom(args.results_directory, args.phase, include_lab=args.phase != "before-lab")
        elif args.command in ("format", "check-format"):
            command = format_command(args.base, fix=args.command == "format")
            if command:
                result = subprocess.run(command, cwd=ROOT, check=False)
                if result.returncode:
                    raise GateError("CleanupCode formatting failed; inspect the output and .artifacts/cleanupcode/")
        elif args.command == "check-backend-reports":
            check_backend_reports(args.directory, profile=args.profile)
        elif args.command == "check-template-report":
            report = load_template_report(args.report, args.mode, source_fingerprint())
            print(f"Validated generated {report['mode']} evidence for the current source.")
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
                print(f"Current {receipt.get('mode', 'all')} verification evidence matches the source tree.")
    except (GateError, OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"BLOCKED: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
