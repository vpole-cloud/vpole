# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("evidence", Path(__file__).parents[1] / "export_test_evidence.py")
evidence = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(evidence)


class FailureEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.raw = self.root / "raw"
        self.raw.mkdir()
        self.phases = self.root / "phases"
        self.phases.mkdir()
        self.output = self.root / "safe"

    def test_failed_report_preserves_only_known_category_and_repository_source_locations(self):
        source = "tests/Pole.Tests.Integration/Realtime/RealtimeRedisTests.cs"
        (self.raw / "failure.trx").write_text(f'''<TestRun><Results>
<UnitTestResult outcome="Failed"><Output><ErrorInfo>
<Message>Assert.Single() Failure: secret-message-canary</Message>
<StackTrace>at Method(cookie-canary) in /home/runner/work/Pole/Pole/{source}:line 394
at Payload() in /tmp/secret-file-canary.cs:line 2
at Header() in /home/runner/work/Pole/Pole/{source}:line 9999999
at Secret() in /home/runner/work/Pole/Pole/{source}:line -2
secret-stack-canary</StackTrace></ErrorInfo></Output></UnitTestResult>
</Results><ResultSummary outcome="Failed"><Counters total="1" failed="1" /></ResultSummary></TestRun>''')
        evidence.export_evidence(self.raw, self.phases, self.output)
        report = next(self.output.glob("*.trx")).read_text()
        self.assertIn('"category": "assert.single"', report)
        self.assertIn(f'"file": "{source}", "line": 394', report)
        self.assertNotIn("canary", report)
        self.assertNotIn("9999999", report)
        self.assertNotIn("/home/runner", report)

    def test_failure_categories_are_fixed_values_and_unknown_messages_are_never_echoed(self):
        for message, expected in (("System.TimeoutException : secret-canary", "timeout"),
                                  ("Assert.Equal() Failure: body-canary", "assert.equal"),
                                  ("private-type-canary: secret-canary", "unknown")):
            with self.subTest(expected=expected):
                self.assertEqual({"category": expected, "locations": []},
                                 evidence.safe_failure(message, "private-stack-canary"))

    def test_known_kubernetes_failure_exports_only_fixed_type_and_status(self):
        for status, expected in (("Conflict", 409), ("Forbidden", 403), ("InternalServerError", 500)):
            failure = evidence.safe_failure(
                f"k8s.Autorest.HttpOperationException : Operation returned an invalid status code '{status}'\n"
                "headers-canary secret-body-canary workload-canary", "cookie-stack-canary")
            self.assertEqual({"category": "kubernetes.http", "status": expected, "locations": []}, failure)
            self.assertNotIn("canary", json.dumps(failure))
        unknown = evidence.safe_failure(
            "k8s.Autorest.HttpOperationException : Operation returned an invalid status code 'status-canary'", "")
        self.assertEqual({"category": "kubernetes.http", "locations": []}, unknown)

    def test_controlled_failed_run_preserves_failure_and_exports_no_sensitive_output(self):
        failed = subprocess.run(["python3", "-c", "raise SystemExit(3)"])
        (self.raw / "run.trx").write_text('''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<TestDefinitions><UnitTest id="test"><TestMethod className="Pole.Tests.Controlled" name="Fails" /></UnitTest></TestDefinitions>
<Results><UnitTestResult testId="test" testName="Fails(secret-parameter-canary)" outcome="Failed"><Output><ErrorInfo><Message>Bearer credential-canary</Message><StackTrace>payload-canary</StackTrace></ErrorInfo><StdOut>cookie-canary workload-canary</StdOut></Output></UnitTestResult></Results>
<ResultSummary outcome="Failed"><Counters total="1" executed="1" passed="0" failed="1" /><Output><StdOut>response-body-canary</StdOut></Output></ResultSummary></TestRun>''')
        (self.phases / "run.jsonl").write_text(json.dumps({
            "phase": "RequestStarted", "resource": "a" * 64, "elapsedMs": 3, "request": 1,
            "headers": "header-canary", "body": "body-canary", "url": "url-token-canary",
        }) + "\n")
        result = evidence.export_evidence(self.raw, self.phases, self.output)
        self.assertEqual(3, failed.returncode)
        self.assertEqual(1, result["reports"][0]["counts"]["failed"])
        self.assertEqual(1, result["phase_files"])
        text = "".join(p.read_text() for p in self.output.iterdir())
        self.assertNotIn("canary", text)
        self.assertIn("Pole.Tests.Controlled.Fails", text)
        self.assertIn('outcome="Failed"', text)
        self.assertIn('"phase": "RequestStarted"', text)

    def test_rejects_untyped_phase_values_and_unknown_fields(self):
        value = evidence.safe_phase({"phase": "Failed", "status": "Bearer private-canary",
                                     "resource": "private-canary", "elapsedMs": True, "body": "private-canary"})
        self.assertEqual({"phase": "Failed"}, value)
        self.assertIsNone(evidence.safe_phase({"phase": "private-canary"}))

    def test_malformed_and_oversized_reports_are_counted_without_echoing_input(self):
        (self.raw / "broken.trx").write_text("private-canary")
        (self.phases / "large.jsonl").write_text("private-canary" * 100_000)
        result = evidence.export_evidence(self.raw, self.phases, self.output)
        self.assertEqual(2, result["export_errors"])
        self.assertNotIn("private-canary", (self.output / "summary.json").read_text())

    def test_theory_parameter_values_are_not_exported_as_test_names(self):
        (self.raw / "input.trx").write_text('''<TestRun><Results><UnitTestResult testName="method(payload-canary)" outcome="NotExecuted" /></Results><ResultSummary outcome="Completed"><Counters total="1" notExecuted="1" /></ResultSummary></TestRun>''')
        evidence.export_evidence(self.raw, self.phases, self.output)
        text = "".join(p.read_text() for p in self.output.iterdir())
        self.assertNotIn("payload-canary", text)
        self.assertIn('outcome="NotExecuted"', text)

    def test_headroom_contains_only_capacity_and_boolean_node_conditions(self):
        (self.raw / "headroom-before-backend-18.json").write_text(json.dumps({
            "phase": "before-backend-18", "disk_free": 123, "memory": {"MemAvailable": 456, "token": "private-canary"},
            "targets": [{"id": "a" * 64, "kubeconfig": "private-canary", "nodes": [{"Ready": True, "message": "private-canary"}]}],
        }))
        evidence.export_evidence(self.raw, self.phases, self.output)
        text = (self.output / "summary.json").read_text()
        self.assertNotIn("private-canary", text)
        self.assertEqual(123, json.loads(text)["headroom"][0]["disk_free"])

    def test_controlled_frontend_failure_exports_typed_outcomes_without_arbitrary_text(self):
        failed = subprocess.run(["python3", "-c", "raise SystemExit(3)"])
        report = self.raw / "frontend.json"
        report.write_text(json.dumps(self.frontend_report()))
        result = evidence.export_evidence(self.raw, self.phases, self.output, report)
        self.assertEqual(3, failed.returncode)
        self.assertEqual(0, result["export_errors"])
        self.assertEqual("frontend.json", result["frontend_report"])
        safe = json.loads((self.output / "frontend.json").read_text())
        self.assertFalse(safe["success"])
        self.assertEqual(1, safe["counts"]["numFailedTests"])
        self.assertEqual("failed", safe["suites"][0]["outcome"])
        self.assertEqual("failed", safe["suites"][0]["tests"][0]["outcome"])
        self.assertEqual(42, safe["suites"][0]["duration_ms"])
        self.assertEqual(12.5, safe["suites"][0]["tests"][0]["duration_ms"])
        self.assertRegex(safe["suites"][0]["id"], r"^[a-f0-9]{64}$")
        self.assertRegex(safe["suites"][0]["tests"][0]["id"], r"^[a-f0-9]{64}$")
        text = "".join(path.read_text() for path in self.output.iterdir())
        self.assertNotIn("canary", text)
        self.assertNotIn("failureMessages", text)
        self.assertNotIn("fullName", text)

    def test_frontend_cli_preserves_safe_failure_evidence_after_the_test_process_failed(self):
        report = self.raw / "frontend.json"
        report.write_text(json.dumps(self.frontend_report()))
        exported = subprocess.run([
            "python3", str(Path(__file__).parents[1] / "export_test_evidence.py"),
            "--input", str(self.raw), "--phases-directory", str(self.phases), "--output", str(self.output),
            "--frontend-report", str(report),
        ], capture_output=True, text=True)
        self.assertEqual(0, exported.returncode)
        safe = json.loads((self.output / "frontend.json").read_text())
        self.assertEqual(1, safe["counts"]["numFailedTests"])
        self.assertNotIn("canary", exported.stdout + exported.stderr)

    def test_missing_invalid_and_oversized_frontend_reports_produce_safe_error_summaries(self):
        report = self.raw / "private-report-canary.json"
        for content in (None, "private-invalid-canary", "[]", "null", '{"success":true}',
                        json.dumps(self.frontend_report(numFailedTests="counter-canary")),
                        json.dumps(self.frontend_report(testResults="payload-canary")),
                        "size-canary" * (32 * 1024 * 1024 // len("size-canary") + 1)):
            with self.subTest(content_type=type(content).__name__):
                if content is None:
                    report.unlink(missing_ok=True)
                else:
                    report.write_text(content)
                result = evidence.export_evidence(self.raw, self.phases, self.output, report)
                self.assertEqual(1, result["export_errors"])
                self.assertIsNone(result["frontend_report"])
                self.assertNotIn("canary", (self.output / "summary.json").read_text())

    def test_invalid_frontend_report_does_not_leave_previous_success_metadata(self):
        report = self.raw / "frontend.json"
        report.write_text(json.dumps(self.frontend_report()))
        evidence.export_evidence(self.raw, self.phases, self.output, report)
        self.assertTrue((self.output / "frontend.json").exists())
        report.write_text("invalid-canary")
        result = evidence.export_evidence(self.raw, self.phases, self.output, report)
        self.assertEqual(1, result["export_errors"])
        self.assertFalse((self.output / "frontend.json").exists())
        self.assertNotIn("canary", (self.output / "summary.json").read_text())

    def test_frontend_missing_report_cli_fails_but_still_writes_the_bounded_summary(self):
        exported = subprocess.run([
            "python3", str(Path(__file__).parents[1] / "export_test_evidence.py"),
            "--input", str(self.raw), "--phases-directory", str(self.phases), "--output", str(self.output),
            "--frontend-report", str(self.raw / "missing-canary.json"),
        ], capture_output=True, text=True)
        self.assertEqual(1, exported.returncode)
        self.assertEqual(1, json.loads((self.output / "summary.json").read_text())["export_errors"])
        self.assertNotIn("canary", exported.stdout + exported.stderr)

    def test_frontend_durations_and_unknown_outcomes_cannot_export_arbitrary_values(self):
        for value in (True, -1, float("nan"), float("inf"), 10**400, "duration-canary"):
            self.assertIsNone(evidence.frontend_duration(value))
        self.assertEqual(12.5, evidence.frontend_duration(12.5))
        self.assertEqual("error", evidence.frontend_outcome("outcome-canary"))
        self.assertEqual("error", evidence.frontend_outcome([]))

    def test_frontend_failure_artifact_is_always_uploaded_and_raw_report_is_excluded(self):
        workflow = (Path(__file__).parents[2] / ".github/workflows/kubernetes-lab.yml").read_text()
        frontend = workflow.split("  frontend:\n", 1)[1]
        for step in ("Export safe frontend test evidence", "Upload safe frontend test evidence"):
            block = frontend.split(f"      - name: {step}\n", 1)[1].split("      - name:", 1)[0]
            self.assertIn("if: always()", block)
        coverage = frontend.split("      - name: Upload frontend coverage report\n", 1)[1].split("      - name:", 1)[0]
        self.assertIn("!src/api/frontend/coverage/tests.json", coverage)
        self.assertIn("--frontend-report src/api/frontend/coverage/tests.json", frontend)

    @staticmethod
    def frontend_report(**overrides):
        report = {
            "success": False, "numTotalTestSuites": 1, "numPassedTestSuites": 0,
            "numFailedTestSuites": 1, "numPendingTestSuites": 0,
            "numTotalTests": 1, "numPassedTests": 0, "numFailedTests": 1,
            "numPendingTests": 0, "numTodoTests": 0,
            "testResults": [{
                "name": "suite-header-canary.test.tsx", "status": "failed", "startTime": 1000, "endTime": 1042,
                "message": "Bearer secret-canary", "console": "cookie-canary",
                "assertionResults": [{
                    "title": "title-canary", "fullName": "theory(parameter-canary)",
                    "ancestorTitles": ["suite-canary"], "status": "failed", "duration": 12.5,
                    "failureMessages": ["header-canary body-canary workload-log-canary"],
                    "failureDetails": {"token": "token-canary", "cookie": "cookie-canary"},
                    "meta": {"payload": "payload-canary"},
                }],
            }],
            "snapshot": {"failure": "snapshot-body-canary"}, "testResultsProcessor": "console-canary",
        }
        report.update(overrides)
        return report


if __name__ == "__main__":
    unittest.main()
