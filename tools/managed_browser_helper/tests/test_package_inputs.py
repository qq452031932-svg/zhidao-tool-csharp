"""Fast source/package regression; real offline packaging is verified separately."""

import ast
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


class PackageInputTests(unittest.TestCase):
    EXPECTED_CAPABILITIES = (
        "Health", "Shutdown", "ObserveAccountIdentity", "OpenTransferSource",
        "ReadTransferSource", "ReadTransferDirectory", "OpenTransferPicker",
        "OpenTransferDirectoryRoot", "EnterTransferDirectory",
        "BeginCreateTransferDirectory", "ConfirmCreateTransferDirectory",
        "CancelTransferPicker", "ObserveTransferPickerClosed",
        "OpenTransferTargetRoot", "EnterTransferTargetDirectory",
        "ReadTransferTargetDirectory", "OpenTransferSourceAfterTargetRead",
        "ObserveTransferSubmissionReady", "SubmitTransferOnce",
        "ObserveTransferSubmissionOutcome", "ReadShareOfficialListing",
        "SubmitShareOnce", "ObserveShareSubmissionOutcome", "SubmitExtractionCode",
    )
    EXPECTED_HELPER_SOURCE_FILES = (
        "main.py", "protocol.py", "identity_query.py", "transfer_source.py",
        "transfer_source_listing.py", "transfer_directory_listing.py",
        "transfer_directory_mutation.py", "transfer_picker.py",
        "transfer_target_baseline.py", "transfer_submission.py",
        "share_submission.py", "session_operations.py",
        "requirements.lock", "THIRD_PARTY_NOTICES.txt",
    )

    @staticmethod
    def _root():
        return Path(__file__).resolve().parents[3]

    @classmethod
    def _write_verifier_fixture(
            cls, root, protocol_version, engine_version, manifest_mutator=None,
            manifest_bytes_mutator=None):
        contents = {
            "THIRD_PARTY_NOTICES.txt": b"test notices\n",
            "identity_query.py": b"def observe_identity():\n    return None\n",
            "main.py": b"import protocol\n",
            "protocol.py": b"PROTOCOL_VERSION = 3\n",
            "python.exe": b"MZ\x00\x01",
            "python314.dll": b"MZ\x00\x02",
            "requirements.lock": b"locked\n",
            "transfer_directory_listing.py": b"def list_directory():\n    return []\n",
            "transfer_directory_mutation.py": b"def mutate_directory():\n    return None\n",
            "transfer_picker.py": b"def open_picker():\n    return None\n",
            "transfer_source.py": b"def open_source():\n    return None\n",
            "transfer_source_listing.py": b"def list_source():\n    return []\n",
            "transfer_submission.py": b"def submit_once():\n    return None\n",
            "share_submission.py": b"def submit_share_once():\n    return None\n",
            "session_operations.py": b"def dispatch_named_operation():\n    return None\n",
            "transfer_target_baseline.py": b"def read_target():\n    return []\n",
        }
        entries = []
        for relative, payload in sorted(contents.items()):
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
            entries.append({
                "path": relative,
                "length": len(payload),
                "sha256": hashlib.sha256(payload).hexdigest(),
            })
        identity = (
            "manifestVersion=1\n"
            f"protocolVersion={protocol_version}\n"
            f"executionEngineVersion={engine_version}\n"
            "pythonVersion=3.14.0\n"
            "distributionMode=PersonalNonCommercial\n"
        )
        for entry in entries:
            identity += (
                f"{entry['path']}\0{entry['length']}\0{entry['sha256']}\n"
            )
        manifest = {
            "manifestVersion": 1,
            "protocolVersion": protocol_version,
            "executionEngineVersion": engine_version,
            "pythonVersion": "3.14.0",
            "distributionMode": "PersonalNonCommercial",
            "buildIdentity": hashlib.sha256(identity.encode("utf-8")).hexdigest(),
            "files": entries,
        }
        if manifest_mutator is not None:
            manifest_mutator(manifest)
        manifest_bytes = (json.dumps(manifest, indent=2) + "\n").encode("utf-8")
        if manifest_bytes_mutator is not None:
            manifest_bytes = manifest_bytes_mutator(manifest_bytes)
        (root / "helper-manifest.json").write_bytes(manifest_bytes)
        return manifest_bytes

    @classmethod
    def _run_release_verifier(
            cls, protocol_version, engine_version, manifest_mutator=None,
            manifest_bytes_mutator=None, source_mutator=None):
        with tempfile.TemporaryDirectory(prefix="managed-helper-verifier-") as temporary:
            package = Path(temporary) / "package"
            package.mkdir()
            manifest = cls._write_verifier_fixture(
                package, protocol_version, engine_version, manifest_mutator,
                manifest_bytes_mutator)
            manifest_source = Path(temporary) / "fixed-manifest.json"
            manifest_source.write_bytes(manifest)
            helper_source = Path(temporary) / "helper-source"
            helper_source.mkdir()
            for relative in cls.EXPECTED_HELPER_SOURCE_FILES:
                (helper_source / relative).write_bytes((package / relative).read_bytes())
            if source_mutator is not None:
                source_mutator(helper_source)
            return subprocess.run(
                [
                    "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                    "-File", str(cls._root() / "scripts" /
                    "verify_managed_browser_helper_package.ps1"),
                    "-ManifestSource", str(manifest_source),
                    "-PackageDirectory", str(package),
                    "-HelperSourceDirectory", str(helper_source),
                ],
                capture_output=True,
                text=True,
                check=False,
            )

    def test_v3_submission_source_is_required_and_copied_exactly_once(self):
        root = Path(__file__).resolve().parents[3]
        self.assertTrue((root / "tools" / "managed_browser_helper" / "transfer_submission.py").is_file())
        script = (root / "scripts" / "build_managed_browser_helper.ps1").read_text(encoding="utf-8")
        required = re.search(r"foreach \(\$requiredSource in @\((.*?)\)\) \{", script, re.S)
        copied = re.search(r"foreach \(\$name in @\((.*?)\)\) \{", script, re.S)
        self.assertIsNotNone(required)
        self.assertIsNotNone(copied)
        self.assertEqual(1, required.group(1).count("'transfer_submission.py'"))
        self.assertEqual(1, copied.group(1).count("'transfer_submission.py'"))

    def test_package_manifest_versions_are_atomic_v3(self):
        root = Path(__file__).resolve().parents[3]
        script = (root / "scripts" / "build_managed_browser_helper.ps1").read_text(encoding="utf-8")
        self.assertRegex(script, r"\$protocolVersion\s*=\s*3(?:\r?\n)")
        self.assertRegex(script, r"\$executionEngineVersion\s*=\s*'ManagedChromium-v3'")

    def test_build_publishes_only_after_checked_in_manifest_and_notice_match(self):
        script = (self._root() / "scripts" /
                  "build_managed_browser_helper.ps1").read_text(encoding="utf-8")
        publish = "[IO.Directory]::Move($staging, $outputPath)"
        manifest_failure = "generated manifest differs from the checked-in manifest"
        notice_failure = "generated notice differs from the checked-in notice"
        self.assertEqual(1, script.count(publish))
        self.assertIn(manifest_failure, script)
        self.assertIn(notice_failure, script)
        self.assertLess(script.index(manifest_failure), script.index(publish))
        self.assertLess(script.index(notice_failure), script.index(publish))
        self.assertIn("SequenceEqual", script[:script.index(publish)])

    def test_build_never_writes_checked_in_manifest_or_notice(self):
        script = (self._root() / "scripts" /
                  "build_managed_browser_helper.ps1").read_text(encoding="utf-8")
        self.assertNotIn("WriteAllText($embeddedManifestPath", script)
        self.assertNotIn("WriteAllText($noticeSourcePath", script)
        publish = script.index("[IO.Directory]::Move($staging, $outputPath)")
        self.assertNotIn("$embeddedManifestPath", script[publish:])
        self.assertNotIn("$noticeSourcePath", script[publish:])

    def test_release_verifier_source_accepts_only_atomic_v3_metadata(self):
        root = Path(__file__).resolve().parents[3]
        script = (root / "scripts" / "verify_managed_browser_helper_package.ps1").read_text(encoding="utf-8")
        self.assertRegex(script, r"\$protocolVersion\s*=\s*3(?:\r?\n)")
        self.assertRegex(script, r"\$executionEngineVersion\s*=\s*'ManagedChromium-v3'")

    def test_release_verifier_uses_the_exact_fixed_helper_source_list(self):
        script = (self._root() / "scripts" /
                  "verify_managed_browser_helper_package.ps1").read_text(encoding="utf-8")
        match = re.search(r"\$helperSourceNames\s*=\s*@\((.*?)\)\s*(?:\r?\n)",
                          script, re.S)
        self.assertIsNotNone(match)
        actual = tuple(re.findall(r"'([^']+)'", match.group(1)))
        self.assertEqual(self.EXPECTED_HELPER_SOURCE_FILES, actual)

    def test_release_project_call_passes_explicit_helper_source_directory(self):
        project = (self._root() / "src" / "BaiduBatchTransfer.App" /
                   "BaiduBatchTransfer.App.csproj").read_text(encoding="utf-8")
        self.assertIn("-HelperSourceDirectory", project)
        self.assertIn("tools\\managed_browser_helper", project)

    def test_release_verifier_accepts_an_otherwise_valid_v3_package(self):
        result = self._run_release_verifier(3, "ManagedChromium-v3")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_release_verifier_rejects_an_otherwise_valid_v2_package(self):
        result = self._run_release_verifier(2, "ManagedChromium-v2")
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("manifest metadata is incompatible", result.stdout + result.stderr)

    def test_release_verifier_rejects_helper_source_content_drift(self):
        result = self._run_release_verifier(
            3, "ManagedChromium-v3",
            source_mutator=lambda source: (source / "main.py").write_bytes(b"drift\n"))
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("helper source payload differs", result.stdout + result.stderr)

    def test_release_verifier_rejects_missing_helper_source(self):
        result = self._run_release_verifier(
            3, "ManagedChromium-v3",
            source_mutator=lambda source: (source / "protocol.py").unlink())
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("required helper source is missing", result.stdout + result.stderr)

    def test_release_verifier_rejects_extra_root_manifest_property(self):
        result = self._run_release_verifier(
            3, "ManagedChromium-v3",
            lambda manifest: manifest.update({"unexpected": True}))
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("manifest schema is invalid", result.stdout + result.stderr)

    def test_release_verifier_rejects_extra_file_manifest_property(self):
        result = self._run_release_verifier(
            3, "ManagedChromium-v3",
            lambda manifest: manifest["files"][0].update({"unexpected": True}))
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("manifest file schema is invalid", result.stdout + result.stderr)

    def test_release_verifier_rejects_missing_root_manifest_property(self):
        result = self._run_release_verifier(
            3, "ManagedChromium-v3",
            lambda manifest: manifest.pop("pythonVersion"))
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("manifest schema is invalid", result.stdout + result.stderr)

    def test_release_verifier_rejects_missing_file_manifest_property(self):
        result = self._run_release_verifier(
            3, "ManagedChromium-v3",
            lambda manifest: manifest["files"][0].pop("sha256"))
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("manifest file schema is invalid", result.stdout + result.stderr)

    def test_release_verifier_rejects_reordered_root_manifest_properties(self):
        def reorder_root(manifest):
            python_version = manifest.pop("pythonVersion")
            manifest["pythonVersion"] = python_version

        result = self._run_release_verifier(
            3, "ManagedChromium-v3", reorder_root)
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("manifest schema is invalid", result.stdout + result.stderr)

    def test_release_verifier_rejects_reordered_file_manifest_properties(self):
        def reorder_file(manifest):
            entry = manifest["files"][0]
            sha256 = entry.pop("sha256")
            reordered = {"sha256": sha256, **entry}
            manifest["files"][0] = reordered

        result = self._run_release_verifier(
            3, "ManagedChromium-v3", reorder_file)
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("manifest file schema is invalid", result.stdout + result.stderr)

    def test_release_verifier_rejects_wrong_json_types(self):
        mutations = (
            lambda manifest: manifest.update({"protocolVersion": "3"}),
            lambda manifest: manifest["files"][0].update(
                {"length": str(manifest["files"][0]["length"])}),
        )
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                result = self._run_release_verifier(
                    3, "ManagedChromium-v3", mutation)
                self.assertNotEqual(0, result.returncode,
                                    result.stdout + result.stderr)
                self.assertIn("schema is invalid", result.stdout + result.stderr)

    def test_release_verifier_rejects_duplicate_json_property(self):
        def duplicate_protocol_version(manifest_bytes):
            marker = b'  "protocolVersion": 3,\n'
            return manifest_bytes.replace(marker, marker + marker, 1)

        result = self._run_release_verifier(
            3, "ManagedChromium-v3",
            manifest_bytes_mutator=duplicate_protocol_version)
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("duplicate manifest property", result.stdout + result.stderr)

    def test_release_verifier_rejects_escaped_duplicate_json_property(self):
        def duplicate_protocol_version(manifest_bytes):
            marker = b'  "protocolVersion": 3,\n'
            escaped = b'  "protocol\\u0056ersion": 3,\n'
            return manifest_bytes.replace(marker, marker + escaped, 1)

        result = self._run_release_verifier(
            3, "ManagedChromium-v3",
            manifest_bytes_mutator=duplicate_protocol_version)
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("duplicate manifest property", result.stdout + result.stderr)

    def test_duplicate_scanner_ignores_structure_inside_escaped_string_values(self):
        result = self._run_release_verifier(
            3, 'ManagedChromium-v3:{}:[\"quoted\"]')
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("manifest metadata is incompatible", result.stdout + result.stderr)
        self.assertNotIn("manifest JSON is malformed", result.stdout + result.stderr)
        self.assertNotIn("duplicate manifest property", result.stdout + result.stderr)

    def test_protocol_source_declares_exact_twenty_v3_capabilities(self):
        root = self._root()
        tree = ast.parse((root / "tools" / "managed_browser_helper" /
                          "protocol.py").read_text(encoding="utf-8"))
        assignments = {
            node.targets[0].id: ast.literal_eval(node.value)
            for node in tree.body
            if isinstance(node, ast.Assign)
            and len(node.targets) == 1
            and isinstance(node.targets[0], ast.Name)
            and node.targets[0].id in {
                "PROTOCOL_VERSION", "EXECUTION_ENGINE_VERSION", "REQUIRED_CAPABILITIES"
            }
        }
        self.assertEqual(3, assignments["PROTOCOL_VERSION"])
        self.assertEqual("ManagedChromium-v3", assignments["EXECUTION_ENGINE_VERSION"])
        self.assertEqual(self.EXPECTED_CAPABILITIES, assignments["REQUIRED_CAPABILITIES"])
        self.assertEqual(24, len(assignments["REQUIRED_CAPABILITIES"]))

    def test_target_baseline_module_is_required_and_copied(self):
        root = Path(__file__).resolve().parents[3]
        script = (root / "scripts" / "build_managed_browser_helper.ps1").read_text(encoding="utf-8")
        required = re.search(r"foreach \(\$requiredSource in @\((.*?)\)\) \{", script, re.S)
        copied = re.search(r"foreach \(\$name in @\((.*?)\)\) \{", script, re.S)
        self.assertIsNotNone(required)
        self.assertIsNotNone(copied)
        self.assertIn("'transfer_target_baseline.py'", required.group(1))
        self.assertIn("'transfer_target_baseline.py'", copied.group(1))

    def test_directory_listing_module_is_required_and_copied(self):
        root = Path(__file__).resolve().parents[3]
        script = (root / "scripts" / "build_managed_browser_helper.ps1").read_text(encoding="utf-8")
        required = re.search(r"foreach \(\$requiredSource in @\((.*?)\)\) \{", script, re.S)
        copied = re.search(r"foreach \(\$name in @\((.*?)\)\) \{", script, re.S)
        self.assertIsNotNone(required)
        self.assertIsNotNone(copied)
        self.assertIn("'transfer_directory_listing.py'", required.group(1))
        self.assertIn("'transfer_directory_listing.py'", copied.group(1))

    def test_source_listing_module_is_required_and_copied(self):
        root = Path(__file__).resolve().parents[3]
        script = (root / "scripts" / "build_managed_browser_helper.ps1").read_text(encoding="utf-8")
        required = re.search(r"foreach \(\$requiredSource in @\((.*?)\)\) \{", script, re.S)
        copied = re.search(r"foreach \(\$name in @\((.*?)\)\) \{", script, re.S)
        self.assertIsNotNone(required)
        self.assertIsNotNone(copied)
        self.assertIn("'transfer_source_listing.py'", required.group(1))
        self.assertIn("'transfer_source_listing.py'", copied.group(1))

    def test_transfer_source_module_is_required_and_copied(self):
        root = Path(__file__).resolve().parents[3]
        script = (root / "scripts" / "build_managed_browser_helper.ps1").read_text(encoding="utf-8")
        required = re.search(r"foreach \(\$requiredSource in @\((.*?)\)\) \{", script, re.S)
        copied = re.search(r"foreach \(\$name in @\((.*?)\)\) \{", script, re.S)
        self.assertIsNotNone(required)
        self.assertIsNotNone(copied)
        self.assertIn("'transfer_source.py'", required.group(1))
        self.assertIn("'transfer_source.py'", copied.group(1))

    def test_identity_module_is_a_required_and_copied_package_input(self):
        root = Path(__file__).resolve().parents[3]
        script = (root / "scripts" / "build_managed_browser_helper.ps1").read_text(encoding="utf-8")
        required = re.search(r"foreach \(\$requiredSource in @\((.*?)\)\) \{", script, re.S)
        copied = re.search(r"foreach \(\$name in @\((.*?)\)\) \{", script, re.S)
        self.assertIsNotNone(required)
        self.assertIsNotNone(copied)
        self.assertIn("'identity_query.py'", required.group(1))
        self.assertIn("'identity_query.py'", copied.group(1))


if __name__ == "__main__":
    unittest.main()
