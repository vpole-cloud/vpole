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
