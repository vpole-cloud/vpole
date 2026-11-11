import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import verification_scope as scope
import quality_gate as gate


class ScopeTests(unittest.TestCase):
    def item(self, text, mode="100644"):
        return mode, text.encode()

    def classify(self, before, after, name="docs/rules.md"):
        return scope.classify_trees({name: self.item(before)}, {name: self.item(after)}, proof=lambda pairs: [False] * len(pairs))

    def test_docs_paths_are_narrow_and_do_not_hide_executable_assets_or_test_fixtures(self):
        for name in ["docs/rules.md", "AGENTS.md", ".codex/skills/example/SKILL.md",
                     "src/features/Example/README.md", "templates/feature/README.md.tpl",
                     "src/api/frontend/README.md", "src/shared/Observation/README.md",
                     "infrastructure/kubernetes-lab/README.md", "src/features/Functions/sdk/README.md"]:
            with self.subTest(name=name):
                self.assertFalse(self.classify("before", "after", name)["behavioral"])
        for name in ["tests/fixtures/input.md", "docs/run.py", "src/api/frontend/src/example.md",
                     "src/features/Example/Example.Tests/fixtures/sample.md", "docs/fixtures/README.md",
                     "src/features/Example/Example.Tests/README.md", "src/api/frontend/snapshots/README.md"]:
            with self.subTest(name=name):
                self.assertTrue(self.classify("before", "after", name)["behavioral"])

    def test_executable_markdown_is_never_documentation_even_when_added_or_deleted(self):
        script = self.item("#!/bin/sh\nexit 0\n", "100755")
        for before, after in [({}, {"docs/tool.md": script}), ({"docs/tool.md": script}, {}),
                              ({"docs/tool.md": script}, {"docs/tool.md": self.item("#!/bin/sh\nexit 1\n", "100755")})]:
            self.assertTrue(scope.classify_trees(before, after, proof=lambda _: [])["behavioral"])

    def test_mixed_change_keeps_all_obligations_even_when_the_latest_edit_is_documentation(self):
        old = {"src/main.fs": self.item("let x = 1"), "docs/note.md": self.item("old")}
        new = {"src/main.fs": self.item("let x = 2"), "docs/note.md": self.item("new")}
        result = scope.classify_trees(old, new, proof=lambda _: [])
        self.assertTrue(result["behavioral"])
        self.assertTrue(result["template"])

    def test_modes_symlinks_renames_and_unsupported_whitespace_require_behavioral_checks(self):
        before = {"docs/note.md": self.item("same")}
        for after in [{"docs/note.md": self.item("same", "100755")},
                      {"docs/note.md": self.item("target", "120000")},
                      {"src/app.fs": self.item("let a=1")},
                      {"src/app.yaml": self.item("a:\n  b: 1")}]:
            self.assertTrue(scope.classify_trees(before, after, proof=lambda _: [])["behavioral"])

    def test_xml_formatting_preserves_property_values_conditions_comments_and_imports(self):
        original = '<Project><PropertyGroup><Name> App </Name></PropertyGroup></Project>'
        spaced = '<Project>\n    <PropertyGroup>\n        <Name> App </Name>\n    </PropertyGroup>\n</Project>'
        self.assertFalse(self.classify(original, spaced, "Feature.csproj")["behavioral"])
        for changed in [spaced.replace(" App ", "App"), spaced.replace("<Project>", '<Project Condition="true">'),
                        spaced.replace("</Project>", '<Import Project="other.props"/></Project>')]:
            self.assertTrue(self.classify(original, changed, "Feature.csproj")["behavioral"])
        self.assertTrue(self.classify('<Project><!-- keep --></Project>', '<Project/>', "x.csproj")["behavioral"])

    def test_xml_dtd_cdata_and_space_sensitive_documents_are_not_formatting_proofs(self):
        for text in ['<!DOCTYPE Project><Project/>', '<Project><![CDATA[ ]]></Project>',
                     '<Project xml:space="preserve"> </Project>']:
            self.assertTrue(self.classify(text, text + '\n', "a.props")["behavioral"])

    def test_utf16_cannot_bypass_xml_space_rejection(self):
        for encoding in ("utf-16", "utf-16-le", "utf-16-be"):
            before = '<Project xml:space="preserve"><Group><A/></Group></Project>'.encode(encoding)
            after = '<Project xml:space="preserve">\n <Group><A/></Group>\n</Project>'.encode(encoding)
            result = scope.classify_trees({"a.props": ("100644", before)}, {"a.props": ("100644", after)},
                                          proof=lambda _: [])
            self.assertTrue(result["behavioral"])
        self.assertEqual([False], scope.csharp_equivalent([("class A{}".encode("utf-16"),
                                                          "class A { }".encode("utf-16"))]))

    def test_json_literals_and_key_order_are_preserved(self):
        self.assertFalse(self.classify('{"a":1}', '{\n  "a": 1\n}', "settings.json")["behavioral"])
        for after in ['{"a":2}', '{"a":" "}', '{"b":2,"a":1}']:
            self.assertTrue(self.classify('{"a":1,"b":2}', after, "settings.json")["behavioral"])

    def test_template_behavior_and_formatting_select_generated_checks_not_unrelated_suites(self):
        result = self.classify("old", "new", "templates/feature/Domain/Resource.cs.tpl")
        self.assertTrue(result["template"])
        self.assertFalse(result["behavioral"])
        old = {"templates/feature/Domain/Resource.cs.tpl": self.item("class A{}")}
        new = {"templates/feature/Domain/Resource.cs.tpl": self.item("class A { }")}
        result = scope.classify_trees(old, new, proof=lambda _: [True])
        self.assertTrue(result["template_format"])
        self.assertFalse(result["template"])
        self.assertTrue(self.classify("old", "new", "scripts/create_feature.py")["automation"])

    def test_failed_or_short_csharp_proof_never_silently_accepts_formatting(self):
        old, new = {"x.cs": self.item("class A{}")}, {"x.cs": self.item("class B{}")}
        self.assertTrue(scope.classify_trees(old, new, proof=lambda _: [False])["behavioral"])
        with self.assertRaises(ValueError):
            scope.classify_trees(old, new, proof=lambda _: [])

    def test_plan_compares_merge_base_through_working_tree_and_untracked_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def git(*args):
                return subprocess.check_output(["git", *args], cwd=root, text=True).strip()
            git("init", "-q")
            git("config", "user.name", "Scope test")
            git("config", "user.email", "scope@example.invalid")
            (root / "README.md").write_text("base")
            git("add", ".")
            git("commit", "-qm", "base")
            base = git("rev-parse", "HEAD")
            (root / "app.fs").write_text("let value = 2")
            git("add", ".")
            git("commit", "-qm", "behavior")
            (root / "README.md").write_text("latest docs")
            (root / "untracked.py").write_text("print('untracked')")
            result = scope.plan(base, root)
            self.assertTrue(result["behavioral"])
            self.assertEqual({"README.md", "app.fs", "untracked.py"}, {item["path"] for item in result["files"]})
            self.assertEqual(scope.fingerprint(scope.committed_tree(root, "HEAD")),
                             scope.fingerprint(scope.committed_tree(root, git("rev-parse", "HEAD"))))

    def test_selected_ci_jobs_cannot_be_skipped_failed_cancelled_or_missing(self):
        selection = {"result": "success", "outputs": {"behavioral": "true", "template": "true", "template_format": "false", "automation": "true"}}
        valid = {"selection": selection, **{name: {"result": "success"} for name in ("backend", "frontend", "feature-template")}}
        scope.validate_ci_needs(valid)
        for status in ("skipped", "failure", "cancelled"):
            with self.subTest(status=status), self.assertRaises(ValueError):
                scope.validate_ci_needs({**valid, "backend": {"result": status}})
        with self.assertRaises(KeyError):
            scope.validate_ci_needs({"selection": selection})
        selection["outputs"] = {"behavioral": "false", "template": "false", "template_format": "false", "automation": "false"}
        result = scope.validate_ci_needs({"selection": selection, **{name: {"result": "skipped"} for name in valid if name != "selection"}})
        self.assertFalse(any(result.values()))

    def test_reuse_requires_success_committed_identity_same_base_and_sufficient_evidence(self):
        tree = {"docs/a.md": self.item("old")}
        previous = {"status": "passed", "committed": True, "base_sha": "base", "tested_head": "tested",
                    "fingerprint": scope.fingerprint(tree), "validated": {"behavioral": True, "template": True}}
        selection = {"base_sha": "base", "behavioral": True, "template": True}
        with patch.object(gate, "ROOT", Path('/unused')), patch.object(scope, "committed_tree", return_value=tree), \
                patch.object(scope, "working_tree", return_value={"docs/a.md": self.item("new")}):
            self.assertIsNotNone(gate.reusable_receipt(previous, selection))
            for changes in [{"status": "failed"}, {"committed": False}, {"base_sha": "different"},
                            {"fingerprint": "unrelated"}, {"validated": {}}]:
                self.assertIsNone(gate.reusable_receipt({**previous, **changes}, selection))
        with patch.object(scope, "committed_tree", return_value=tree), \
                patch.object(scope, "working_tree", return_value={"app.fs": self.item("let x=2")}):
            self.assertIsNone(gate.reusable_receipt(previous, selection))

    def test_fingerprint_includes_executable_mode(self):
        self.assertNotEqual(scope.fingerprint({"file": self.item("same")}),
                            scope.fingerprint({"file": self.item("same", "100755")}))


