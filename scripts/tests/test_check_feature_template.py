import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import check_feature_template as validator


class FeatureTemplateValidationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)

    def test_snapshot_uses_scoped_ids_and_the_real_shared_lab_state(self):
        state = self.root / ".tmp/kubernetes-lab/state.json"
        state.parent.mkdir(parents=True)
        state.write_text('{}')
        original = {"DOCKER_HOST": "unix:///explicit.sock"}
        first = validator.validation_environment(self.root, original)
        second = validator.validation_environment(self.root, original)
        self.assertEqual(first["POLE_TEST_WORKTREE_ID"], second["POLE_TEST_WORKTREE_ID"])
        self.assertNotEqual(first["POLE_TEST_RUN_ID"], second["POLE_TEST_RUN_ID"])
        self.assertEqual(str(state), first["POLE_K8S_LAB_STATE"])
        self.assertEqual("unix:///explicit.sock", first["DOCKER_HOST"])
        self.assertEqual({"DOCKER_HOST": "unix:///explicit.sock"}, original)

    def test_explicit_runner_identity_and_lab_overrides_are_preserved(self):
        original = {"POLE_TEST_WORKTREE_ID": "runner", "POLE_TEST_RUN_ID": "attempt-1",
                    "POLE_K8S_LAB_STATE": "/selected/state.json"}
        self.assertEqual(original, validator.validation_environment(self.root, original))

    def test_a_failed_snapshot_is_reported_as_failed_not_running_or_passed(self):
        output = self.root / "results"
        with patch.object(validator, "source_fingerprint", return_value="current"), \
                patch.object(validator, "snapshot_sources", side_effect=OSError("controlled failure")):
            with self.assertRaisesRegex(OSError, "controlled failure"):
                validator.check(output)
        self.assertEqual("failed", json.loads((output / "result.json").read_text())["status"])

    def test_validation_requires_a_fresh_results_directory(self):
        with self.assertRaises(FileExistsError):
            validator.check(self.root)

    def test_formatting_uses_the_shared_pinned_safe_cleanup_on_generated_sources(self):
        source = self.root / "src/features/Entries/Entry.cs"
        with patch.object(validator, "execute") as execute:
            validator.check_format(self.root, self.root / "results", [source])
        self.assertEqual(["dotnet", "tool", "restore"], execute.call_args_list[0].args[1])
        command = execute.call_args_list[1].args[1]
        self.assertEqual(["scripts/cleanupcode.py", "--check", "src/features/Entries/Entry.cs"], command[1:])
        self.assertEqual(self.root, execute.call_args_list[1].args[2])

    def test_coverage_reuses_the_current_gate_product_inventory(self):
        output = self.root / ".artifacts/template-coverage"
        output.mkdir(parents=True)
        evidence = {"assemblies": ["Product", "Product.Migrations"], "settings": "coverage.runsettings"}
        (output / "coverage-settings.json").write_text(json.dumps(evidence))
        with patch.object(validator, "execute") as execute:
            settings, observed = validator.prepare_test_coverage(self.root, self.root / "results")
        self.assertEqual(output / "coverage.runsettings", settings)
        self.assertEqual(evidence, observed)
        self.assertEqual(["scripts/quality_gate.py", "coverage-settings", "--results-directory", str(output)],
                         execute.call_args.args[1][1:])

    def test_semantic_architecture_prerequisites_build_the_whole_snapshot_solution(self):
        def stop_after_build(name, *args):
            if name == "build":
                raise validator.GateError("controlled build stop")

        with patch.object(validator, "source_fingerprint", return_value="current"), \
                patch.object(validator, "snapshot_sources"), patch.object(validator, "wire_snapshot"), \
                patch.object(validator, "validation_environment", return_value={
                    "POLE_TEST_WORKTREE_ID": "worktree", "POLE_TEST_RUN_ID": "run"}), \
                patch.object(validator, "execute", side_effect=stop_after_build) as execute:
            with self.assertRaisesRegex(validator.GateError, "controlled build stop"):
                validator.check(self.root / "results")
        self.assertEqual(["dotnet", "restore", "Pole.slnx"], execute.call_args_list[0].args[1][:3])
        self.assertEqual(["dotnet", "build", "Pole.slnx"], execute.call_args_list[1].args[1][:3])
