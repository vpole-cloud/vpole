import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

SPEC = importlib.util.spec_from_file_location("timings", Path(__file__).parents[1] / "summarize_test_timings.py")
timings = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(timings)


class TimingSummaryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.results = self.root / "results"
        self.results.mkdir()
        self.phases = self.root / "phases"
        self.phases.mkdir()

    @staticmethod
    def utc(second):
        return f"2026-11-09T00:00:{second:06.3f}+00:00"

    def trx(self, rows):
        root = ET.Element("TestRun")
        definitions, results = ET.SubElement(root, "TestDefinitions"), ET.SubElement(root, "Results")
        for index, row in enumerate(rows):
            owner, method, start, end, duration = row
            item = ET.SubElement(definitions, "UnitTest", id=f"test{index}", name="theory(secret-argument-canary)")
            ET.SubElement(item, "TestMethod", className=owner, name=method, codeBase="/private/path/Pole.Tests.Sample.dll")
            test = ET.SubElement(results, "UnitTestResult", testId=f"test{index}", executionId=f"execution{index}",
                                testName="theory(secret-argument-canary)", startTime=self.utc(start), endTime=self.utc(end),
                                duration=f"0:0:{duration}", outcome="Passed")
            ET.SubElement(test, "Output").text = "Bearer secret-output-canary"
        ET.SubElement(ET.SubElement(root, "ResultSummary"), "Counters", total=str(len(rows)))
        ET.ElementTree(root).write(self.results / "results.trx")

    def phase(self, owner, start, end, phase="database.reset", instance="one", kind="test", elapsed=None):
        record = {"owner": owner, "instance": instance, "kind": kind, "phase": phase,
                  "startedUtc": self.utc(start), "finishedUtc": self.utc(end),
                  "elapsedMs": (end-start)*1000 if elapsed is None else elapsed,
                  "mongo": [{"command": "dropDatabase", "count": 1, "failed": 0, "elapsedMs": 2}],
                  "body": "secret-payload-canary"}
        with (self.phases / "timing-123.jsonl").open("a") as out:
            out.write(json.dumps(record) + "\n")

    def summary(self):
        return timings.summarize(self.results, self.phases)

    def test_parallel_classes_and_sequential_theory_cases_map_without_display_arguments(self):
        self.trx([("Pole.Tests.A", "Theory", 0, 10, 1),
                  ("Pole.Tests.A", "Theory", 10, 20, 1),
                  ("Pole.Tests.B", "Other", 0, 20, 1)])
        self.phase("Pole.Tests.A", 1, 2, instance="a")
        self.phase("Pole.Tests.A", 11, 12, instance="b")
        self.phase("Pole.Tests.B", 1, 2, instance="c")
        report = self.summary()
        backend = report["backend"]
        self.assertEqual((3, 3, 0, 0), (backend["cases"], backend["profiled_cases"], backend["unmatched_phase_records"], backend["ambiguous_phase_records"]))
        a = sorted((case for case in backend["case_records"] if case["owner"] == "Pole.Tests.A"), key=lambda case: case["started"])
        self.assertNotEqual(a[0]["execution_id"], a[1]["execution_id"])
        self.assertNotIn("canary", json.dumps(report) + timings.markdown(report))

    def test_ambiguous_and_outside_records_stay_unassigned_and_fixture_is_separate(self):
        self.trx([("Pole.Tests.A", "One", 0, 10, 1), ("Pole.Tests.A", "Two", 5, 15, 1)])
        self.phase("Pole.Tests.A", 6, 7, instance="ambiguous")
        self.phase("Pole.Tests.A", 17, 18, instance="outside")
        self.phase("Pole.Testing.MongoReplicaSetFixture", 0, 4, phase="fixture.mongo.container.start", kind="fixture")
        report = self.summary()
        self.assertEqual(1, report["backend"]["ambiguous_phase_records"])
        self.assertEqual(1, report["backend"]["unmatched_phase_records"])
        self.assertEqual(0, report["backend"]["profiled_cases"])
        self.assertEqual(4000, report["fixtures"]["phases"][0]["total_ms"])

    def test_instance_anchor_recovers_boundary_skew_with_explicit_delta(self):
        self.trx([("Pole.Tests.A", "One", .015, 10, 1)])
        self.phase("Pole.Tests.A", 0, 2, phase="database.reset", instance="same-case")
        self.phase("Pole.Tests.A", 3, 4, phase="seed.base", instance="same-case")
        report = self.summary()["backend"]
        self.assertEqual(2, report["matched_phase_records"])
        self.assertEqual(0, report["unmatched_phase_records"])
        recovered = next(record for record in report["case_records"][0]["phases"] if record["phase"] == "database.reset")
        self.assertEqual("instance_anchor", recovered["match_method"])
        self.assertAlmostEqual(15, recovered["boundary_delta_ms"]["before_start"], places=2)
        self.assertEqual({"instance_anchor": 1, "utc_enclosure": 1}, report["match_methods"])

    def test_near_boundary_without_an_instance_anchor_is_not_nearest_matched(self):
        self.trx([("Pole.Tests.A", "One", .015, 10, 1)])
        self.phase("Pole.Tests.A", 0, 2, instance="unanchored")
        report = self.summary()["backend"]
        self.assertEqual(0, report["matched_phase_records"])
        self.assertEqual(1, report["unmatched_phase_records"])
        self.assertEqual("no_instance_anchor", report["unmatched"][0]["unmatched_reason"])

    def test_same_instance_conflicting_anchors_never_force_assignment(self):
        self.trx([("Pole.Tests.A", "One", 0, 10, 1), ("Pole.Tests.A", "Two", 10, 20, 1)])
        self.phase("Pole.Tests.A", 1, 2, instance="conflict")
        self.phase("Pole.Tests.A", 11, 12, phase="seed.base", instance="conflict")
        report = self.summary()
        self.assertEqual(0, report["backend"]["matched_phase_records"])
        self.assertEqual(2, report["backend"]["ambiguous_phase_records"])
        self.assertEqual(1, report["diagnostics"]["conflicting_instance_anchors"])

    def test_instance_anchor_rejects_large_boundary_anomaly(self):
        self.trx([("Pole.Tests.A", "One", 1, 10, 1)])
        self.phase("Pole.Tests.A", 0, 2, instance="same-case")
        self.phase("Pole.Tests.A", 3, 4, phase="seed.base", instance="same-case")
        report = self.summary()
        self.assertEqual(1, report["backend"]["matched_phase_records"])
        self.assertEqual(1, report["backend"]["unmatched_phase_records"])
        self.assertEqual("instance_boundary_limit_exceeded", report["backend"]["unmatched"][0]["unmatched_reason"])
        self.assertEqual(1, report["diagnostics"]["instance_boundary_limit_exceeded"])

    def test_parallel_intervals_and_nested_records_are_not_claimed_as_wall_time(self):
        self.trx([("Pole.Tests.A", "One", 0, 10, 3), ("Pole.Tests.B", "Two", 0, 10, 3)])
        self.phase("Pole.Tests.A", 1, 5, phase="api.host.start")
        self.phase("Pole.Tests.A", 2, 3, phase="api.services.build")
        (self.results / "receipt.json").write_text(json.dumps({"seconds": 10, "projects": []}))
        backend = self.summary()["backend"]
        self.assertEqual(20000, backend["intervals"]["total_ms"])
        self.assertEqual(10, backend["inventory"]["wall_seconds"])
        case = next(case for case in backend["case_records"] if case["owner"] == "Pole.Tests.A")
        self.assertAlmostEqual(4000, case["phase_interval_union_ms"], places=2)
        self.assertAlmostEqual(1000, case["overlapping_phase_interval_ms"], places=2)
        self.assertAlmostEqual(2000, case["unattributed_ms"], places=2)

    def test_nested_e2e_and_cleanup_phases_survive_and_unknown_resource_names_are_rejected(self):
        owner = "Pole.Tests.E2E.Example"
        self.trx([(owner, "Scenario", 0, 10, 1)])
        self.phase(owner, 0, 10, phase="e2e.case", instance="same")
        self.phase(owner, 1, 8, phase="test.body", instance="same")
        self.phase(owner, 6, 7, phase="e2e.source-build.infrastructure.ready", instance="same")
        self.phase(owner, 7, 8, phase="kubernetes.namespace.wait-deleted", instance="same")
        self.phase(owner, 8, 10, phase="cleanup.total", instance="same")
        self.phase(owner, 8, 9, phase="cleanup.redis.relay", instance="same")
        self.phase(owner, 9, 10, phase="cleanup.namespace.wait-deleted", instance="same")
        self.phase(owner, 8, 9, phase="cleanup.private-resource-canary", instance="same")

        report = self.summary()

        self.assertEqual(7, report["backend"]["matched_phase_records"])
        self.assertEqual(0, report["backend"]["unmatched_phase_records"])
        self.assertEqual(1, report["diagnostics"]["invalid_timing_records"])
        self.assertEqual(10000, report["backend"]["case_records"][0]["phase_interval_union_ms"])
        self.assertNotIn("canary", json.dumps(report) + timings.markdown(report))

    def test_missing_profiles_remain_visible_and_bad_records_are_counted_without_payload(self):
        self.trx([("Pole.Tests.A", "One", 0, 10, 3)])
        self.phase("Pole.Tests.A", 1, 2, phase="payload-secret-canary")
        with (self.phases / "timing-123.jsonl").open("a") as out:
            out.write("private-secret-canary\n")
        report = self.summary()
        self.assertEqual(1, report["backend"]["unprofiled_cases"])
        self.assertEqual(2, report["diagnostics"]["invalid_timing_records"])
        self.assertFalse(report["backend"]["inventory"]["manifest_available"])
        self.assertNotIn("canary", json.dumps(report))

    def test_command_aggregates_percentiles_and_overaccounting_are_explicit(self):
        self.trx([("Pole.Tests.A", "One", 0, 10, 9)])
        self.phase("Pole.Tests.A", 1, 2, elapsed=2000)
        self.phase("Pole.Tests.A", 3, 4, phase="api.migration.ExampleMigration", instance="two")
        backend = self.summary()["backend"]
        self.assertEqual(2, sum(item["count"] for item in backend["mongo"]))
        case = backend["case_records"][0]
        self.assertEqual(0, case["unattributed_ms"])
        self.assertEqual(2000, case["overaccounted_ms"])
        self.assertEqual({"count": 3, "total_ms": 14, "median_ms": 3, "p95_ms": 9, "max_ms": 9}, timings.stats([2, 3, 9]))

    def test_frontend_durations_and_inventory_gap_without_raw_names_or_failures(self):
        self.trx([("Pole.Tests.A", "One", 0, 10, 1)])
        (self.results / "frontend.json").write_text(json.dumps({"numTotalTests": 2, "testResults": [
            {"name": "/home/private/src/api/frontend/src/owned.test.ts", "message": "secret-error-canary",
             "assertionResults": [{"fullName": "secret-arguments-canary", "status": "passed", "duration": 8,
                                   "failureMessages": ["secret-body-canary"]}]}]}))
        report = self.summary()
        self.assertTrue(report["frontend"]["inventory_gap"])
        self.assertEqual(8, report["frontend"]["durations"]["total_ms"])
        self.assertEqual("src/owned.test.ts", report["frontend"]["cases"][0]["file"])
        self.assertNotIn("canary", json.dumps(report))

    def test_sibling_receipt_wall_time_requires_this_results_directory(self):
        self.trx([("Pole.Tests.A", "One", 0, 10, 1)])
        receipt = self.root / "receipt.json"
        receipt.write_text(json.dumps({"seconds": 123, "projects": [], "steps": [{"log": ".artifacts/quality/other/one.log"}]}))
        self.assertIsNone(self.summary()["backend"]["inventory"]["wall_seconds"])
        receipt.write_text(json.dumps({"seconds": 123, "projects": [], "steps": [{"log": ".artifacts/quality/results/one.log"}]}))
        self.assertEqual(123, self.summary()["backend"]["inventory"]["wall_seconds"])

    def test_declared_case_inventory_gap_is_not_hidden_by_matching_assembly(self):
        self.trx([("Pole.Tests.A", "One", 0, 10, 1)])
        path = self.results / "results.trx"
        tree = ET.parse(path)
        tree.find(".//Counters").set("total", "2")
        tree.write(path)
        report = self.summary()["backend"]
        self.assertEqual(2, report["declared_cases"])
        self.assertTrue(report["case_inventory_gap"])

    def test_cli_creates_both_reports_and_missing_manifest_inventory_is_explicit(self):
        self.trx([("Pole.Tests.A", "One", 0, 10, 1)])
        (self.results / "backend-manifest.json").write_text(json.dumps({"projects": ["tests/Pole.Tests.Missing/Pole.Tests.Missing.csproj"]}))
        output = self.root / "output"
        result = subprocess.run(["python3", str(Path(__file__).parents[1] / "summarize_test_timings.py"),
                                 "--results-directory", str(self.results), "--timings-directory", str(self.phases), "--output", str(output)],
                                capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        report = json.loads((output / "timings.json").read_text())
        self.assertEqual(["Pole.Tests.Missing"], report["backend"]["inventory"]["missing_assemblies"])
        self.assertTrue((output / "timings.md").exists())
        self.assertNotIn("canary", (output / "timings.md").read_text())


if __name__ == "__main__":
    unittest.main()
