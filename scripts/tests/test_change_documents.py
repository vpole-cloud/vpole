# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

from pathlib import Path
import subprocess
import tempfile
import unittest

from check_change_documents import check


class DocumentChecksTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        def git(*args):
            return subprocess.check_output(["git", *args], cwd=self.root, text=True).strip()
        self.git = git
        git("init", "-q")
        git("config", "user.name", "Docs test")
        git("config", "user.email", "docs@example.invalid")
        (self.root / "README.md").write_text("# Heading\n\n[target](docs/page.md#topic)\n")
        (self.root / "docs").mkdir()
        (self.root / "docs/page.md").write_text("# Topic\n")
        git("add", ".")
        git("commit", "-qm", "base")
        self.base = git("rev-parse", "HEAD")

    def test_changed_links_and_anchors_must_exist(self):
        (self.root / "README.md").write_text("[target](docs/page.md#topic)\n")
        check(self.base, self.root)
        (self.root / "README.md").write_text("[target](docs/page.md#missing)\n")
        with self.assertRaisesRegex(ValueError, "anchor"):
            check(self.base, self.root)

    def test_deletion_checks_incoming_links_from_unchanged_documents(self):
        (self.root / "docs/page.md").unlink()
        with self.assertRaisesRegex(ValueError, "target"):
            check(self.base, self.root)

    def test_changed_anchor_checks_incoming_links_from_unchanged_documents(self):
        (self.root / "docs/page.md").write_text("# Renamed topic\n")
        with self.assertRaisesRegex(ValueError, "anchor"):
            check(self.base, self.root)

    def test_skill_metadata_is_required(self):
        skill = self.root / ".claude/skills/test/SKILL.md"
        skill.parent.mkdir(parents=True)
        skill.write_text("# No frontmatter\n")
        with self.assertRaisesRegex(ValueError, "metadata"):
            check(self.base, self.root)
        skill.write_text("---\nname: test\ndescription: Actual purpose\n---\n# Skill\n")
        check(self.base, self.root)

    def test_fenced_examples_are_not_broken_document_links(self):
        (self.root / "README.md").write_text("```markdown\n[example](not-a-real-file.md)\n```\n")
        check(self.base, self.root)
