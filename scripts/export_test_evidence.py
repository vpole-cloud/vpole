#!/usr/bin/env python3
"""Export allowlisted test metadata; never publish captured output or response bodies."""

import argparse
from functools import lru_cache
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess
import uuid
import xml.etree.ElementTree as ET

NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
ET.register_namespace("", NS)
COUNTERS = ("total", "executed", "passed", "failed", "notExecuted", "aborted", "error")
OUTCOMES = {"Passed", "Failed", "NotExecuted", "Aborted", "Completed", "Error", "Timeout"}
PHASES = {
    "Started", "Discovery", "Selection", "Action", "RequestStarted", "ResponseReceived",
    "RequestCancelled", "RequestFailed", "SnapshotAccepted", "StreamOpening", "StreamReading",
    "StreamEnded", "StreamFailed", "Cleanup", "Completed", "Failed", "Truncated",
}
HASH_FIELDS = ("scenario", "resource", "instance", "container")
INTEGER_FIELDS = ("elapsedMs", "request", "revision", "generation", "observationVersion")
REPOSITORY_ROOT = Path(__file__).resolve().parents[1]


def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()


def count(value):
    return int(value) if value and value.isascii() and value.isdecimal() and len(value) <= 9 else 0


def outcome(value):
    return value if value in OUTCOMES else "Error"


@lru_cache(maxsize=1)
def source_locations():
    """Only tracked source filenames can enter an artifact, never paths from exceptions."""
    try:
        tracked = subprocess.check_output(
            ["git", "ls-files", "-z", "--", "*.cs", "*.fs"], cwd=REPOSITORY_ROOT,
            stderr=subprocess.DEVNULL, timeout=10).decode().split("\0")
        locations = {}
        for name in tracked:
            if not re.fullmatch(r"(?:src|tests)/[A-Za-z0-9_./-]+\.(?:cs|fs)", name) or ".." in name.split("/"):
                continue
            path = (REPOSITORY_ROOT / name).resolve()
            if path.is_relative_to(REPOSITORY_ROOT) and path.is_file():
                locations[name] = len(path.read_text().splitlines())
        return locations
    except (OSError, ValueError, UnicodeError, subprocess.SubprocessError):
        return {}


def safe_failure(message, stack):
    category = "unknown"
    # Map known prefixes to fixed labels. Never include exception text or arbitrary type names.
    for assertion in ("Equal", "NotEqual", "Single", "Empty", "NotEmpty", "True", "False", "Null",
                      "NotNull", "Contains", "DoesNotContain", "Throws", "ThrowsAny", "IsType", "Fail"):
        if (message or "").startswith(f"Assert.{assertion}() Failure"):
            category = "assert." + assertion.lower()
            break
    for prefix, label in (("System.TimeoutException", "timeout"),
                          ("System.OperationCanceledException", "cancelled"),
                          ("System.Threading.Tasks.TaskCanceledException", "cancelled"),
                          ("System.Net.Http.HttpRequestException", "http"),
                          ("System.AggregateException", "aggregate")):
        if re.match(re.escape(prefix) + r"(?:\s|:|$)", message or ""):
            category = label
            break
    locations = []
    for path, line in re.findall(r"\bin ([^\r\n]+):line ([0-9]{1,7})(?:\s|$)", (stack or "")[:65536]):
        for name, last_line in source_locations().items():
            if path.replace("\\", "/").endswith("/" + name) and 1 <= int(line) <= last_line:
                location = {"file": name, "line": int(line)}
                if location not in locations:
                    locations.append(location)
                break
        if len(locations) >= 8:
            break
    return {"category": category, "locations": locations}


