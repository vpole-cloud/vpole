# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

import importlib.util
from datetime import datetime, timezone
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

SPEC = importlib.util.spec_from_file_location("create_feature", Path(__file__).parents[1] / "create_feature.py")
starter = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(starter)


class FeatureStarterTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def test_generation_materializes_project_graph_and_normal_source_paths(self):
        output = starter.create_feature("NotebookEntries", "NotebookEntry", "notebook-entries", self.root)
        projects = list(output.glob("*/*.csproj"))
        self.assertEqual(6, len(projects))
        self.assertTrue((output / "NotebookEntries.Application/Endpoints/CreateNotebookEntry.cs").is_file())
        for project in projects:
            xml = ET.parse(project)
            self.assertEqual("Pole.Features." + project.stem, xml.findtext(".//RootNamespace"))
            for reference in xml.findall(".//ProjectReference"):
                target = (project.parent / reference.get("Include")).resolve()
                if str(target).startswith(str(output)):
                    self.assertTrue(target.is_file(), target)
        self.assertFalse((self.root / "Pole.slnx").exists(), "Scaffolding must not silently wire production.")

    def test_existing_feature_is_never_overwritten(self):
        output = starter.create_feature("NotebookEntries", "NotebookEntry", "notebook-entries", self.root)
        marker = output / "user-owned.txt"
        marker.write_text("preserve me")
        with self.assertRaises(FileExistsError):
            starter.create_feature("NotebookEntries", "NotebookEntry", "notebook-entries", self.root)
        self.assertEqual("preserve me", marker.read_text())

    def test_unsafe_identifiers_and_routes_cannot_escape_output(self):
        for feature, resource, route in [("../Escape", "Entry", "entries"), ("Entries", "../Entry", "entries"),
                                          ("Entries", "Entry", "../escape"), ("Entries", "Entry", "MixedCase")]:
            with self.subTest(feature=feature, resource=resource, route=route), self.assertRaises(ValueError):
                starter.create_feature(feature, resource, route, self.root)
        self.assertFalse((self.root / "src").exists())

    def test_replacement_does_not_reprocess_inserted_names(self):
        output = starter.create_feature("ResourceNameCatalog", "CatalogEntry", "catalog-entries", self.root)
        project = output / "ResourceNameCatalog.Domain/ResourceNameCatalog.Domain.csproj"
        self.assertEqual("Pole.Features.ResourceNameCatalog.Domain", ET.parse(project).findtext(".//RootNamespace"))

    def test_migration_identity_is_stable_per_feature_and_distinct_across_features(self):
        first = starter.create_feature("AlphaEntries", "Entry", "entries", self.root)
        second = starter.create_feature("BetaEntries", "Entry", "entries", self.root)
        a = (first / "AlphaEntries.Migrations/AlphaEntriesSchemaMigration.cs").read_text()
        b = (second / "BetaEntries.Migrations/BetaEntriesSchemaMigration.cs").read_text()
        import re
        identity = lambda text: re.search(r'new\("([0-9a-f]{24})"\)', text).group(1)
        self.assertNotEqual(identity(a), identity(b))
        with tempfile.TemporaryDirectory() as other:
            repeated = starter.create_feature("AlphaEntries", "Entry", "entries", Path(other))
            self.assertEqual(identity(a), identity((repeated / "AlphaEntries.Migrations/AlphaEntriesSchemaMigration.cs").read_text()))

    def test_initial_migration_sorts_before_a_followup_from_the_next_schema_day(self):
        import re
        output = starter.create_feature("BetaEntries", "Entry", "entries", self.root)
        source = (output / "BetaEntries.Migrations/BetaEntriesSchemaMigration.cs").read_text()
        identity = re.search(r'new\("([0-9a-f]{24})"\)', source).group(1)
        next_day = int(datetime(2026, 11, 8, tzinfo=timezone.utc).timestamp())
        followup = f"{next_day:08x}" + "0" * 16
        self.assertLess(int(identity, 16), int(followup, 16))
