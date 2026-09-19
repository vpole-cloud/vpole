#!/usr/bin/env python3
"""Local publication gate. Full runs never accept test filters or skipped tests."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
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


def check_backend_reports(directory, root=ROOT):
    expected = set()
    for project in test_projects(root):
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


def execute(name, command, directory, steps):
    log = directory / f"{name}.log"
    print(f"Running {name}; log: {log.relative_to(ROOT)}", flush=True)
    start = time.monotonic()
    with log.open("w") as stream:
        result = subprocess.run(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT)
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
    if receipt.get("projects") != test_projects():
        raise GateError("Test project inventory changed; rerun the full local gate")
    return receipt


def require_publication_base(receipt, branch):
    target = git("rev-parse", "--verify", f"refs/remotes/origin/{branch}")
    if target != receipt["base_sha"]:
        raise GateError("PR target differs from the reviewed base; use open-pr --base with the tested target, or rerun the full gate against its origin ref")


def run_full(base):
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    directory = ARTIFACTS / time.strftime("%Y%m%d-%H%M%S")
    directory.mkdir()
    receipt = {"status": "running", "base": base, "base_sha": git("rev-parse", base), "fingerprint": source_fingerprint(), "steps": [], "tests": {}}
    save_receipt(receipt)  # An interrupted run invalidates older success evidence.
    steps = receipt["steps"]
    try:
        projects = test_projects()
        receipt["projects"] = projects
        execute("automation", [sys.executable, "scripts/run_script_tests.py", str(directory / "scripts.json")], directory, steps)
        for index, shell_test in enumerate(sorted((ROOT / "infrastructure").glob("**/tests/*.sh"))):
            execute(f"shell-{index}", ["bash", str(shell_test)], directory, steps)
        execute("lab-verify", ["bash", "infrastructure/kubernetes-lab/lab.sh", "verify"], directory, steps)
        execute("frontend-install", ["npm", "ci", "--prefix", "src/api/frontend"], directory, steps)
        execute("restore", ["dotnet", "restore", "Pole.slnx", *MSBUILD], directory, steps)
        execute("build", ["dotnet", "build", "Pole.slnx", "--no-restore", *MSBUILD], directory, steps)
        command = format_command(base)
        if command:
            execute("format", command, directory, steps)
        # No filter, project subset, opt-out, or no-test success path is accepted here.
        for index, project in enumerate(projects):
            output = directory / f"backend-{index}"
            execute(f"backend-{index}", ["dotnet", "test", project, "--no-build", "--no-restore", "--nologo", "--logger", "trx;LogFileName=results.trx", "--results-directory", str(output)], directory, steps)
            receipt["tests"][project] = read_trx(output / "results.trx")
        frontend_report = directory / "frontend.json"
        execute("frontend-tests", ["npm", "test", "--prefix", "src/api/frontend", "--", "--reporter=json", f"--outputFile={frontend_report}"], directory, steps)
        receipt["tests"]["frontend"] = read_vitest(frontend_report)
        execute("frontend-build", ["npm", "run", "build", "--prefix", "src/api/frontend"], directory, steps)
        execute("frontend-lint", ["npm", "run", "lint", "--prefix", "src/api/frontend"], directory, steps)
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
        save_receipt(receipt)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("run", "format", "check-format"):
        sub = commands.add_parser(name)
        sub.add_argument("--base", default="origin/master")
    commands.add_parser("verify")
    backend = commands.add_parser("check-backend-reports")
    backend.add_argument("--directory", type=Path, required=True)
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
            run_full(args.base)
        elif args.command in ("format", "check-format"):
            command = format_command(args.base, fix=args.command == "format")
            if command:
                subprocess.run(command, cwd=ROOT, check=True)
        elif args.command == "check-backend-reports":
            check_backend_reports(args.directory)
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
