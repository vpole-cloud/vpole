#!/usr/bin/env python3
"""Verify the frozen E2E case multiset and the two explicitly approved journey merges.

Only code identifiers and SHA-256 display identities leave the TRX boundary. Theory
arguments, exception messages and output are never copied into the report.
"""

import argparse
from collections import Counter
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_MANIFEST = ROOT / "tests/Pole.Tests.E2E/journey-inventory.json"
OWNER = re.compile(r"Pole\.Tests\.E2E\.[A-Za-z_][A-Za-z0-9_.]*\Z")
METHOD = re.compile(r"[A-Za-z_][A-Za-z0-9_]*\Z")
HASH = re.compile(r"[0-9a-f]{64}\Z")


def identity(case):
    return case["owner"], case["method"], case["displaySha256"]


def safe_case(key, count):
    return {"owner": key[0], "method": key[1], "displaySha256": key[2], "count": count}


def validate_manifest(manifest):
    if manifest.get("version") != 1 or not manifest.get("cases"):
        raise ValueError("A version-one manifest with frozen cases is required")
    sources = Counter()
    targets = {}
    for entry in manifest["cases"]:
        for side in ("source", "target"):
            case = entry[side]
            if not (OWNER.fullmatch(case["owner"]) and METHOD.fullmatch(case["method"])
                    and HASH.fullmatch(case["displaySha256"])):
                raise ValueError("Manifest identifiers must be code names and SHA-256 hashes")
        if not entry.get("phase") or not entry.get("guarantees"):
            raise ValueError("Every frozen case needs a named phase and explicit guarantees")
        if any(key not in manifest["guarantees"] for key in entry["guarantees"]):
            raise ValueError("A case refers to an undefined guarantee")
        source, target = identity(entry["source"]), identity(entry["target"])
        if not entry.get("merge") and source != target:
            raise ValueError("Unmerged cases must preserve their exact frozen identity")
        sources[source] += 1
        if source in targets and targets[source] != target:
            raise ValueError("One source case cannot map to different targets")
        targets[source] = target
    if sum(sources.values()) != manifest["baselineCaseCount"]:
        raise ValueError("Frozen baseline count disagrees with its cases")
    # Unchanged duplicate Theory displays retain their multiplicity. Only named
    # merge groups may collapse different original cases into one target case.
    expected = Counter()
    grouped = {}
    for entry in manifest["cases"]:
        target = identity(entry["target"])
        merge = entry.get("merge")
        if merge:
            if merge not in manifest.get("merges", {}):
                raise ValueError("A case refers to an undefined merge")
            if merge in grouped and grouped[merge] != target:
                raise ValueError("All cases of one merge must share one target")
            grouped[merge] = target
        else:
            expected[target] += 1
    for target in grouped.values():
        if target in expected:
            raise ValueError("A merge target cannot overlap an unchanged case")
        expected[target] += 1
    if sum(expected.values()) != manifest["plannedCaseCount"]:
        raise ValueError("Planned count disagrees with explicit merge groups")
    if len(set(grouped.values())) != len(grouped):
        raise ValueError("Different merges cannot share a target")
    for name, merge in manifest.get("merges", {}).items():
        members = [entry for entry in manifest["cases"] if entry.get("merge") == name]
        if len(members) != merge["sourceCaseCount"] or len({identity(e["source"]) for e in members}) != len(members):
            raise ValueError("A merge must enumerate its distinct frozen source cases")
    return sources, expected


def read_trx(path):
    root = ET.parse(path).getroot()
    definitions = {}
    errors = []
    for test in root.findall("./{*}TestDefinitions/{*}UnitTest"):
        method = test.find("./{*}TestMethod")
        if method is None:
            continue
        owner, name = method.get("className", ""), method.get("name", "")
        if not (OWNER.fullmatch(owner) and METHOD.fullmatch(name)):
            errors.append("TRX contains an invalid E2E code identifier")
            continue
        assembly = Path(method.get("codeBase", "").replace("\\", "/")).name
        if assembly and assembly.lower() != "pole.tests.e2e.dll":
            errors.append("TRX contains a different test assembly")
        if test.get("id") in definitions:
            errors.append("TRX repeats a test definition ID")
        definitions[test.get("id")] = (owner, name)
    cases, executions, outcomes, intervals = Counter(), Counter(), Counter(), {}
    results = root.findall("./{*}Results/{*}UnitTestResult")
    if not results:
        errors.append("TRX contains no test results")
    for result in results:
        definition = definitions.get(result.get("testId"))
        display = result.get("testName", "")
        if definition is None or not display:
            errors.append("TRX result lacks its E2E definition or display identity")
            continue
        key = (*definition, hashlib.sha256(display.encode()).hexdigest())
        cases[key] += 1
        if result.get("startTime") and result.get("endTime"):
            intervals.setdefault(definition, []).append((
                datetime.fromisoformat(result.get("startTime").replace("Z", "+00:00")),
                datetime.fromisoformat(result.get("endTime").replace("Z", "+00:00"))))
        outcomes[result.get("outcome", "Unknown")] += 1
        execution = result.get("executionId")
        if not execution:
            errors.append("TRX result lacks an execution ID")
        else:
            executions[execution] += 1
    if any(count != 1 for count in executions.values()):
        errors.append("TRX repeats an execution ID")
    if any(outcome != "Passed" for outcome in outcomes):
        errors.append("Every E2E case must pass without failures or skips")
    counters = root.find("./{*}ResultSummary/{*}Counters")
    if counters is None:
        errors.append("TRX lacks final result counters")
    else:
        for key in ("total", "executed", "passed"):
            if int(counters.get(key, "-1")) != len(results):
                errors.append("TRX result counters disagree with passed executions")
                break
        if any(int(value) for key, value in counters.attrib.items()
               if key not in ("total", "executed", "passed")):
            errors.append("TRX counters contain a failed or nonexecuted result")
    return cases, outcomes, errors, intervals


