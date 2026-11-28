# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

import json
from pathlib import Path
import tempfile
import unittest

import template_evidence as evidence


def complete_report():
    return {"status": "passed", "mode": "complete", "fingerprint": "current",
            "tests": {suite: {"total": 1, "executed": 1, "passed": 1, "failed": 0,
                              "notExecuted": 0, "aborted": 0, "error": 0} for suite in evidence.SUITES}}


class TemplateEvidenceTests(unittest.TestCase):
    def test_accepts_only_the_requested_mode_and_matching_source(self):
        report = complete_report()
        self.assertEqual(report, evidence.validate_report(report, "complete", "current"))
        format_report = {"status": "passed", "mode": "format-only", "fingerprint": "current", "tests": {}}
        evidence.validate_report(format_report, "format-only", "current")
        for report, mode, fingerprint in [(format_report, "complete", "current"),
                                          (complete_report(), "format-only", "current"),
                                          (complete_report(), "complete", "different")]:
            with self.subTest(mode=mode), self.assertRaises(ValueError):
                evidence.validate_report(report, mode, fingerprint)

    def test_complete_evidence_rejects_empty_missing_unexpected_and_duplicate_suites(self):
        for tests in [{}, {evidence.SUITES[0]: complete_report()["tests"][evidence.SUITES[0]]},
                      {**complete_report()["tests"], "Unexpected.Tests": {}}]:
            report = complete_report()
            report["tests"] = tests
            with self.assertRaises(ValueError):
                evidence.validate_report(report, "complete", "current")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.json"
            # JSON objects must not silently overwrite a duplicate suite identity.
            suite = evidence.SUITES[0]
            raw = json.dumps(complete_report())
            raw = raw.replace('"tests": {', '"tests": {' + json.dumps(suite) + ': {}, ', 1)
            path.write_text(raw)
            with self.assertRaisesRegex(ValueError, "Duplicate"):
                evidence.load_report(path, "complete", "current")

    def test_complete_evidence_requires_nonzero_successful_consistent_integer_counters(self):
        for changes in [{"total": 0}, {"passed": 0}, {"executed": 0}, {"failed": 1},
                        {"notExecuted": 1}, {"aborted": 1}, {"error": 1}, {"total": True},
                        {"passed": "1"}, {"failed": -1}]:
            report = complete_report()
            report["tests"][evidence.SUITES[0]].update(changes)
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                evidence.validate_report(report, "complete", "current")
        report = complete_report()
        del report["tests"][evidence.SUITES[0]]["error"]
        with self.assertRaises(ValueError):
            evidence.validate_report(report, "complete", "current")

    def test_failed_running_and_wrong_shape_reports_never_pass(self):
        for status in ("failed", "running", None):
            report = complete_report()
            report["status"] = status
            with self.assertRaises(ValueError):
                evidence.validate_report(report, "complete", "current")
        for report in ([], None, "passed"):
            with self.assertRaises(ValueError):
                evidence.validate_report(report, "complete", "current")

    def test_project_inventory_is_exact_distinct_and_confined_to_the_expected_roles(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            projects = evidence.expected_projects(root)
            evidence.validate_projects(projects[::-1], root)
            for observed in ([], projects[:2], [projects[0], projects[0], projects[1]],
                             [*projects, root / "Extra.csproj"],
                             [*projects[:2], root / "elsewhere" / projects[2].name]):
                with self.subTest(projects=observed), self.assertRaises(ValueError):
                    evidence.validate_projects(observed, root)
