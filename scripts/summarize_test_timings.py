#!/usr/bin/env python3
"""Summarize opt-in lifecycle phases and TRX without exporting test arguments or errors."""

import argparse
from collections import Counter, defaultdict
from datetime import datetime
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
STATIC_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(?:[.+][A-Za-z_][A-Za-z0-9_]*|`[0-9]+)*$")
PHASES = frozenset("""database.reset seed.base mediator.services.register mediator.services.build
mediator.scope.create mediator.scope.dispose mediator.provider.dispose api.encryption-key
api.services.register api.services.build api.seed.compute-profile api.endpoints.map
api.host.start api.host.dispose gitops.seed.catalog fixture.mongo.container.start
fixture.mongo.replica-set.initialize fixture.mongo.client.create fixture.mongo.outbox.build
fixture.mongo.dispose""".split())
MONGO_COMMANDS = frozenset("""aggregate abortTransaction buildInfo collMod commitTransaction
count create createIndexes delete distinct drop dropDatabase dropIndexes endSessions find
findAndModify getMore hello insert isMaster killCursors listCollections listDatabases
listIndexes ping replSetGetStatus serverStatus update renameCollection validate""".split())
OUTCOMES = frozenset(("Passed", "Failed", "NotExecuted", "Aborted", "Error", "Timeout", "Inconclusive"))


def number(value):
    return float(value) if type(value) in (float, int) and math.isfinite(value) and value >= 0 else None


def identifier(value):
    value = str(value or "")
    if re.fullmatch(r"[a-fA-F0-9-]{32,64}", value):
        return value.lower()
    return hashlib.sha256(value.encode()).hexdigest()


def static_name(value, owner=False):
    if not isinstance(value, str) or not STATIC_NAME.fullmatch(value):
        return None
    return value if not owner or value.startswith("Pole.") else None


def timestamp(value):
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None:
        raise ValueError("timezone required")
    return result.timestamp()


def duration_ms(value):
    hours, minutes, seconds = value.split(":")
    days = 0
    if "." in hours:
        days, hours = hours.split(".")
    result = (int(days) * 86400 + int(hours) * 3600 + int(minutes) * 60 + float(seconds)) * 1000
    if not math.isfinite(result) or result < 0:
        raise ValueError("invalid duration")
    return result


def stats(values):
    values = sorted(values)
    return {"count": len(values), "total_ms": round(sum(values), 3),
            "median_ms": round(statistics.median(values), 3) if values else None,
            "p95_ms": round(values[math.ceil(len(values) * .95) - 1], 3) if values else None,
            "max_ms": round(values[-1], 3) if values else None}


def safe_phase(value):
    if value in PHASES:
        return value
    for prefix in ("api.migration.", "gitops.migration."):
        if isinstance(value, str) and value.startswith(prefix) and static_name(value[len(prefix):]):
            return value
    return None


def read_cases(directory, diagnostics):
    cases, assemblies, seen = [], set(), set()
    declared_cases, reports_with_counters = 0, 0
    reports = sorted(directory.rglob("*.trx"))
    for report in reports:
        try:
            root = ET.parse(report).getroot()
            counters = root.find(".//{*}Counters")
            if counters is not None and (counters.get("total") or "").isdecimal():
                declared_cases += int(counters.get("total"))
                reports_with_counters += 1
            definitions = {}
            for item in root.findall(".//{*}UnitTest"):
                method = item.find("{*}TestMethod")
                if method is None:
                    continue
                owner = static_name(method.get("className"), owner=True)
                name = static_name(method.get("name"))
                assembly = static_name(Path(method.get("codeBase", "").replace("\\", "/")).stem)
                definitions[item.get("id")] = (owner, name, assembly)
                if assembly:
                    assemblies.add(assembly)
            for item in root.findall(".//{*}UnitTestResult"):
                owner, method, assembly = definitions.get(item.get("testId"), (None, None, None))
                if not owner or not method:
                    diagnostics["invalid_case_metadata"] += 1
                started = timestamp(item.get("startTime", ""))
                finished = timestamp(item.get("endTime", ""))
                if finished < started:
                    raise ValueError("invalid interval")
                test_id, execution_id = identifier(item.get("testId")), identifier(item.get("executionId"))
                key = (assembly, test_id, execution_id, started, finished)
                if key in seen:
                    diagnostics["duplicate_cases"] += 1
                    continue
                seen.add(key)
                cases.append({"owner": owner or "Unknown", "method": method or "Unknown",
                              "assembly": assembly or "Unknown", "test_id": test_id,
                              "execution_id": execution_id, "started": started, "finished": finished,
                              "reported_ms": duration_ms(item.get("duration", "0:0:0")),
                              "interval_ms": (finished - started) * 1000,
                              "outcome": item.get("outcome") if item.get("outcome") in OUTCOMES else "Unknown",
                              "phases": []})
        except (OSError, ET.ParseError, ValueError, TypeError, AttributeError):
            diagnostics["invalid_trx_reports"] += 1
    return cases, assemblies, len(reports), declared_cases if reports_with_counters == len(reports) and reports else None


def read_phases(directory, diagnostics):
    records = []
    files = sorted(directory.rglob("timing-*.jsonl")) if directory.exists() else []
    for path in files:
        try:
            with path.open() as stream:
                for line in stream:
                    try:
                        raw = json.loads(line)
                        owner = static_name(raw.get("owner"), owner=True)
                        phase, elapsed = safe_phase(raw.get("phase")), number(raw.get("elapsedMs"))
                        started, finished = timestamp(raw.get("startedUtc", "")), timestamp(raw.get("finishedUtc", ""))
                        if not owner or not phase or elapsed is None or finished < started or raw.get("kind") not in ("test", "fixture"):
                            raise ValueError("invalid timing metadata")
                        mongo = []
                        for command in raw.get("mongo", []):
                            count, failed = command.get("count"), command.get("failed")
                            ms = number(command.get("elapsedMs"))
                            if command.get("command") not in MONGO_COMMANDS or type(count) is not int or type(failed) is not int or not 0 <= failed <= count or ms is None:
                                diagnostics["invalid_mongo_records"] += 1
                                continue
                            mongo.append({"command": command["command"], "count": count, "failed": failed, "elapsed_ms": ms})
                        records.append({"owner": owner, "instance": identifier(raw.get("instance")),
                                        "kind": raw["kind"], "phase": phase, "elapsed_ms": elapsed,
                                        "started": started, "finished": finished, "mongo": mongo})
                    except (ValueError, TypeError, AttributeError):
                        diagnostics["invalid_timing_records"] += 1
        except (OSError, UnicodeError):
            diagnostics["invalid_timing_files"] += 1
    return records, len(files)


def phase_summary(records):
    grouped, commands = defaultdict(list), defaultdict(lambda: {"count": 0, "failed": 0, "elapsed_ms": 0.0})
    for record in records:
        grouped[record["phase"]].append(record["elapsed_ms"])
        for item in record["mongo"]:
            summary = commands[(record["phase"], item["command"])]
            for field in summary:
                summary[field] += item[field]
    return {"phases": [{"phase": name, **stats(values)} for name, values in sorted(grouped.items(), key=lambda item: sum(item[1]), reverse=True)],
            "mongo": [{"phase": phase, "command": command, **value} for (phase, command), value in sorted(commands.items())]}


def union_ms(records):
    total, end = 0.0, float("-inf")
    for record in sorted(records, key=lambda item: item["started"]):
        total += max(0, record["finished"] - max(end, record["started"]))
        end = max(end, record["finished"])
    return total * 1000


def inventory(directory, observed, diagnostics):
    expected, wall_seconds, manifest_available = set(), None, False
    paths = [directory / "backend-manifest.json", directory / "receipt.json"]
    sibling = directory.parent / "receipt.json"
    if sibling.exists() and sibling not in paths:
        try:
            receipt = json.loads(sibling.read_text())
            if any(f"/{directory.name}/" in str(step.get("log", "")) for step in receipt.get("steps", [])):
                paths.append(sibling)
        except (OSError, ValueError, TypeError, AttributeError):
            diagnostics["invalid_sibling_receipts"] += 1
    for path in paths:
        if not path.exists():
            continue
        try:
            manifest = json.loads(path.read_text())
            manifest_available = True
            if number(manifest.get("seconds")) is not None:
                wall_seconds = manifest["seconds"]
            for name in manifest.get("projects", []):
                if not isinstance(name, str) or not re.fullmatch(r"(?:src|tests)/[A-Za-z0-9_./-]+\.(?:csproj|fsproj)", name) or ".." in name.split("/"):
                    diagnostics["invalid_inventory_entries"] += 1
                    continue
                project = ROOT / name
                assembly = ET.parse(project).findtext(".//AssemblyName") if project.exists() else None
                expected.add(assembly or Path(name).stem)
        except (OSError, ValueError, TypeError, ET.ParseError):
            diagnostics["invalid_manifests"] += 1
    return {"manifest_available": manifest_available, "expected_assemblies": sorted(expected),
            "observed_assemblies": sorted(observed), "missing_assemblies": sorted(expected - observed),
            "unexpected_assemblies": sorted(observed - expected) if manifest_available else [],
            "wall_seconds": wall_seconds}


def frontend_summary(directory, diagnostics):
    path = directory / "frontend.json"
    if not path.exists():
        return {"available": False, "cases": []}
    try:
        raw = json.loads(path.read_text())
        cases = []
        for suite in raw.get("testResults", []):
            file = str(suite.get("name", "")).replace("\\", "/")
            marker = "src/api/frontend/"
            file = file.split(marker, 1)[-1] if marker in file else Path(file).name
            if not re.fullmatch(r"[A-Za-z0-9_./-]+\.(?:[cm]?[jt]sx?)", file) or ".." in file.split("/"):
                file = "Unknown"
            for test in suite.get("assertionResults", []):
                identity = json.dumps([file, test.get("fullName", ""), len(cases)])
                cases.append({"id": identifier(identity), "index": len(cases), "file": file,
                              "status": test.get("status") if test.get("status") in ("passed", "failed", "pending", "todo", "skipped") else "unknown",
                              "duration_ms": number(test.get("duration"))})
        expected = raw.get("numTotalTests") if type(raw.get("numTotalTests")) is int else None
        return {"available": True, "expected_cases": expected, "discovered_cases": len(cases),
                "inventory_gap": expected is not None and expected != len(cases),
                "outcomes": dict(Counter(case["status"] for case in cases)),
                "missing_duration_cases": sum(case["duration_ms"] is None for case in cases),
                "durations": stats([case["duration_ms"] for case in cases if case["duration_ms"] is not None]), "cases": cases}
    except (OSError, ValueError, TypeError, AttributeError):
        diagnostics["invalid_frontend_reports"] += 1
        return {"available": True, "valid": False, "cases": []}


def summarize(results_directory, timings_directory):
    diagnostics = Counter()
    cases, assemblies, report_count, declared_cases = read_cases(results_directory, diagnostics)
    records, phase_file_count = read_phases(timings_directory, diagnostics)
    by_owner, unmatched, ambiguous, fixtures, matched = defaultdict(list), [], [], [], []
    for case in cases:
        by_owner[case["owner"]].append(case)
    for record in records:
        if record["kind"] == "fixture":
            fixtures.append(record)
            continue
        candidates = [case for case in by_owner[record["owner"]] if case["started"] <= record["started"] and record["finished"] <= case["finished"]]
        if len(candidates) == 1:
            candidates[0]["phases"].append(record)
            matched.append(record)
        elif candidates:
            ambiguous.append(record)
        else:
            unmatched.append(record)
    classes = []
    for owner, members in by_owner.items():
        class_records = [phase for case in members for phase in case["phases"]]
        classes.append({"owner": owner, "cases": len(members),
                        "profiled_cases": sum(bool(case["phases"]) for case in members),
                        "reported": stats([case["reported_ms"] for case in members]),
                        "intervals": stats([case["interval_ms"] for case in members]), **phase_summary(class_records)})
    classes.sort(key=lambda item: item["intervals"]["total_ms"], reverse=True)
    for case in cases:
        measured = sum(record["elapsed_ms"] for record in case["phases"])
        phase_union = union_ms(case["phases"])
        case["measured_setup_ms"] = measured
        case["phase_interval_union_ms"] = phase_union
        case["overlapping_phase_interval_ms"] = max(0, sum((record["finished"] - record["started"]) * 1000 for record in case["phases"]) - phase_union)
        residual = case["interval_ms"] - case["reported_ms"] - measured
        case["unattributed_ms"] = max(0, residual)
        case["overaccounted_ms"] = max(0, -residual)
    result = {"schema_version": 1, "backend": {
        "reports": report_count, "cases": len(cases), "declared_cases": declared_cases,
        "case_inventory_gap": declared_cases is not None and declared_cases != len(cases), "outcomes": dict(Counter(case["outcome"] for case in cases)),
        "reported": stats([case["reported_ms"] for case in cases]),
        "intervals": stats([case["interval_ms"] for case in cases]),
        "profiled_cases": sum(bool(case["phases"]) for case in cases),
        "unprofiled_cases": sum(not case["phases"] for case in cases),
        "timing_files": phase_file_count, "matched_phase_records": len(matched),
        "unmatched_phase_records": len(unmatched), "ambiguous_phase_records": len(ambiguous),
        "inventory": inventory(results_directory, assemblies, diagnostics),
        "classes": classes, "case_records": sorted(cases, key=lambda item: item["interval_ms"], reverse=True),
        "unmatched": unmatched, "ambiguous": ambiguous, **phase_summary(matched)},
        "fixtures": {"records": fixtures, **phase_summary(fixtures)},
        "frontend": frontend_summary(results_directory, diagnostics),
        "diagnostics": diagnostics,
        "limitations": [
            "TRX reported duration includes constructor/synchronous disposal; xUnit v2 async lifetime setup/cleanup are not included.",
            "Case intervals, phase totals and reported durations are cumulative across parallel cases and must not be added to infer wall time.",
            "Unattributed is max(0, interval - reported duration - measured phases); it is an approximation, not measured scenario time.",
            "Only explicitly instrumented lifecycle phases have setup detail; all other cases remain visible as unprofiled.",
            "Mongo counters cover explicit measured phases only, not scenario/body commands.",
            "Fixture records are separate from test cases. Existing E2E journals are separate evidence and are not interpreted here.",
            "Matching requires same class and enclosing UTC interval; no nearest-case or forced assignment is performed."]}
    return result


def markdown(result):
    backend = result["backend"]
    lines = ["# Test timing profile", "", f"Backend: **{backend['cases']} cases**, {backend['reports']} TRX reports; "
             f"**{backend['profiled_cases']} profiled**, **{backend['unprofiled_cases']} without lifecycle detail.",
             f"Phase records: {backend['matched_phase_records']} matched, {backend['unmatched_phase_records']} unmatched, "
             f"{backend['ambiguous_phase_records']} ambiguous.", "",
             f"Recorded gate wall time: {backend['inventory']['wall_seconds']} seconds (null means unavailable).",
             f"Cumulative reported duration: {backend['reported']['total_ms'] / 1000:.3f} s; "
             f"cumulative case intervals: {backend['intervals']['total_ms'] / 1000:.3f} s. These are not wall time.", "",
             "## Classes by cumulative interval", "", "| Class | Cases | Profiled | Reported s | Interval s |",
             "| --- | ---: | ---: | ---: | ---: |"]
    for item in backend["classes"][:40]:
        lines.append(f"| `{item['owner']}` | {item['cases']} | {item['profiled_cases']} | {item['reported']['total_ms']/1000:.3f} | {item['intervals']['total_ms']/1000:.3f} |")
    for title, values in (("Measured test phases", backend["phases"]), ("Fixture phases", result["fixtures"]["phases"])):
        lines += ["", f"## {title}", "", "| Phase | Samples | Total s | Median ms | P95 ms |", "| --- | ---: | ---: | ---: | ---: |"]
        for item in values:
            lines.append(f"| `{item['phase']}` | {item['count']} | {item['total_ms']/1000:.3f} | {item['median_ms']:.3f} | {item['p95_ms']:.3f} |")
    lines += ["", "## Cases by interval", "", "Case identifiers distinguish theory rows without exposing argument values.", "",
              "| Case | Execution ID | Reported ms | Interval ms | Measured setup ms | Unattributed ms |",
              "| --- | --- | ---: | ---: | ---: | ---: |"]
    for case in backend["case_records"][:40]:
        lines.append(f"| `{case['owner']}.{case['method']}` | `{case['execution_id']}` | {case['reported_ms']:.3f} | {case['interval_ms']:.3f} | {case['measured_setup_ms']:.3f} | {case['unattributed_ms']:.3f} |")
    lines += ["", "## Mongo commands within measured phases", "", "| Phase | Command | Calls | Failed | Cumulative ms |", "| --- | --- | ---: | ---: | ---: |"]
    for item in sorted(backend["mongo"], key=lambda item: item["elapsed_ms"], reverse=True)[:40]:
        lines.append(f"| `{item['phase']}` | `{item['command']}` | {item['count']} | {item['failed']} | {item['elapsed_ms']:.3f} |")
    lines += ["", "## Inventory and limitations", "", f"Missing assemblies: {', '.join(backend['inventory']['missing_assemblies']) or 'none detected'}.",
              f"Unexpected assemblies: {', '.join(backend['inventory']['unexpected_assemblies']) or 'none detected'}.",
              f"Inventory manifest available: {backend['inventory']['manifest_available']}; declared case count: {backend['declared_cases']}; case inventory gap: {backend['case_inventory_gap']}.",
              f"Frontend: {result['frontend'].get('discovered_cases', 0)} discovered cases; report available: {result['frontend']['available']}.",
              f"Parsing/validation diagnostics: {json.dumps(dict(result['diagnostics']), sort_keys=True)}.", ""]
    lines += [f"- {item}" for item in result["limitations"]]
    lines += ["", "Full per-case, Mongo command, percentile and unmatched records are in `timings.json`."]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-directory", type=Path, required=True)
    parser.add_argument("--timings-directory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = summarize(args.results_directory, args.timings_directory)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "timings.json").write_text(json.dumps(result, indent=2) + "\n")
    (args.output / "timings.md").write_text(markdown(result))
    print(f"Summarized {result['backend']['cases']} backend cases; {result['backend']['profiled_cases']} have matched lifecycle phases.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
