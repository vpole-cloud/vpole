# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET


SPEC = importlib.util.spec_from_file_location("journeys", Path(__file__).parents[1] / "verify_e2e_journeys.py")
journeys = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(journeys)


def case(owner, method, suffix=""):
    owner = "Pole.Tests.E2E." + owner
    return {"owner": owner, "method": method,
            "displaySha256": hashlib.sha256((owner + "." + method + suffix).encode()).hexdigest()}


def manifest():
    target = case("CombinedJourney", "Runs")
    first, second = case("OriginalA", "First"), case("OriginalB", "Second")
    theory = [case("Helpers", "Theory", suffix) for suffix in ('(value: "secret-canary")', '(value: "second")')]
    return {"version": 1, "baselineCommit": "baseline", "baselineCaseCount": 4, "plannedCaseCount": 3,
            "guarantees": {"a": "Original A assertions", "b": "Original B assertions", "theory": "Exact Theory rows"},
            "merges": {"combined": {"sourceCaseCount": 2}},
            "cases": [{"source": first, "target": target, "merge": "combined", "phase": "e2e.first", "guarantees": ["a"]},
                      {"source": second, "target": target, "merge": "combined", "phase": "e2e.second", "guarantees": ["b"]}]
                     + [{"source": c, "target": c, "phase": "helper", "guarantees": ["theory"]} for c in theory]}


class JourneyInventoryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.manifest = manifest()

    def write_trx(self, rows, name="results.trx"):
        root = ET.Element("TestRun")
        definitions = ET.SubElement(root, "TestDefinitions")
        results = ET.SubElement(root, "Results")
        for index, row in enumerate(rows):
            owner, method, suffix, outcome = row
            owner = "Pole.Tests.E2E." + owner
            definition = ET.SubElement(definitions, "UnitTest", id=f"test-{index}")
            ET.SubElement(definition, "TestMethod", className=owner, name=method, codeBase="Pole.Tests.E2E.dll")
            ET.SubElement(results, "UnitTestResult", testId=f"test-{index}", executionId=f"execution-{index}",
                          testName=owner + "." + method + suffix, outcome=outcome,
                          startTime="2026-11-09T12:00:00+00:00", endTime="2026-11-09T12:01:00+00:00")
        summary = ET.SubElement(root, "ResultSummary", outcome="Completed")
        ET.SubElement(summary, "Counters", total=str(len(rows)), executed=str(len(rows)),
                      passed=str(sum(row[3] == "Passed" for row in rows)))
        path = self.root / name
        ET.ElementTree(root).write(path)
        return path

    def planned_rows(self):
        return [("CombinedJourney", "Runs", "", "Passed"),
                ("Helpers", "Theory", '(value: "secret-canary")', "Passed"),
                ("Helpers", "Theory", '(value: "second")', "Passed")]

    def test_approved_merge_counts_two_original_cases_once(self):
        report = journeys.verify(self.manifest, self.write_trx(self.planned_rows()))
        self.assertEqual("passed", report["status"])
        self.assertEqual((4, 3, 3), (report["baselineCases"], report["expectedCases"], report["observedCases"]))
        self.assertEqual([], report["additionalRegressionCases"])
        self.assertNotIn("secret-canary", json.dumps(report))

    def test_baseline_mode_accepts_the_frozen_original_cases(self):
        rows = [("OriginalA", "First", "", "Passed"), ("OriginalB", "Second", "", "Passed")]
        rows += self.planned_rows()[1:]
        report = journeys.verify(self.manifest, self.write_trx(rows), mode="baseline")
        self.assertEqual("passed", report["status"])
        self.assertEqual(4, report["expectedCases"])

    def test_missing_theory_row_cannot_be_replaced_by_another_new_row(self):
        rows = self.planned_rows()
        rows[-1] = ("Helpers", "Theory", '(value: "replacement-canary")', "Passed")
        report = journeys.verify(self.manifest, self.write_trx(rows))
        self.assertEqual("failed", report["status"])
        self.assertEqual(1, len(report["missing"]))
        self.assertEqual(1, len(report["additionalRegressionCases"]))
        self.assertNotIn("canary", json.dumps(report))

    def test_duplicate_theory_execution_is_rejected(self):
        rows = self.planned_rows()
        report = journeys.verify(self.manifest, self.write_trx(rows + [rows[-1]]))
        self.assertEqual("failed", report["status"])
        self.assertEqual(1, report["duplicates"][0]["count"])

    def test_frozen_duplicate_display_multiplicity_is_preserved(self):
        entry = copy.deepcopy(self.manifest["cases"][-1])
        self.manifest["cases"].append(entry)
        self.manifest["baselineCaseCount"] += 1
        self.manifest["plannedCaseCount"] += 1
        rows = self.planned_rows()
        self.assertEqual("passed", journeys.verify(self.manifest, self.write_trx(rows + [rows[-1]]))["status"])
        self.assertEqual("failed", journeys.verify(self.manifest, self.write_trx(rows))["status"])

    def test_additional_regressions_are_reported_separately(self):
        rows = self.planned_rows() + [("Regressions", "New_case", "", "Passed")]
        report = journeys.verify(self.manifest, self.write_trx(rows))
        self.assertEqual("passed", report["status"])
        self.assertEqual("New_case", report["additionalRegressionCases"][0]["method"])

    def test_additional_regression_cannot_run_twice(self):
        row = ("Regressions", "New_case", "", "Passed")
        report = journeys.verify(self.manifest, self.write_trx(self.planned_rows() + [row, row]))
        self.assertEqual("failed", report["status"])

    def test_retired_source_cannot_remain_as_an_additional_regression(self):
        rows = self.planned_rows() + [("OriginalA", "First", "", "Passed")]
        report = journeys.verify(self.manifest, self.write_trx(rows))
        self.assertIn("A retired source case remains outside its approved journey", report["errors"])

    def test_skips_failures_and_counter_disagreement_are_rejected(self):
        for outcome in ("Failed", "NotExecuted", "Unknown"):
            with self.subTest(outcome=outcome):
                rows = self.planned_rows()
                rows[0] = (*rows[0][:3], outcome)
                report = journeys.verify(self.manifest, self.write_trx(rows))
                self.assertEqual("failed", report["status"])

    def test_duplicate_execution_id_is_rejected(self):
        path = self.write_trx(self.planned_rows())
        root = ET.parse(path)
        root.findall("./Results/UnitTestResult")[1].set("executionId", "execution-0")
        root.write(path)
        self.assertIn("TRX repeats an execution ID", journeys.verify(self.manifest, path)["errors"])

    def test_different_assembly_is_rejected(self):
        path = self.write_trx(self.planned_rows())
        root = ET.parse(path)
        root.find("./TestDefinitions/UnitTest/TestMethod").set("codeBase", "Other.Tests.dll")
        root.write(path)
        self.assertIn("TRX contains a different test assembly", journeys.verify(self.manifest, path)["errors"])

    def test_collapsing_an_unchanged_theory_row_requires_a_named_merge(self):
        self.manifest["cases"][-1]["target"] = self.manifest["cases"][-2]["target"]
        self.manifest["plannedCaseCount"] -= 1
        with self.assertRaises(ValueError):
            journeys.validate_manifest(self.manifest)

    def write_timings(self, phases, instance="current", started="2026-11-09T12:00:10+00:00", name="timing-1.jsonl"):
        records = [{"owner": "Pole.Tests.E2E.CombinedJourney", "instance": instance, "kind": "test", "phase": phase,
                    "startedUtc": started, "finishedUtc": started, "elapsedMs": 0} for phase in phases]
        (self.root / name).write_text("".join(json.dumps(row) + "\n" for row in records))

    def test_both_merged_phases_must_belong_to_one_current_journey_instance(self):
        path = self.write_trx(self.planned_rows())
        self.write_timings(["e2e.first", "e2e.second"])
        report = journeys.verify(self.manifest, path, timings_directory=self.root)
        self.assertEqual("passed", report["status"])
        self.assertEqual("checked", report["phaseEvidence"])

    def test_missing_phase_cannot_be_filled_by_another_instance_or_stale_run(self):
        path = self.write_trx(self.planned_rows())
        self.write_timings(["e2e.first"])
        self.write_timings(["e2e.second"], instance="other", name="timing-2.jsonl")
        self.write_timings(["e2e.first", "e2e.second"], instance="stale",
                           started="2026-11-08T12:00:10+00:00", name="timing-3.jsonl")
        report = journeys.verify(self.manifest, path, timings_directory=self.root)
        self.assertEqual("failed", report["status"])
        self.assertEqual(2, len(report["missingPhases"]))

    def test_committed_inventory_retains_160_sources_and_two_named_merges(self):
        data = json.loads(journeys.DEFAULT_MANIFEST.read_text())
        baseline, planned = journeys.validate_manifest(data)
        self.assertEqual((160, 158), (sum(baseline.values()), sum(planned.values())))
        self.assertEqual(2, len(data["merges"]))
        self.assertEqual(4, sum(bool(entry.get("merge")) for entry in data["cases"]))


if __name__ == "__main__":
    unittest.main()
