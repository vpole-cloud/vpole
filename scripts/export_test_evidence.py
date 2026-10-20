#!/usr/bin/env python3
"""Export allowlisted test metadata; never publish captured output or response bodies."""

import argparse
import hashlib
import json
from pathlib import Path
import re
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


def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()


def count(value):
    return int(value) if value and value.isascii() and value.isdecimal() and len(value) <= 9 else 0


def outcome(value):
    return value if value in OUTCOMES else "Error"


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
            ET.SubElement(error, f"{{{NS}}}Message").text = "Test did not pass; inspect the bounded phase journal."
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


def export_evidence(source, phases, destination):
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
    result = {"reports": reports, "phase_files": exported_phases, "export_errors": problems}
    (destination / "summary.json").write_text(json.dumps(result, indent=2) + "\n")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--phases-directory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = export_evidence(args.input, args.phases_directory, args.output)
    print(f"Exported {len(result['reports'])} test reports and {result['phase_files']} phase journals; export errors: {result['export_errors']}.")
    return 1 if result["export_errors"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