def verify(manifest, trx, mode="planned", timings_directory=None):
    baseline, planned = validate_manifest(manifest)
    expected = baseline if mode == "baseline" else planned
    observed, outcomes, errors, intervals = read_trx(trx)
    missing = expected - observed
    duplicate = Counter({key: count - expected[key] for key, count in observed.items()
                         if key in expected and count > expected[key]})
    additions = Counter({key: count for key, count in observed.items() if key not in expected})
    if missing:
        errors.append("Frozen E2E cases are missing")
    if duplicate or any(count != 1 for count in additions.values()):
        errors.append("E2E display identities have unexpected duplicate executions")
    if mode == "planned" and any(key in baseline and key not in planned for key in additions):
        errors.append("A retired source case remains outside its approved journey")
    missing_phases = []
    if timings_directory is not None and mode == "planned":
        observed_phases = {}
        required = {}
        for entry in manifest["cases"]:
            if entry.get("merge"):
                target = entry["target"]
                required.setdefault((target["owner"], target["method"]), set()).add(entry["phase"])
        try:
            for path in Path(timings_directory).glob("timing-*.jsonl"):
                for line in path.read_text().splitlines():
                    record = json.loads(line)
                    if record.get("kind") != "test" or not record.get("instance"):
                        continue
                    owner = record.get("owner")
                    started = datetime.fromisoformat(record["startedUtc"].replace("Z", "+00:00"))
                    finished = datetime.fromisoformat(record["finishedUtc"].replace("Z", "+00:00"))
                    for target in required:
                        if target[0] == owner and any(begin <= started <= finished <= end
                                                     for begin, end in intervals.get(target, [])):
                            observed_phases.setdefault((target, record["instance"]), set()).add(record.get("phase"))
        except (OSError, ValueError, KeyError, TypeError):
            errors.append("Timing evidence cannot be read completely")
        for target, phases in required.items():
            # Both old cases must execute in one current journey instance; a
            # stale run or another test instance cannot fill the missing phase.
            candidates = [seen for (case, _), seen in observed_phases.items() if case == target]
            if not any(phases <= seen for seen in candidates):
                missing_phases.extend({"owner": target[0], "phase": phase} for phase in sorted(phases))
        if missing_phases:
            errors.append("Approved journey assertion phases are missing from timing evidence")
    return {
        "status": "passed" if not errors else "failed",
        "mode": mode,
        "baselineCommit": manifest["baselineCommit"],
        "baselineCases": sum(baseline.values()),
        "expectedCases": sum(expected.values()),
        "observedCases": sum(observed.values()),
        "outcomes": dict(outcomes),
        "missing": [safe_case(k, v) for k, v in sorted(missing.items())],
        "duplicates": [safe_case(k, v) for k, v in sorted(duplicate.items())],
        "additionalRegressionCases": [safe_case(k, v) for k, v in sorted(additions.items())],
        "phaseEvidence": "checked" if timings_directory is not None and mode == "planned" else "not-checked",
        "missingPhases": missing_phases,
        "errors": sorted(set(errors)),
        "limitation": "Case/phase identity proves inventory, not assertion semantics; review the guarantee map and source diff.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--trx", type=Path, required=True)
    parser.add_argument("--mode", choices=("baseline", "planned"), default="planned")
    parser.add_argument("--timings-directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        report = verify(json.loads(args.manifest.read_text()), args.trx, args.mode, args.timings_directory)
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError):
        report = {"status": "failed", "errors": ["Invalid manifest, TRX or timing evidence"]}
    encoded = json.dumps(report, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(encoded)
    sys.stdout.write(encoded)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
