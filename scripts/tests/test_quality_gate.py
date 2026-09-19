import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("quality_gate", Path(__file__).parents[1] / "quality_gate.py")
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def trx(self, total=1, passed=1, executed=1, outcome="Passed", summary="Completed"):
        path = self.root / "test.trx"
        path.write_text(f'''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<Results><UnitTestResult testName="Example" outcome="{outcome}" /></Results>
<ResultSummary outcome="{summary}"><Counters total="{total}" passed="{passed}" executed="{executed}" /></ResultSummary>
</TestRun>''')
        return path

    def test_accepts_completed_test_results(self):
        self.assertEqual(1, gate.read_trx(self.trx())["passed"])

    def test_rejects_skip_even_when_test_process_exits_successfully(self):
        with self.assertRaises(gate.GateError):
            gate.read_trx(self.trx(passed=0, executed=0, outcome="NotExecuted"))

    def test_rejects_zero_discovered_tests(self):
        with self.assertRaises(gate.GateError):
            gate.read_trx(self.trx(total=0, passed=0, executed=0))

    def test_rejects_aborted_run_with_successful_counters(self):
        with self.assertRaises(gate.GateError):
            gate.read_trx(self.trx(summary="Aborted"))

    def test_rejects_inconsistent_counters(self):
        with self.assertRaises(gate.GateError):
            gate.read_trx(self.trx(outcome="Failed"))

    def test_rejects_missing_test_results(self):
        with self.assertRaises(gate.GateError):
            gate.read_trx(self.trx(total=2, passed=2, executed=2))

    def test_rejects_frontend_todo_or_skips(self):
        for property_name in ("numTodoTests", "numPendingTests"):
            with self.subTest(property_name=property_name):
                path = self.root / "frontend.json"
                path.write_text(json.dumps({"success": True, "numTotalTests": 1, "numPassedTests": 1, property_name: 1}))
                with self.assertRaises(gate.GateError):
                    gate.read_vitest(path)

    def test_accepts_frontend_success(self):
        path = self.root / "frontend.json"
        path.write_text(json.dumps({"success": True, "numTotalTests": 3, "numPassedTests": 3}))
        self.assertEqual(3, gate.read_vitest(path)["passed"])

    def test_missing_project_in_solution_blocks_full_run(self):
        (self.root / "src").mkdir()
        (self.root / "tests").mkdir()
        (self.root / "tests" / "Example.csproj").write_text('<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>')
        (self.root / "Pole.slnx").write_text("<Solution />")
        with self.assertRaises(gate.GateError):
            gate.test_projects(self.root)

    def test_discovers_every_test_project_but_not_test_helpers(self):
        (self.root / "src").mkdir()
        (self.root / "tests").mkdir()
        (self.root / "tests" / "Example.csproj").write_text('<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>')
        (self.root / "tests" / "Helper.csproj").write_text("<Project />")
        (self.root / "Pole.slnx").write_text('<Solution><Project Path="tests/Example.csproj" /></Solution>')
        self.assertEqual(["tests/Example.csproj"], gate.test_projects(self.root))

    def test_backend_reports_must_cover_every_discovered_assembly(self):
        (self.root / "tests").mkdir()
        (self.root / "tests" / "Example.csproj").write_text('<Project><PropertyGroup><IsTestProject>true</IsTestProject><AssemblyName>Pole.Example.Tests</AssemblyName></PropertyGroup></Project>')
        (self.root / "Pole.slnx").write_text('<Solution><Project Path="tests/Example.csproj" /></Solution>')
        report = self.trx()
        with self.assertRaises(gate.GateError):
            gate.check_backend_reports(self.root, self.root)
        report.write_text(report.read_text().replace("<Results>", '<TestDefinitions><UnitTest><TestMethod codeBase="/bin/Pole.Example.Tests.dll" /></UnitTest></TestDefinitions><Results>'))
        gate.check_backend_reports(self.root, self.root)
        report.write_text(report.read_text().replace('outcome="Passed"', 'outcome="NotExecuted"'))
        with self.assertRaises(gate.GateError):
            gate.check_backend_reports(self.root, self.root)

    def test_fingerprint_tracks_uncommitted_untracked_and_deleted_sources(self):
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        path = self.root / "example.cs"
        path.write_text("first")
        initial = gate.source_fingerprint(self.root)
        subprocess.run(["git", "add", "example.cs"], cwd=self.root, check=True)
        self.assertEqual(initial, gate.source_fingerprint(self.root))
        path.write_text("changed")
        self.assertNotEqual(initial, gate.source_fingerprint(self.root))
        path.unlink()
        self.assertNotEqual(initial, gate.source_fingerprint(self.root))

    def test_staging_deletion_preserves_tested_fingerprint(self):
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        path = self.root / "deleted.cs"
        path.write_text("existing source")
        subprocess.run(["git", "add", "deleted.cs"], cwd=self.root, check=True)
        path.unlink()
        tested = gate.source_fingerprint(self.root)
        subprocess.run(["git", "add", "--all"], cwd=self.root, check=True)
        self.assertEqual(tested, gate.source_fingerprint(self.root))

    def test_staging_rename_preserves_tested_fingerprint(self):
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        path = self.root / "before.cs"
        path.write_text("existing source")
        subprocess.run(["git", "add", "before.cs"], cwd=self.root, check=True)
        path.rename(self.root / "after.cs")
        tested = gate.source_fingerprint(self.root)
        subprocess.run(["git", "add", "--all"], cwd=self.root, check=True)
        self.assertEqual(tested, gate.source_fingerprint(self.root))

    def publication_attempt(self, base=None):
        body = self.root / "body.md"
        body.write_text("Reviewed change")
        argv = ["quality_gate.py", "open-pr", "--title", "Example", "--body-file", str(body)]
        if base is not None:
            argv += ["--base", base]

        def git(*args):
            outputs = {
                ("status", "--porcelain"): "",
                ("branch", "--show-current"): "feature",
                ("ls-remote", "--exit-code", "origin", "refs/heads/feature"): "head-sha\trefs/heads/feature",
                ("rev-parse", "HEAD"): "head-sha",
                ("rev-parse", "--verify", "refs/remotes/origin/master"): "master-sha",
                ("rev-parse", "--verify", "refs/remotes/origin/parent"): "parent-sha",
            }
            return outputs[args]

        receipt = {"base_sha": "parent-sha", "review": {"agent": "reviewer"}}
        with patch.object(gate.sys, "argv", argv), \
                patch.object(gate, "require_current_receipt", return_value=receipt), \
                patch.object(gate, "git", side_effect=git), \
                patch.object(gate.subprocess, "run") as publish:
            result = gate.main()
        return result, publish

    def test_publication_rejects_a_base_different_from_reviewed_evidence(self):
        result, publish = self.publication_attempt()
        self.assertEqual(1, result)
        publish.assert_not_called()

    def test_publication_accepts_explicit_matching_stacked_base(self):
        result, publish = self.publication_attempt("parent")
        self.assertEqual(0, result)
        command = publish.call_args.args[0]
        self.assertEqual("parent", command[command.index("--base") + 1])

    def test_stale_or_failed_receipt_cannot_authorize_publication(self):
        path = self.root / "receipt.json"
        for data in ({"status": "failed", "failure": "E2E unavailable"}, {"status": "passed", "fingerprint": "old"}):
            path.write_text(json.dumps(data))
            with patch.object(gate, "RECEIPT", path), patch.object(gate, "source_fingerprint", return_value="new"):
                with self.assertRaises(gate.GateError):
                    gate.require_current_receipt()

    def test_failed_command_records_failure_without_success_claim(self):
        steps = []
        with patch.object(gate, "ROOT", self.root), self.assertRaises(gate.GateError):
            gate.execute("intentional-failure", ["python3", "-c", "raise SystemExit(3)"], self.root, steps)
        self.assertEqual(3, steps[0]["exit_code"])


if __name__ == "__main__":
    unittest.main()
