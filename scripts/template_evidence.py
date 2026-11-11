"""Independent admission rules for complete versus formatting-only generated-feature evidence."""

import json
from pathlib import Path

FEATURE = "FeatureTemplateProbe"
SUITES = (f"{FEATURE}.Tests", "Pole.Tests.Architecture", f"{FEATURE}.Tests.Integration")
COUNTERS = ("total", "executed", "passed", "failed", "notExecuted", "aborted", "error")


def expected_projects(root):
    feature = Path(root) / "src/features" / FEATURE
    return [feature / f"{FEATURE}.Tests/{FEATURE}.Tests.csproj",
            Path(root) / "tests/Pole.Tests.Architecture/Pole.Tests.Architecture.csproj",
            feature / f"{FEATURE}.Tests.Integration/{FEATURE}.Tests.Integration.csproj"]


def validate_projects(projects, root):
    observed = [Path(project).resolve() for project in projects]
    expected = [project.resolve() for project in expected_projects(root)]
    if len(observed) != len(expected) or set(observed) != set(expected):
        raise ValueError("Complete generated verification requires exactly unit, architecture and integration projects")


def validate_report(report, mode, fingerprint=None):
    if mode not in {"complete", "format-only"}:
        raise ValueError("Unknown generated verification mode")
    if not isinstance(report, dict):
        raise ValueError("Invalid generated verification report")
    if report.get("status") != "passed" or report.get("mode") != mode:
        raise ValueError("Generated evidence did not pass the requested verification mode")
    if fingerprint is not None and report.get("fingerprint") != fingerprint:
        raise ValueError("Generated evidence does not match the current source")
    tests = report.get("tests")
    if mode == "format-only":
        if tests != {}:
            raise ValueError("Formatting-only evidence must not claim behavioral test results")
        return report
    if not isinstance(tests, dict) or set(tests) != set(SUITES):
        raise ValueError("Complete generated evidence requires exactly unit, architecture and integration suites")
    for suite, counts in tests.items():
        if (not isinstance(counts, dict)
                or any(type(counts.get(key)) is not int or counts[key] < 0 for key in COUNTERS)):
            raise ValueError(f"Invalid generated test counters: {suite}")
        if (counts["total"] == 0 or counts["executed"] != counts["total"] or counts["passed"] != counts["total"]
                or any(counts[key] for key in ("failed", "notExecuted", "aborted", "error"))):
            raise ValueError(f"Generated tests failed, skipped or were not discovered: {suite}")
    return report


def load_report(path, mode, fingerprint=None):
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"Duplicate generated evidence field: {key}")
            result[key] = value
        return result
    return validate_report(json.loads(Path(path).read_text(), object_pairs_hook=unique_object), mode, fingerprint)