def export_trx(source, destination):
    if source.stat().st_size > 32 * 1024 * 1024:
        raise ValueError("Report exceeds metadata export limit")
    original = ET.parse(source)
    methods = {}
    for test in original.findall(".//{*}UnitTest"):
        method = test.find("{*}TestMethod")
        if method is not None:
            symbol = f"{method.get('className', '')}.{method.get('name', '')}"
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.+`<>]{0,511}", symbol):
                methods[test.get("id")] = symbol
    root = ET.Element(f"{{{NS}}}TestRun", {"id": str(uuid.uuid5(uuid.NAMESPACE_URL, source.name))})
    definitions = ET.SubElement(root, f"{{{NS}}}TestDefinitions")
    results = ET.SubElement(root, f"{{{NS}}}Results")
    for result in original.findall(".//{*}UnitTestResult"):
        state = outcome(result.get("outcome"))
        test_id = str(uuid.UUID(hex=digest(result.get("testId", ""))[:32]))
        name = methods.get(result.get("testId"), "test-" + digest(result.get("testName", "")))
        definition = ET.SubElement(definitions, f"{{{NS}}}UnitTest", {"id": test_id, "name": name})
        ET.SubElement(definition, f"{{{NS}}}TestMethod", {
            "className": name.rsplit(".", 1)[0], "name": name.rsplit(".", 1)[-1],
        })
        entry = ET.SubElement(results, f"{{{NS}}}UnitTestResult", {
            "testName": name, "testId": test_id, "outcome": state,
        })
        if state != "Passed":
            error = ET.SubElement(ET.SubElement(entry, f"{{{NS}}}Output"), f"{{{NS}}}ErrorInfo")
            original_error = result.find("{*}Output/{*}ErrorInfo")
            failure = safe_failure(
                original_error.findtext("{*}Message") if original_error is not None else None,
                original_error.findtext("{*}StackTrace") if original_error is not None else None)
            ET.SubElement(error, f"{{{NS}}}Message").text = json.dumps(failure)
    original_summary = original.find(".//{*}ResultSummary")
    original_counts = original.find(".//{*}Counters")
    counts = {name: count(original_counts.get(name)) if original_counts is not None else 0 for name in COUNTERS}
    summary = ET.SubElement(root, f"{{{NS}}}ResultSummary", {
        "outcome": outcome(original_summary.get("outcome")) if original_summary is not None else "Error",
    })
    ET.SubElement(summary, f"{{{NS}}}Counters", {key: str(value) for key, value in counts.items()})
    ET.ElementTree(root).write(destination, encoding="utf-8", xml_declaration=True)
    return counts


def safe_phase(value):
    if not isinstance(value, dict) or value.get("phase") not in PHASES:
        return None
    result = {"phase": value["phase"]}
    for key in HASH_FIELDS:
        item = value.get(key)
        if isinstance(item, str) and re.fullmatch(r"[a-f0-9]{64}", item):
            result[key] = item
    for key in INTEGER_FIELDS:
        item = value.get(key)
        if type(item) is int and 0 <= item <= 2**63 - 1:
            result[key] = item
    status = value.get("status")
    if type(status) is int and 100 <= status <= 599:
        result["status"] = status
    return result


def safe_headroom(value):
    phase = value.get("phase", "")
    if not isinstance(phase, str) or not re.fullmatch(r"(?:before|after|failed)-(?:lab|run|backend(?:-[0-9]{1,3})?)", phase):
        return None
    result = {"phase": phase}
    if type(value.get("lab_unavailable")) is bool:
        result["lab_unavailable"] = value["lab_unavailable"]
    for key in ("disk_total", "disk_free"):
        number = value.get(key)
        if type(number) is int and 0 <= number <= 2**63 - 1:
            result[key] = number
    result["memory"] = {key: number for key in ("MemTotal", "MemAvailable")
                        if type(number := value.get("memory", {}).get(key)) is int and 0 <= number <= 2**63 - 1}
    result["targets"] = []
    for target in value.get("targets", [])[:8]:
        safe = {"nodes": [{key: node[key] for key in ("Ready", "DiskPressure", "MemoryPressure") if type(node.get(key)) is bool}
                          for node in target.get("nodes", [])[:32]]}
        if isinstance(target.get("id"), str) and re.fullmatch(r"[a-f0-9]{64}", target["id"]):
            safe["id"] = target["id"]
        if type(target.get("unavailable")) is bool:
            safe["unavailable"] = target["unavailable"]
        result["targets"].append(safe)
    return result


FRONTEND_OUTCOMES = {"passed", "failed", "pending", "todo", "skipped", "disabled", "error"}
FRONTEND_COUNTERS = (
    "numTotalTestSuites", "numPassedTestSuites", "numFailedTestSuites", "numPendingTestSuites",
    "numTotalTests", "numPassedTests", "numFailedTests", "numPendingTests", "numTodoTests",
)


def frontend_duration(value):
    if type(value) in (int, float) and 0 <= value <= 2**53 - 1 and math.isfinite(value):
        return value
    return None


def frontend_outcome(value):
    return value if isinstance(value, str) and value in FRONTEND_OUTCOMES else "error"


def export_frontend(source, destination):
    """Vitest's JSON contains arbitrary assertion messages and titles; copy no free-form text."""
    if source.stat().st_size > 32 * 1024 * 1024:
        raise ValueError("Report exceeds metadata export limit")
    original = json.loads(source.read_text())
    if not isinstance(original, dict) or type(original.get("success")) is not bool:
        raise ValueError("Invalid frontend report")
    counts = {}
    for key in FRONTEND_COUNTERS:
        number = original.get(key, 0)
        if type(number) is not int or not 0 <= number <= 999_999_999:
            raise ValueError("Invalid frontend counters")
        counts[key] = number
    if any(key not in original for key in ("numTotalTests", "numPassedTests", "numFailedTests")):
        raise ValueError("Missing frontend counters")
    results = original.get("testResults")
    if not isinstance(results, list) or len(results) > 10_000:
        raise ValueError("Invalid frontend suites")
    suites = []
    test_count = 0
    for suite_index, suite in enumerate(results):
        if not isinstance(suite, dict):
            raise ValueError("Invalid frontend suite")
        assertions = suite.get("assertionResults")
        if not isinstance(assertions, list) or len(assertions) > 20_000:
            raise ValueError("Invalid frontend assertions")
        test_count += len(assertions)
        if test_count > 100_000:
            raise ValueError("Frontend assertion limit exceeded")
        suite_name = suite.get("name")
        if not isinstance(suite_name, str):
            raise ValueError("Invalid frontend suite identity")
        suite_id = digest(suite_name)
        tests = []
        for test_index, assertion in enumerate(assertions):
            if not isinstance(assertion, dict):
                raise ValueError("Invalid frontend assertion")
            title = assertion.get("fullName", assertion.get("title", ""))
            if not isinstance(title, str):
                raise ValueError("Invalid frontend test identity")
            tests.append({
                "id": digest(json.dumps([suite_id, suite_index, title, test_index])),
                "outcome": frontend_outcome(assertion.get("status")),
                "duration_ms": frontend_duration(assertion.get("duration")),
            })
        start = frontend_duration(suite.get("startTime"))
        end = frontend_duration(suite.get("endTime"))
        suites.append({
            "id": suite_id,
            "outcome": frontend_outcome(suite.get("status")),
            "duration_ms": frontend_duration(end - start) if start is not None and end is not None else None,
            "tests": tests,
        })
    result = {"success": original["success"], "counts": counts, "suites": suites}
    destination.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n")
    return result


def export_evidence(source, phases, destination, frontend_report=None):
    destination.mkdir(parents=True, exist_ok=True)
    reports = []
    problems = 0
    for report in sorted(source.rglob("*.trx"))[:512]:
        filename = digest(str(report.relative_to(source))) + ".trx"
        try:
            counts = export_trx(report, destination / filename)
            reports.append({"file": filename, "counts": counts})
        except (ET.ParseError, OSError, ValueError):
            problems += 1
    exported_phases = 0
    for journal in sorted(phases.rglob("*.jsonl"))[:512]:
        try:
            if journal.stat().st_size > 1024 * 1024:
                problems += 1
                continue
            records = []
            for line in journal.read_text().splitlines()[:1024]:
                item = safe_phase(json.loads(line))
                if item is not None:
                    records.append(item)
            target = destination / (digest(str(journal.relative_to(phases))) + ".jsonl")
            target.write_text("".join(json.dumps(item) + "\n" for item in records))
            exported_phases += 1
        except (OSError, ValueError, TypeError, RecursionError):
            problems += 1
    headroom = []
    for snapshot in sorted(source.glob("headroom-*.json"))[:128]:
        try:
            if snapshot.stat().st_size > 128 * 1024:
                problems += 1
                continue
            item = safe_headroom(json.loads(snapshot.read_text()))
            if item is not None:
                headroom.append(item)
        except (OSError, ValueError, TypeError, AttributeError, RecursionError):
            problems += 1
    frontend_file = None
    if frontend_report is not None:
        try:
            (destination / "frontend.json").unlink(missing_ok=True)
            export_frontend(frontend_report, destination / "frontend.json")
            frontend_file = "frontend.json"
        except (OSError, ValueError, TypeError, OverflowError, RecursionError):
            problems += 1
    result = {"reports": reports, "phase_files": exported_phases, "headroom": headroom, "export_errors": problems}
    if frontend_report is not None:
        result["frontend_report"] = frontend_file
    (destination / "summary.json").write_text(json.dumps(result, indent=2) + "\n")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--phases-directory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--frontend-report", type=Path)
    args = parser.parse_args()
    result = export_evidence(args.input, args.phases_directory, args.output, args.frontend_report)
    print(f"Exported {len(result['reports'])} test reports and {result['phase_files']} phase journals; export errors: {result['export_errors']}.")
    return 1 if result["export_errors"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
