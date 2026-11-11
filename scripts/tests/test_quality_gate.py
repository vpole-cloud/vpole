import importlib.util
import io
import json
import os
import re
import threading
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("quality_gate", Path(__file__).parents[1] / "quality_gate.py")
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class EvidenceTests(unittest.TestCase):
    def test_format_failure_reports_evidence_without_dumping_every_source_argument(self):
        command = ["formatter", "a-very-long-source-list.cs"]
        with patch.object(gate.sys, "argv", ["quality_gate.py", "check-format"]), \
                patch.object(gate, "format_command", return_value=command), \
                patch.object(gate.subprocess, "run", return_value=subprocess.CompletedProcess(command, 1)), \
                patch.object(gate.sys, "stderr", new_callable=io.StringIO) as errors:
            self.assertEqual(1, gate.main())
        self.assertIn("CleanupCode formatting failed", errors.getvalue())
        self.assertNotIn("a-very-long-source-list.cs", errors.getvalue())

    def test_format_commands_use_safe_wrapper_without_losing_source_paths(self):
        paths = ["file with spaces.cs", "-leading.cs"]
        with patch.object(gate, "changed_csharp", return_value=paths):
            check = gate.format_command("origin/master")
            write = gate.format_command("origin/master", fix=True)
        self.assertEqual([gate.sys.executable, "scripts/cleanupcode.py", "--check", "--", *paths], check)
        self.assertEqual([gate.sys.executable, "scripts/cleanupcode.py", "--write", "--", *paths], write)

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

    def test_profiles_partition_all_projects(self):
        projects = ["tests/A.Tests.csproj", "tests/A.Tests.Integration.csproj",
                    "tests/A.Tests.Kubernetes.csproj", "tests/A.Tests.E2E.csproj"]
        with patch.object(gate, "test_projects", return_value=projects):
            self.assertEqual(projects[:2], gate.selected_projects("pr"))
            self.assertEqual(projects, gate.selected_projects("full"))

    def test_parallelism_config_validates_and_overrides_defaults(self):
        path = self.root / "limits.json"
        path.write_text('{"fast": 8, "services": 3}')
        with patch.dict(os.environ, {"CI": "true"}):
            self.assertEqual({"fast": 8, "services": 3, "kubernetes": 1, "e2e": 1}, gate.parallelism(path))
        for value in ('{"fast": 0}', '{"services": true}', '{"unknown": 1}', '[]'):
            path.write_text(value)
            with self.assertRaises(gate.GateError):
                gate.parallelism(path)

    def test_build_and_integration_limits_keep_legacy_flat_group_settings(self):
        path = self.root / "limits.json"
        path.write_text('{"services": 3, "buildMaxCpuCount": 6, "integrationMaxParallelThreads": 8}')
        with patch.dict(os.environ, {"CI": "true"}):
            settings = gate.gate_settings(path)
        self.assertEqual(6, settings["buildMaxCpuCount"])
        self.assertEqual(8, settings["integrationMaxParallelThreads"])
        self.assertEqual(3, gate.parallelism(path)["services"])
        self.assertIn("-maxcpucount:6", gate.build_options(settings["buildMaxCpuCount"]))
        self.assertFalse(any(argument.startswith("-maxcpucount") for argument in gate.build_options("auto")))
        for value in ('{"buildMaxCpuCount": 0}', '{"buildMaxCpuCount": true}',
                      '{"buildMaxCpuCount": "all"}', '{"integrationMaxParallelThreads": -1}'):
            path.write_text(value)
            with self.assertRaises(gate.GateError):
                gate.gate_settings(path)

    def test_build_and_integration_defaults_follow_local_or_ci_resources(self):
        with patch.object(gate, "ROOT", self.root), patch.dict(os.environ, {"CI": "true"}):
            self.assertEqual(2, gate.gate_settings()["buildMaxCpuCount"])
            self.assertEqual(2, gate.gate_settings()["integrationMaxParallelThreads"])
        with patch.object(gate, "ROOT", self.root), patch.dict(os.environ, {"CI": ""}):
            self.assertEqual("auto", gate.gate_settings()["buildMaxCpuCount"])
            self.assertEqual(4, gate.gate_settings()["integrationMaxParallelThreads"])

    def test_coverage_uses_evaluated_product_names_and_preserves_migrations_and_child_processes(self):
        items = [dict(Kind="Project", Role=role, IsTestProject=is_test, AssemblyName=name)
                 for name, role, is_test in (("Product.Core", "Production", "false"),
                                            ("Blog", "Production", "false"),
                                            ("Product.Migrations", "Migration", "false"),
                                            ("Product.Testing", "TestSupport", "false"),
                                            ("Product.Tests", "Production", "true"))]
        settings = self.root / "coverage.runsettings"
        evidence = gate.write_coverage_settings(items, settings)
        self.assertEqual(["Blog", "Product.Core", "Product.Migrations"], evidence["assemblies"])
        root = ET.parse(settings).getroot()
        self.assertEqual("False", root.findtext(".//IncludeTestAssembly"))
        self.assertEqual("True", root.findtext(".//CollectFromChildProcesses"))
        patterns = [node.text for node in root.findall(".//ModulePath")]
        for module in ("/bin/Product.Core.dll", r"C:\bin\Product.Migrations.dll", "/bin/Blog.dll"):
            self.assertTrue(any(re.search(pattern, module) for pattern in patterns))
        for module in ("/bin/ProductXCore.dll", "/bin/Product.Testing.dll", "/bin/Product.Tests.dll", "/bin/ThirdParty.dll"):
            self.assertFalse(any(re.search(pattern, module) for pattern in patterns))
        self.assertNotIn("CompilerGeneratedAttribute", settings.read_text())
        self.assertEqual(64, len(evidence["sha256"]))

    def test_coverage_rejects_empty_or_invalid_product_inventory(self):
        for role, name in (("Other", "Product"), ("Production", ""), ("TestSupport", "Helper")):
            with self.subTest(role=role, name=name), self.assertRaises(gate.GateError):
                gate.write_coverage_settings([dict(Kind="Project", Role=role, IsTestProject="false", AssemblyName=name)],
                                             self.root / "coverage.runsettings")

    def test_coverage_rejects_unexpected_vendor_modules(self):
        report = self.root / "test.cobertura.xml"
        report.write_text('<coverage><packages><package name="Vendor" /></packages></coverage>')
        with self.assertRaisesRegex(gate.GateError, "excluded modules"):
            gate.read_coverage_reports(self.root, ["Product"])

    def test_coverage_project_discovery_excludes_worktrees_outputs_and_node_modules(self):
        paths = ["src/Feature/Feature.csproj", "tests/Fixture.fsproj", "src/Feature/obj/Other.csproj",
                 "src/Feature/.tmp/Other.csproj", "src/node_modules/Other.csproj", ".tmp/other/src/Other.csproj"]
        for relative in paths:
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("<Project />")
        self.assertEqual([self.root / paths[0], self.root / paths[1]], gate.project_files(self.root))

    def test_coverage_missing_project_metadata_cannot_produce_a_successful_allowlist(self):
        path = self.root / "Product.csproj"
        data = {"TargetResults": {"CollectArchitectureInputs": {"Items": []}}}
        (self.root / "coverage-metadata.log").write_text(json.dumps(data))
        with patch.object(gate, "ROOT", self.root), patch.object(gate, "project_files", return_value=[path]), \
                patch.object(gate, "execute"), self.assertRaisesRegex(gate.GateError, "inventory differs"):
            gate.prepare_coverage(self.root, [])

    def test_pr_report_inventory_requires_only_selected_projects(self):
        (self.root / "tests").mkdir()
        for name in ("Fast", "Heavy.Kubernetes"):
            (self.root / "tests" / (name + ".csproj")).write_text(
                '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>')
        (self.root / "Pole.slnx").write_text(
            '<Solution><Project Path="tests/Fast.csproj" /><Project Path="tests/Heavy.Kubernetes.csproj" /></Solution>')
        report = self.trx()
        report.write_text(report.read_text().replace("<Results>",
            '<TestDefinitions><UnitTest><TestMethod codeBase="/bin/Fast.dll" /></UnitTest></TestDefinitions><Results>'))
        gate.check_backend_reports(self.root, self.root, "pr")
        with self.assertRaises(gate.GateError):
            gate.check_backend_reports(self.root, self.root, "full")

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

    def backend_fixture(self, missing_coverage=False, fail_second=False, names=("A", "B")):
        projects = [f"tests/{name}.csproj" for name in names]
        (self.root / "tests").mkdir()
        (self.root / "src").mkdir()
        (self.root / "src" / "Product.csproj").write_text("<Project />")
        for name in names:
            (self.root / "tests" / f"{name}.csproj").write_text(f"<Project><PropertyGroup><AssemblyName>{name}</AssemblyName></PropertyGroup></Project>")
        report = self.trx().read_text()
        calls = []

        def execute(name, command, directory, steps, environment=None):
            if name == "coverage-metadata":
                items = [dict(Identity=str(path), Kind="Project", ProjectPath=str(path), Role="Production",
                              IsTestProject="true" if path.parent.name == "tests" else "false", AssemblyName=path.stem)
                         for path in gate.project_files(self.root)]
                data = {"TargetResults": {"CollectArchitectureInputs": {"Items": items}}}
                (directory / "coverage-metadata.log").write_text(json.dumps(data))
                return
            calls.append((name, command, environment))
            if fail_second and name == "backend-1":
                raise gate.GateError("controlled command failure")
            output = Path(command[command.index("--results-directory") + 1])
            output.mkdir()
            assembly = Path(command[2]).stem
            (output / "results.trx").write_text(report.replace("<Results>", f'<TestDefinitions><UnitTest><TestMethod codeBase="/bin/{assembly}.dll" /></UnitTest></TestDefinitions><Results>'))
            if not missing_coverage:
                (output / "test.cobertura.xml").write_text('<coverage><packages><package name="Product" /></packages></coverage>')

        return projects, calls, execute

    def test_backend_lane_runs_every_project_sequentially_with_the_same_collector(self):
        projects, calls, execute = self.backend_fixture()
        output = self.root / "output"
        with patch.object(gate, "ROOT", self.root), patch.object(gate, "test_projects", return_value=projects), \
                patch.object(gate, "execute", side_effect=execute), patch.object(gate, "capture_headroom"):
            result = gate.run_backend(output)
        self.assertEqual(projects, list(result))
        self.assertEqual(projects, [command[2] for _, command, _ in calls])
        for _, command, environment in calls:
            self.assertIn("--collect:Code Coverage;Format=Cobertura", command)
            for argument in ("--no-build", "--no-restore", "--nologo", "-maxcpucount:1", "-nodeReuse:false"):
                self.assertIn(argument, command)
            self.assertNotIn("--filter", command)
            self.assertIn("POLE_TEST_EVIDENCE_DIRECTORY", environment)
        manifest = json.loads((output / "backend-manifest.json").read_text())
        self.assertEqual("passed", manifest["status"])
        self.assertTrue(manifest["coverage"])
        self.assertEqual(["Product"], manifest["coverage_settings"]["assemblies"])
        self.assertEqual(["Product"], manifest["coverage_modules"][projects[0]])

    def test_backend_parallelism_overlaps_independent_projects(self):
        projects, calls, execute = self.backend_fixture()
        rendezvous = threading.Barrier(2)

        def simultaneous(*args, **kwargs):
            if args[0].startswith("backend-"):
                rendezvous.wait(timeout=5)
            execute(*args, **kwargs)

        with patch.object(gate, "ROOT", self.root), patch.object(gate, "test_projects", return_value=projects), \
                patch.object(gate, "execute", side_effect=simultaneous), patch.object(gate, "capture_headroom"):
            result = gate.run_backend(self.root / "output", limits=dict.fromkeys(gate.GROUPS, 2))
        self.assertEqual(set(projects), set(result))
        self.assertEqual(2, len(calls))

    def test_integration_parallelism_uses_standard_runner_settings_only_for_central_suite(self):
        projects, calls, execute = self.backend_fixture(names=("Pole.Tests.Integration", "Feature.Tests.Integration"))
        with patch.object(gate, "ROOT", self.root), patch.object(gate, "test_projects", return_value=projects), \
                patch.object(gate, "execute", side_effect=execute), patch.object(gate, "capture_headroom"):
            gate.run_backend(self.root / "output", integration_max_parallel_threads=3)
        central = next(command for _, command, _ in calls if command[2] == projects[0])
        feature = next(command for _, command, _ in calls if command[2] == projects[1])
        self.assertEqual(["--", "xUnit.MaxParallelThreads=3"], central[-2:])
        self.assertNotIn("--", feature)
        self.assertIn("-maxcpucount:1", central)

    def test_pr_gate_does_not_require_cluster_commands(self):
        with patch.object(gate, "ROOT", self.root), patch.object(gate, "ARTIFACTS", self.root / "quality"), \
                patch.object(gate, "RECEIPT", self.root / "receipt.json"), \
                patch.object(gate, "git", return_value="base"), patch.object(gate, "source_fingerprint", return_value="unchanged"), \
                patch.object(gate, "test_projects", return_value=["tests/A.csproj"]), patch.object(gate, "capture_headroom"), \
                patch.object(gate, "execute") as execute, patch.object(gate, "format_command", return_value=None), \
                patch.object(gate, "read_vitest", return_value={"total": 1, "passed": 1}), patch("builtins.print"), \
                patch.object(gate, "run_backend"):
            gate.run_full("origin/master", "pr")
        names = [call.args[0] for call in execute.call_args_list]
        self.assertNotIn("lab-verify", names)
        self.assertNotIn("resource-audit", names)
        self.assertIn("frontend-tests", names)

    def test_failed_backend_command_retains_failure_manifest_and_headroom(self):
        projects, calls, execute = self.backend_fixture(fail_second=True)
        output = self.root / "output"
        with patch.object(gate, "ROOT", self.root), patch.object(gate, "test_projects", return_value=projects), \
                patch.object(gate, "execute", side_effect=execute), patch.object(gate, "capture_headroom") as snapshots:
            with self.assertRaisesRegex(gate.GateError, "controlled command failure"):
                gate.run_backend(output)
        manifest = json.loads((output / "backend-manifest.json").read_text())
        self.assertEqual("failed", manifest["status"])
        self.assertEqual([projects[0]], list(manifest["tests"]))
        self.assertEqual("failed-backend", snapshots.call_args.args[1])

    def test_missing_coverage_blocks_backend_success(self):
        projects, _, execute = self.backend_fixture(missing_coverage=True)
        with patch.object(gate, "ROOT", self.root), patch.object(gate, "test_projects", return_value=projects), \
                patch.object(gate, "execute", side_effect=execute), patch.object(gate, "capture_headroom"):
            with self.assertRaisesRegex(gate.GateError, "coverage"):
                gate.run_backend(self.root / "output")

    def test_existing_backend_results_cannot_be_reused_as_new_evidence(self):
        output = self.root / "output"
        output.mkdir()
        (output / "backend-manifest.json").write_text('{}')
        with patch.object(gate, "ROOT", self.root), self.assertRaisesRegex(gate.GateError, "fresh directory"):
            gate.run_backend(output)

    def test_full_gate_uses_the_common_backend_lane(self):
        with patch.object(gate, "ROOT", self.root), patch.object(gate, "ARTIFACTS", self.root / "quality"), \
                patch.object(gate, "RECEIPT", self.root / "receipt.json"), \
                patch.object(gate, "git", return_value="base"), patch.object(gate, "source_fingerprint", return_value="unchanged"), \
                patch.object(gate, "test_projects", return_value=["tests/A.csproj"]), patch.object(gate, "capture_headroom"), \
                patch.object(gate, "execute"), patch.object(gate, "format_command", return_value=None), \
                patch.object(gate, "read_vitest", return_value={"total": 1, "passed": 1}), patch("builtins.print"), \
                patch.object(gate, "run_backend") as backend:
            gate.run_full("origin/master")
        backend.assert_called_once()
        self.assertEqual("passed", json.loads((self.root / "receipt.json").read_text())["status"])

    def test_headroom_drops_native_messages_specs_and_credentials(self):
        output = self.root / "output"
        output.mkdir()
        state = self.root / "state.json"
        state.write_text(json.dumps({"tools": {"kubectlPath": "kubectl"}, "targets": [
            {"key": "private-canary", "kubeconfigPath": "credentials-canary", "context": "context"}]}))
        nodes = {"items": [{"spec": {"token": "spec-canary"}, "status": {"conditions": [
            {"type": "Ready", "status": "True", "message": "message-canary"},
            {"type": "DiskPressure", "status": "False"}]}}]}
        with patch.object(gate, "ROOT", self.root), patch.dict(gate.os.environ, {"POLE_K8S_LAB_STATE": str(state)}), \
                patch.object(gate.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, json.dumps(nodes).encode())):
            gate.capture_headroom(output, "before-backend")
        data = (output / "headroom-before-backend.json").read_text()
        self.assertNotIn("canary", data)
        self.assertTrue(json.loads(data)["targets"][0]["nodes"][0]["Ready"])
        self.assertFalse(json.loads(data)["targets"][0]["nodes"][0]["DiskPressure"])


if __name__ == "__main__":
    unittest.main()
