"""Offline-only fixed directory mutation protocol tests."""

import copy
import json
import shutil
import subprocess
import unittest
from unittest import mock
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import protocol
FIXTURES = Path(__file__).with_name("fixtures")
DIRECTORY_FIXTURE = json.loads((FIXTURES / "read_transfer_directory.json").read_text(encoding="utf-8"))
SOURCE_FIXTURE = json.loads((FIXTURES / "read_transfer_source.json").read_text(encoding="utf-8"))


def request(action, payload):
    return {"protocolVersion": 3, "executionEngineVersion": "ManagedChromium-v3",
            "correlationId": "11111111-1111-1111-1111-111111111111", "operationId": "22222222-2222-2222-2222-222222222222",
            "accountId": "33333333-3333-3333-3333-333333333333", "generation": 7, "action": action, "payload": payload}


def directory_payload(): return {**DIRECTORY_FIXTURE["request"]["payload"], "timeoutMilliseconds": 5_000}
def directory_observed(**changes): return {**copy.deepcopy(DIRECTORY_FIXTURE["result"]), **changes}
def source_observed(**changes): return {**copy.deepcopy(SOURCE_FIXTURE["result"]), **changes}


def source_binding():
    return {key: value for key, value in directory_payload().items() if key not in ("expectedParentExactId", "expectedCanonicalPath")}


def original_source_binding():
    return {**SOURCE_FIXTURE["request"]["payload"], "timeoutMilliseconds": 5_000}


def mutation_payload(action, **changes):
    body = {**source_binding(), "expectedDirectory": directory_observed()}
    if action == "OpenTransferDirectoryRoot": body["rootBreadcrumbExactId"] = "root-1"
    elif action == "EnterTransferDirectory": body.update(childExactId="101", childCanonicalPath="/目标目录/测试文件夹")
    elif action in ("BeginCreateTransferDirectory", "ConfirmCreateTransferDirectory"): body["newSegment"] = "created"
    return {**body, **changes}


