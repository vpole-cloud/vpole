import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("cleanupcode", Path(__file__).parents[1] / "cleanupcode.py")
cleanup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cleanup)


class CleanupCodeTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Cleanup test")
        self.git("config", "user.email", "cleanup@example.invalid")
        (self.root / ".gitignore").write_text(".artifacts/\n")
        (self.root / ".editorconfig").write_text("root = true\n[*]\nindent_size = 4\n")
        (self.root / ".config").mkdir()
        (self.root / ".config/dotnet-tools.json").write_text(json.dumps({"tools": {
            cleanup.TOOL: {"version": "2026.3.0-eap08", "commands": ["jb"]}}}))
        (self.root / cleanup.SETTINGS).write_text('''<ResourceDictionary
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <String x:Key="/Default/CodeStyle/CodeCleanup/Profiles/=Safe/@EntryIndexedValue">&lt;Profile name="Pole Safe Cleanup"&gt;&lt;CSReformatCode&gt;True&lt;/CSReformatCode&gt;&lt;/Profile&gt;</String>
            </ResourceDictionary>''')
        self.path = self.root / "file with spaces.cs"
        self.path.write_text("class A{ }\n")
        self.git("add", ".")
        self.git("commit", "-qm", "baseline")

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True)

    def runner(self, command, **kwargs):
        self.assertEqual(["dotnet", "tool", "run", "jb", "--", "cleanupcode"], command[:6])
        self.assertNotIn("Pole.slnx", command)
        self.assertIn("--profile=Pole Safe Cleanup", command)
        self.assertIn("--no-build", command)
        self.assertIn("--disable-settings-layers=GlobalAll;GlobalPerProduct;SolutionShared;SolutionPersonal;ProjectShared;ProjectPersonal", command)
        copied = Path(next(arg.split("=", 1)[1] for arg in command if arg.startswith("--settings="))).parent
        self.assertNotEqual(self.root, copied)
        end = next(index for index, arg in enumerate(command) if arg.startswith("--settings="))
        for name in command[6:end]:
            source = Path(name)
            source.write_text(source.read_text().replace("class A{ }", "class A { }"))
        return SimpleNamespace(returncode=0)

    def test_check_detects_formatting_without_modifying_source_or_index(self):
        index = self.git("write-tree")
        self.assertEqual(1, cleanup.run(self.root, [self.path.name], execute=self.runner))
        self.assertEqual("class A{ }\n", self.path.read_text())
        self.assertEqual(index, self.git("write-tree"))
        report = json.loads(next((self.root / ".artifacts").rglob("report.json")).read_text())
        self.assertEqual("needs-formatting", report["status"])
        self.assertEqual([self.path.name], report["changed"])

    def test_write_formats_without_staging_and_second_check_is_clean(self):
        index = self.git("write-tree")
        self.assertEqual(0, cleanup.run(self.root, [self.path.name], True, execute=self.runner))
        self.assertEqual("class A { }\n", self.path.read_text())
        self.assertEqual(index, self.git("write-tree"))
        self.assertEqual(0, cleanup.run(self.root, [self.path.name], execute=self.runner))

    def test_tool_failure_never_applies_partial_output(self):
        def fail(command, **kwargs):
            self.runner(command, **kwargs)
            return SimpleNamespace(returncode=17)

        self.assertEqual(17, cleanup.run(self.root, [self.path.name], True, execute=fail))
        self.assertEqual("class A{ }\n", self.path.read_text())

    def test_partially_staged_source_is_refused_before_invoking_tool(self):
        self.path.write_text("class B{ }\n")
        self.git("add", self.path.name)
        self.path.write_text("class C{ }\n")
        index = self.git("write-tree")
        with self.assertRaisesRegex(ValueError, "partially staged"):
            cleanup.run(self.root, [self.path.name], True, execute=lambda *a, **k: self.fail("tool ran"))
        self.assertEqual(index, self.git("write-tree"))

    def test_concurrent_edit_is_preserved_without_overwriting_other_sources(self):
        other = self.root / "other.cs"
        other.write_text("class A{ }\n")

        def edit(command, **kwargs):
            self.runner(command, **kwargs)
            self.path.write_text("class UserEdit { }\n")
            return SimpleNamespace(returncode=0)

        with self.assertRaisesRegex(ValueError, "changed during"):
            cleanup.run(self.root, [self.path.name, other.name], True, execute=edit)
        self.assertEqual("class UserEdit { }\n", self.path.read_text())
        self.assertEqual("class A{ }\n", other.read_text())

    def test_editorconfig_hierarchy_is_copied_and_changes_in_rules_invalidate_run(self):
        nested = self.root / "feature"
        nested.mkdir()
        rules = nested / ".editorconfig"
        rules.write_text("[*.cs]\nindent_size = 2\n")
        source = nested / "new.cs"
        source.write_text("class A{ }\n")

        def edit(command, **kwargs):
            copied = Path(next(arg.split("=", 1)[1] for arg in command if arg.startswith("--settings="))).parent
            self.assertEqual(rules.read_bytes(), (copied / "feature/.editorconfig").read_bytes())
            rules.write_text("[*.cs]\nindent_size = 8\n")
            return self.runner(command, **kwargs)

        with self.assertRaisesRegex(ValueError, "changed during"):
            cleanup.run(self.root, ["feature/new.cs"], True, execute=edit)
        self.assertEqual("class A{ }\n", source.read_text())

    def test_profile_rejects_semantic_tasks_before_tool_can_run(self):
        settings = self.root / cleanup.SETTINGS
        settings.write_text(settings.read_text().replace(
            "&lt;/Profile&gt;", "&lt;RemoveCodeRedundancies&gt;True&lt;/RemoveCodeRedundancies&gt;&lt;/Profile&gt;"))
        with self.assertRaisesRegex(ValueError, "only CSReformatCode"):
            cleanup.run(self.root, [self.path.name], execute=lambda *a, **k: self.fail("tool ran"))

    def test_missing_editorconfig_root_marker_cannot_inherit_machine_rules(self):
        (self.root / ".editorconfig").write_text("[*.cs]\nindent_size = 4\n")
        with self.assertRaisesRegex(ValueError, "root = true"):
            cleanup.run(self.root, [self.path.name], execute=lambda *a, **k: self.fail("tool ran"))

    def test_tool_manifest_change_during_run_cannot_apply_mixed_versions(self):
        def change_version(command, **kwargs):
            self.runner(command, **kwargs)
            manifest = self.root / ".config/dotnet-tools.json"
            manifest.write_text(manifest.read_text().replace("2026.3.0-eap08", "2026.3.0-eap09"))
            return SimpleNamespace(returncode=0)

        with self.assertRaisesRegex(ValueError, "changed during"):
            cleanup.run(self.root, [self.path.name], True, execute=change_version)
        self.assertEqual("class A{ }\n", self.path.read_text())

    def test_symlinks_and_non_csharp_files_are_not_formatted(self):
        link = self.root / "linked.cs"
        link.symlink_to(self.path)
        for path in [link.name, ".editorconfig"]:
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, "regular C#"):
                cleanup.run(self.root, [path], execute=self.runner)

    def test_patch_for_missing_final_newline_is_applyable(self):
        self.path.write_text("class A{ }")

        def newline(command, **kwargs):
            copied = Path(next(arg.split("=", 1)[1] for arg in command if arg.startswith("--settings="))).parent
            (copied / self.path.name).write_text("class A { }\n")
            return SimpleNamespace(returncode=0)

        self.assertEqual(1, cleanup.run(self.root, [self.path.name], execute=newline))
        patch = next((self.root / ".artifacts").rglob("format.patch"))
        subprocess.run(["git", "apply", "--check", str(patch)], cwd=self.root, check=True)

    def test_later_batch_failure_does_not_apply_earlier_formatted_files(self):
        second = self.root / "second.cs"
        second.write_text("class A{ }\n")
        calls = []

        def fail_second(command, **kwargs):
            calls.append(command)
            self.runner(command, **kwargs)
            return SimpleNamespace(returncode=0 if len(calls) == 1 else 19)

        with patch.object(cleanup, "BATCH_CHAR_LIMIT", 1):
            self.assertEqual(19, cleanup.run(self.root, [self.path.name, second.name], True, execute=fail_second))
        self.assertEqual(2, len(calls))
        self.assertEqual("class A{ }\n", self.path.read_text())
        self.assertEqual("class A{ }\n", second.read_text())


if __name__ == "__main__":
    unittest.main()
