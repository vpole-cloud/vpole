import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location("format_changed", Path(__file__).parents[1] / "format_changed.py")
formatter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(formatter)


class FormatChangedTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Formatter test")
        self.git("config", "user.email", "formatter@example.invalid")
        (self.root / "file with spaces.cs").write_text("record A;\n")
        self.git("add", ".")
        self.git("commit", "-qm", "baseline")
        self.base = self.git("rev-parse", "HEAD").strip()

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True)

    def test_write_refuses_partial_staging_without_invoking_formatter(self):
        path = self.root / "file with spaces.cs"
        path.write_text("record B;\n")
        self.git("add", ".")
        path.write_text("record C;\n")
        index = self.git("write-tree")
        with self.assertRaisesRegex(ValueError, "partially staged"):
            formatter.run(self.root, self.base, True, execute=lambda *a, **k: self.fail("invoked formatter"))
        self.assertEqual(index, self.git("write-tree"))
        self.assertEqual("record C;\n", path.read_text())

    def test_discovers_untracked_files_and_preserves_spaces_and_index(self):
        (self.root / "new.cs").write_text("record New;\n")
        index = self.git("write-tree")
        calls = []
        result = formatter.run(self.root, self.base, False,
                               execute=lambda command, **kwargs: calls.append(command) or SimpleNamespace(returncode=0))
        self.assertEqual(0, result)
        self.assertIn("new.cs", calls[0])
        self.assertIn("--verify-no-changes", calls[0])
        self.assertEqual(index, self.git("write-tree"))

    def test_formatter_failure_is_reported_and_never_staged(self):
        (self.root / "file with spaces.cs").write_text("record Changed;\n")
        index = self.git("write-tree")
        result = formatter.run(self.root, self.base, True,
                               execute=lambda command, **kwargs: SimpleNamespace(returncode=17))
        self.assertEqual(17, result)
        self.assertEqual(index, self.git("write-tree"))
        self.assertEqual(["file with spaces.cs"], formatter.selected_paths(self.root, self.base))

    def test_fully_staged_file_can_be_formatted_without_restaging(self):
        path = self.root / "file with spaces.cs"
        path.write_text("record Changed;\n")
        self.git("add", ".")
        index = self.git("write-tree")

        def execute(command, **kwargs):
            path.write_text("record Changed ;\n")
            return SimpleNamespace(returncode=0)

        self.assertEqual(0, formatter.run(self.root, self.base, True, execute=execute))
        self.assertEqual(index, self.git("write-tree"))