class RealCSharpProofTests(unittest.TestCase):
    def test_parser_preserves_literals_imports_directives_inactive_text_and_syntax(self):
        samples = [
            ("class A{}", "class A { }", True),
            ('class A { string X="a b"; }', 'class A { string X="ab"; }', False),
            ('class A { string X="""a b"""; }', 'class A { string X="""ab"""; }', False),
            ("using A; class X{}", "class X{}", False),
            ("#if DEBUG\nclass A{}\n#endif", "#if DEBUG\nclass B{}\n#endif", False),
            ("#nullable enable\nclass X{}", "#nullable disable\nclass X{}", False),
            ("// keep\nclass A{}", "// changed\nclass A{}", False),
            ("class A { int X=1; }", "class A { int Y=1; }", False),
            ("class A{}", "class A {", False),
            ("public union R(A,B);", "public union R( A, B );", True),
        ]
        self.assertEqual([expected for _, _, expected in samples],
                         scope.csharp_equivalent([(a.encode(), b.encode()) for a, b, _ in samples]))


class ReducedReceiptTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Evidence test")
        self.git("config", "user.email", "evidence@example.invalid")
        (self.root / ".gitignore").write_text(".artifacts/\n")
        (self.root / "README.md").write_text("base")
        self.git("add", ".")
        self.git("commit", "-qm", "base")
        self.base = self.git("rev-parse", "HEAD")

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True).strip()

    def context(self):
        from contextlib import ExitStack
        stack = ExitStack()
        for name, value in {"ROOT": self.root, "ARTIFACTS": self.root / ".artifacts",
                            "RECEIPT": self.root / ".artifacts/receipt.json"}.items():
            stack.enter_context(patch.object(gate, name, value))
        stack.enter_context(patch.object(gate, "source_fingerprint",
                                        side_effect=lambda: scope.fingerprint(scope.working_tree(self.root))))
        return stack

    def test_docs_receipt_verifies_without_build_backend_frontend_or_template_suites(self):
        (self.root / "README.md").write_text("docs only")
        with self.context(), patch.object(gate, "execute") as execute, patch.object(gate, "run_full") as full:
            gate.run_selected(self.base)
            receipt = gate.require_current_receipt()
            self.assertEqual("reduced", receipt["mode"])
            self.assertEqual({}, receipt["tests"])
        full.assert_not_called()
        self.assertEqual({"documents", "frontend-install", "format", "diff-check"},
                         {call.args[0] for call in execute.call_args_list})

    def test_template_semantics_require_real_generated_reports_not_a_category_label(self):
        path = self.root / "templates/feature/file.tpl"
        path.parent.mkdir(parents=True)
        path.write_text("code")
        with self.context(), patch.object(gate, "execute"), patch.object(gate, "run_full") as full:
            with self.assertRaises(FileNotFoundError):
                gate.run_selected(self.base)
            self.assertEqual("failed", json.loads(gate.RECEIPT.read_text())["status"])
        full.assert_not_called()

    def test_behavioral_delta_cannot_verify_a_docs_receipt(self):
        (self.root / "README.md").write_text("docs only")
        with self.context(), patch.object(gate, "execute"):
            gate.run_selected(self.base)
            (self.root / "app.fs").write_text("let x = 2")
            with self.assertRaises(gate.GateError):
                gate.require_current_receipt()

    def test_semantic_source_after_docs_selects_all_suites_and_full_forces_fresh_execution(self):
        (self.root / "app.fs").write_text("let x = 2")
        with self.context(), patch.object(gate, "run_full") as full:
            gate.run_selected(self.base)
            full.assert_called_once_with(self.base, "pr", None)
        (self.root / "app.fs").unlink()
        with self.context(), patch.object(gate, "run_full") as full:
            gate.run_selected(self.base, "full")
            full.assert_called_once_with(self.base, "full", None)

    def test_committed_docs_delta_reuses_tested_identity_but_gets_new_checks_and_review(self):
        (self.root / "app.fs").write_text("let x = 1")
        self.git("add", ".")
        self.git("commit", "-qm", "behavioral change")
        tested = self.git("rev-parse", "HEAD")
        previous = {"status": "passed", "committed": True, "base_sha": self.base, "base": self.base,
                    "tested_head": tested, "fingerprint": scope.fingerprint(scope.committed_tree(self.root, tested)),
                    "validated": {"behavioral": True, "template": True}, "profile": "pr", "projects": [],
                    "tests": {"behavior": {"passed": 1}}, "review": {"agent": "old reviewer"}}
        (self.root / "README.md").write_text("follow-up documentation")
        self.git("add", ".")
        self.git("commit", "-qm", "docs follow-up")
        def execute(name, command, *args):
            if name == "feature-template":
                self.assertIn("--format-only", command)
                output = Path(command[command.index("--results-directory") + 1])
                output.mkdir(parents=True)
                (output / "result.json").write_text(json.dumps({"status": "passed", "tests": {},
                    "fingerprint": scope.fingerprint(scope.working_tree(self.root))}))
        with self.context(), patch.object(gate, "execute", side_effect=execute), \
                patch.object(gate, "run_full") as full, patch.object(gate, "selected_projects", return_value=[]):
            gate.RECEIPT.parent.mkdir(parents=True)
            gate.RECEIPT.write_text(json.dumps(previous))
            gate.run_selected(self.base)
            current = gate.require_current_receipt()
        full.assert_not_called()
        self.assertEqual(tested, current["reused"]["from_head"])
        self.assertEqual(self.git("rev-parse", "HEAD"), current["reused"]["to_head"])
        self.assertEqual(previous["tests"], current["tests"])
        self.assertNotIn("review", current)


if __name__ == "__main__":
    unittest.main()
