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


if __name__ == "__main__":
    unittest.main()