class RootProtocolTests(unittest.TestCase):
    def test_dispatch_bound_rechecks_exact_target_id_and_all_post_dispatch_failures_are_unknown(self):
        import transfer_directory_mutation

        actions = ("OpenTransferDirectoryRoot", "EnterTransferDirectory", "BeginCreateTransferDirectory",
                   "ConfirmCreateTransferDirectory", "CancelTransferPicker")
        for action in actions:
            for mutation in ("cancel", "browser", "replacement", "target-id"):
                with self.subTest(action=action, mutation=mutation):
                    state = {"cancelled": False}

                    class Target:
                        target_id = "synthetic-target"

                        def current_url(self, _remaining):
                            return directory_payload()["sourceUri"]

                        def mutate_transfer_directory(self, current_action, _payload, _remaining):
                            if mutation == "cancel": state["cancelled"] = True
                            elif mutation == "browser": connector.browser_id = "55555555-5555-5555-5555-555555555555"
                            elif mutation == "replacement": connector.target = object()
                            elif mutation == "target-id": self.target_id = "rebound-target"
                            return {"kind": protocol.DIRECTORY_MUTATION_SUCCESS_KINDS[current_action]}

                    class Connector:
                        browser_id = directory_payload()["browserId"]
                        target = Target()

                        def get_target(self, _target_id, _remaining):
                            return self.target

                    connector = Connector()
                    self.assertEqual(
                        {"kind": "OutcomeUnknown"},
                        transfer_directory_mutation.dispatch_bound(
                            action, mutation_payload(action), connector,
                            is_cancelled=lambda: state["cancelled"]),
                    )

    def test_pre_invocation_budget_stop_keeps_typed_denial_and_zero_invocations(self):
        import transfer_directory_mutation
        from transfer_source import _Stopped

        for kind in ("Cancelled", "TimedOut"):
            with self.subTest(kind=kind):
                class Budget:
                    fail_next = False

                    def call(self, operation):
                        if self.fail_next:
                            raise _Stopped(kind)
                        return operation(1.0)

                    def remaining(self):
                        return 1.0

                budget = Budget()

                class Target:
                    target_id = directory_payload()["targetId"]
                    invocations = 0

                    def current_url(self, _remaining):
                        return directory_payload()["sourceUri"]

                    def mutate_transfer_directory(self, _action, _payload, _remaining):
                        self.invocations += 1
                        return {"kind": "PickerCancelled"}

                class Connector:
                    target = Target()
                    browser_reads = 0

                    @property
                    def browser_id(self):
                        self.browser_reads += 1
                        if self.browser_reads == 5:
                            budget.fail_next = True
                        return directory_payload()["browserId"]

                    def get_target(self, _target_id, _remaining):
                        return self.target

                connector = Connector()
                self.assertEqual({"kind": kind}, transfer_directory_mutation.dispatch_bound(
                    "CancelTransferPicker", mutation_payload("CancelTransferPicker"), connector, budget=budget))
                self.assertEqual(0, connector.target.invocations)

    def test_closed_observer_rechecks_browser_object_and_exact_target_id(self):
        import main

        payload = {**original_source_binding(), "expectedSource": source_observed()}
        for mutation in ("cancel", "browser", "replacement", "target-id", "initial-target-id"):
            with self.subTest(mutation=mutation):
                state = {"cancelled": False}

                class Target:
                    target_id = "wrong-target" if mutation == "initial-target-id" else payload["targetId"]

                    def observe_transfer_picker_closed(self, _payload, _remaining):
                        if mutation == "cancel": state["cancelled"] = True
                        elif mutation == "browser": connector.browser_id = "55555555-5555-5555-5555-555555555555"
                        elif mutation == "replacement": connector.target = object()
                        elif mutation == "target-id": self.target_id = "rebound-target"
                        return {"kind": "PickerClosed"}

                class Connector:
                    browser_id = payload["browserId"]
                    target = Target()

                    def get_target(self, _target_id, _remaining):
                        return self.target

                connector = Connector()
                with mock.patch.object(main, "_attach_existing", return_value=connector):
                    expected = "Cancelled" if mutation == "cancel" else "BrowserUnavailable"
                    self.assertEqual({"kind": expected}, main.observe_transfer_picker_closed(
                        payload, is_cancelled=lambda: state["cancelled"]))

    def test_mutation_adapter_never_treats_a_bare_click_acknowledgement_as_success(self):
        import transfer_directory_mutation

        for action, kind in (("OpenTransferDirectoryRoot", "RootOpened"),
                             ("EnterTransferDirectory", "DirectoryEntered")):
            with self.subTest(action=action):
                class BareAcknowledgementTab:
                    def run_js(self, *_args, **_kwargs):
                        return {"kind": kind}

                self.assertEqual(
                    {"kind": "OutcomeUnknown"},
                    transfer_directory_mutation.mutate_directory(
                        BareAcknowledgementTab(), action, mutation_payload(action), 1.0),
                )

    def test_mutation_adapter_reduces_a_complete_root_observation_to_the_typed_result(self):
        import transfer_directory_mutation

        post = directory_observed(
            generationToken="root-generation-2",
            parentExactId="root-1",
            canonicalPath="/",
            ancestorExactIds=["root-1"],
        )
        post.pop("observationFingerprint")

        class CompleteObservationTab:
            def run_js(self, *_args, **_kwargs):
                return {"kind": "RootOpened", "postDirectory": post}

        self.assertEqual(
            {"kind": "RootOpened"},
            transfer_directory_mutation.mutate_directory(
                CompleteObservationTab(),
                "OpenTransferDirectoryRoot",
                mutation_payload("OpenTransferDirectoryRoot"),
                1.0,
            ),
        )

        child_post = directory_observed(
            generationToken="child-generation-2",
            parentExactId="101",
            canonicalPath="/目标目录/测试文件夹",
            ancestorExactIds=["root-1", "parent-1", "101"],
            totalCount=1,
            items=[{"exactId": "301", "kind": "Folder", "serverName": "子目录内容"}],
        )
        child_post.pop("observationFingerprint")

        class CompleteChildObservationTab:
            def run_js(self, *_args, **_kwargs):
                return {"kind": "DirectoryEntered", "postDirectory": child_post}

        self.assertEqual(
            {"kind": "DirectoryEntered"},
            transfer_directory_mutation.mutate_directory(
                CompleteChildObservationTab(),
                "EnterTransferDirectory",
                mutation_payload("EnterTransferDirectory"),
                1.0,
            ),
        )

    def test_closed_adapter_requires_and_reduces_the_complete_final_source_observation(self):
        import transfer_directory_mutation

        payload = {**original_source_binding(), "expectedSource": source_observed()}
        post = source_observed()
        post.pop("observationFingerprint")

        class BareAcknowledgementTab:
            def run_js(self, *_args, **_kwargs):
                return {"kind": "PickerClosed"}

        class CompleteObservationTab:
            def run_js(self, *_args, **_kwargs):
                return {"kind": "PickerClosed", "postSource": post}

        self.assertEqual(
            {"kind": "PageNotValidated"},
            transfer_directory_mutation.observe_picker_closed(BareAcknowledgementTab(), payload, 1.0),
        )
        self.assertEqual(
            {"kind": "PickerClosed"},
            transfer_directory_mutation.observe_picker_closed(CompleteObservationTab(), payload, 1.0),
        )

    def test_open_root_is_a_fixed_capability(self):
        self.assertIn("OpenTransferDirectoryRoot", protocol.REQUIRED_CAPABILITIES)
        self.assertIn("OpenTransferDirectoryRoot", protocol.ALLOWED_ACTIONS)

    def test_enter_directory_is_a_fixed_capability(self):
        self.assertIn("EnterTransferDirectory", protocol.REQUIRED_CAPABILITIES)

    def test_begin_create_is_a_fixed_capability(self):
        self.assertIn("BeginCreateTransferDirectory", protocol.REQUIRED_CAPABILITIES)

    def test_confirm_create_is_a_fixed_capability(self):
        self.assertIn("ConfirmCreateTransferDirectory", protocol.REQUIRED_CAPABILITIES)

    def test_cancel_picker_is_a_fixed_capability(self):
        self.assertIn("CancelTransferPicker", protocol.REQUIRED_CAPABILITIES)

    def test_closed_observer_is_a_fixed_read_only_capability(self):
        self.assertIn("ObserveTransferPickerClosed", protocol.REQUIRED_CAPABILITIES)

    def test_every_directory_action_rejects_unbound_or_dynamic_input_and_pairs_its_result(self):
        actions = ("OpenTransferDirectoryRoot", "EnterTransferDirectory", "BeginCreateTransferDirectory",
                   "ConfirmCreateTransferDirectory", "CancelTransferPicker")
        successes = ("RootOpened", "DirectoryEntered", "CreateDialogOpened", "DirectoryCreated", "PickerCancelled")
        for action, success in zip(actions, successes):
            operation = request(action, mutation_payload(action))
            protocol.validate_request(operation)
            protocol.response_for(operation, {"kind": success})
            for body in (mutation_payload(action, selector="private"), mutation_payload(action, script="private"),
                         mutation_payload(action, expectedDirectory=directory_observed(baiduUserId="wrong")),
                         mutation_payload(action, expectedDirectory=directory_observed(observationFingerprint="0" * 64))):
                with self.assertRaises(protocol.ProtocolError): protocol.validate_request(request(action, body))
            with self.assertRaises(protocol.ProtocolError): protocol.response_for(operation, {"kind": "RootOpened" if success != "RootOpened" else "DirectoryEntered"})

    def test_enter_directory_rejects_a_folder_name_that_can_derive_multiple_path_segments(self):
        import transfer_directory_listing

        directory = directory_observed(items=[
            {"exactId": "101", "kind": "Folder", "serverName": "bad/name"},
            {"exactId": "202", "kind": "Folder", "serverName": " spaced name "},
        ])
        unsigned = {key: value for key, value in directory.items() if key != "observationFingerprint"}
        directory["observationFingerprint"] = transfer_directory_listing.compute_directory_fingerprint(
            source_binding()["sourceUri"], unsigned)
        body = mutation_payload("EnterTransferDirectory", expectedDirectory=directory,
                                childCanonicalPath="/目标目录/bad/name")
        with self.assertRaises(protocol.ProtocolError):
            protocol.validate_request(request("EnterTransferDirectory", body))

    def test_closed_proof_requires_exact_original_source_and_never_accepts_generic_page(self):
        body = {**original_source_binding(), "expectedSource": source_observed()}
        operation = request("ObserveTransferPickerClosed", body)
        protocol.validate_request(operation)
        protocol.response_for(operation, {"kind": "PickerClosed"})
        for changed in (source_observed(baiduUserId="wrong"), source_observed(observationFingerprint="0" * 64),
                        {"kind": "PageNotValidated"}):
            with self.assertRaises(protocol.ProtocolError): protocol.validate_request(request("ObserveTransferPickerClosed", {**body, "expectedSource": changed}))
        with self.assertRaises(protocol.ProtocolError): protocol.response_for(operation, {"kind": "OutcomeUnknown"})

    def test_actual_fixed_scripts_run_only_in_the_synthetic_node_vm(self):
        import transfer_directory_mutation
        node = shutil.which("node")
        self.assertIsNotNone(node, "Node required for the offline fixed-script proof")
        result = subprocess.run([node, str(Path(__file__).with_name("transfer_directory_mutation_harness.cjs"))],
            input=json.dumps({"mutation": transfer_directory_mutation.DIRECTORY_MUTATION_SCRIPT,
                              "closed": transfer_directory_mutation.PICKER_CLOSED_SCRIPT,
                              "directory": directory_observed(), "source": source_observed()}),
            capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("offline directory mutation assertions passed", result.stdout)

    def test_pre_dispatch_cancellation_is_typed_for_every_fixed_action_and_closed_observer(self):
        import main
        import transfer_directory_mutation
        actions = ("OpenTransferDirectoryRoot", "EnterTransferDirectory", "BeginCreateTransferDirectory",
                   "ConfirmCreateTransferDirectory", "CancelTransferPicker")
        for action in actions:
            self.assertEqual({"kind": "Cancelled"}, transfer_directory_mutation.dispatch_bound(
                action, mutation_payload(action), object(), is_cancelled=lambda: True))
        self.assertEqual({"kind": "Cancelled"}, main.observe_transfer_picker_closed(
            {**original_source_binding(), "expectedSource": source_observed()}, is_cancelled=lambda: True))


if __name__ == "__main__":
    unittest.main()
