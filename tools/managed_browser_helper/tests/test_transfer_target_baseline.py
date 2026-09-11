"""Strict offline protocol tests for the fixed transfer-target baseline actions."""

import copy
import io
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import protocol
import main
from test_protocol import request


SOURCE = {
    "kind": "Observed", "baiduUserId": "uid-1", "generationToken": "source-generation",
    "totalCount": 1, "reachedEnd": True, "isStable": True,
    "items": [{"exactId": "source-1", "kind": "File", "serverName": "source.txt"}],
    "observationFingerprint": "4cdb5ac7a450d6125ebaef9287cb8431f3976b5374ebc5de96c2570786995956",
}


def binding():
    return {"browserId": "44444444-4444-4444-4444-444444444444", "targetId": "target-1",
            "devToolsEndpoint": "http://127.0.0.1:43127/", "timeoutMilliseconds": 30000,
            "sourceUri": "https://pan.baidu.com/s/source_1?pwd=A1b2", "expectedBaiduUserId": "uid-1",
            "expectedSource": copy.deepcopy(SOURCE)}


def raw_inventory(items=None, **changes):
    value = {"kind": "Observed", "scope": "AllItems",
             "browserId": binding()["browserId"], "targetId": "target-1",
             "devToolsEndpoint": "http://127.0.0.1:43127/", "baiduUserId": "uid-1",
             "generationToken": "generation-1", "directoryExactId": "parent-1",
             "canonicalPath": "/parent", "ancestorExactIds": ["root-1", "parent-1"],
             "totalCount": 2, "reachedEnd": True, "isStable": True, "enumerationStepCount": 3,
             "items": items or [
                 {"exactId": "child-1", "kind": "Folder", "serverName": "child"},
                 {"exactId": "file-2", "kind": "File", "serverName": "two.txt"}], **changes}
    return value


def inventory(**changes):
    fingerprint = changes.pop("observationFingerprint", None)
    value = raw_inventory(**changes)
    value["observationFingerprint"] = fingerprint or protocol.compute_target_inventory_fingerprint(value)
    return value


def root_inventory():
    return raw_inventory(directoryExactId="root-1", canonicalPath="/", ancestorExactIds=["root-1"],
                         enumerationStepCount=1)


def paged_root_inventory():
    return raw_inventory(directoryExactId="root-1", canonicalPath="/", ancestorExactIds=["root-1"],
                         totalCount=5, enumerationStepCount=3,
                         items=[{"exactId": "a", "kind": "Folder", "serverName": "a"},
                                {"exactId": "b", "kind": "File", "serverName": "b"},
                                {"exactId": "c", "kind": "File", "serverName": "c"},
                                {"exactId": "d", "kind": "Folder", "serverName": "d"},
                                {"exactId": "e", "kind": "Folder", "serverName": "e"}])


def header():
    value = inventory()
    return {"scope": value["scope"], "browserId": value["browserId"], "targetId": value["targetId"],
            "devToolsEndpoint": value["devToolsEndpoint"], "baiduUserId": value["baiduUserId"],
            "generationToken": value["generationToken"], "directoryExactId": value["directoryExactId"],
            "canonicalPath": value["canonicalPath"], "ancestorExactIds": value["ancestorExactIds"],
            "totalCount": value["totalCount"], "reachedEnd": True, "isStable": True,
            "enumerationStepCount": value["enumerationStepCount"],
            "parentObservationFingerprint": value["observationFingerprint"]}


class TargetBaselineProtocolTests(unittest.TestCase):
    def test_drission_root_adapter_preserves_single_field_shape_through_dispatch(self):
        import transfer_target_baseline as target_baseline

        class Tab:
            tab_id = "target-1"
            def get(self, *_args, **_kwargs): pass
            def run_js(self, script, *_args, **_kwargs):
                self.assert_script = script
                return {"kind": "RootOpened"}
        class Connector:
            browser_id = binding()["browserId"]
            def __init__(self, target): self.target = target
            def get_target(self, _target_id, _remaining): return self.target
        class Budget:
            def call(self, operation): return operation(30.0)

        target = main._DrissionTarget(Tab())
        self.assertEqual({"kind": "RootOpened", "navigationState": "RouteOpened"},
                         target_baseline.dispatch_bound("OpenTransferTargetRoot", binding(),
                                                        Connector(target), budget=Budget()))

    def test_drission_enter_adapter_preserves_click_phase_through_dispatch(self):
        import transfer_target_baseline as target_baseline

        parent = raw_inventory()
        child = raw_inventory(generationToken="generation-2", directoryExactId="child-1",
                              canonicalPath="/parent/child",
                              ancestorExactIds=["root-1", "parent-1", "child-1"], totalCount=1,
                              items=[{"exactId": "nested-1", "kind": "File", "serverName": "nested.txt"}])
        class Tab:
            tab_id = "target-1"
            def __init__(self): self.reads = [parent, child]
            def run_js(self, script, *_args, **_kwargs):
                if script == target_baseline.TARGET_ENTER_CHILD_SCRIPT:
                    return {"kind": "DirectoryEntered", "navigationAttempted": True}
                if script == target_baseline.TARGET_INVENTORY_PASS_ONE_SCRIPT:
                    return copy.deepcopy(self.reads.pop(0))
                raise AssertionError("unexpected fixed script")
        class Connector:
            browser_id = binding()["browserId"]
            def __init__(self, target): self.target = target
            def get_target(self, _target_id, _remaining): return self.target
        class Budget:
            def call(self, operation): return operation(30.0)

        target = main._DrissionTarget(Tab())
        payload = {**binding(), "expectedParent": header(), "childExactId": "child-1",
                   "childServerName": "child", "childCanonicalPath": "/parent/child"}
        self.assertEqual({"kind": "DirectoryEntered", "navigationState": "RouteOpened"},
                         target_baseline.dispatch_bound("EnterTransferTargetDirectory", payload,
                                                        Connector(target), budget=Budget()))

    def test_drission_enter_adapter_forwards_complete_parent_fingerprint_marker(self):
        captured = []
        class Tab:
            tab_id = "target-1"
            def run_js(self, script, *args, **_kwargs):
                captured.append((script, args))
                return {"kind": "PageNotValidated", "navigationAttempted": False}

        payload = {**binding(), "expectedParent": header(), "childExactId": "child-1",
                   "childServerName": "child", "childCanonicalPath": "/parent/child"}
        value = main._DrissionTarget(Tab()).enter_target_child(payload, raw_inventory(), 30.0)
        self.assertEqual({"kind": "PageNotValidated", "navigationAttempted": False}, value)
        self.assertEqual(payload["expectedParent"]["parentObservationFingerprint"], captured[0][1][9])

    def test_bound_dispatch_preserves_exact_binding_and_fixed_action_sequence(self):
        import transfer_target_baseline as target_baseline

        parent = raw_inventory()
        child = raw_inventory(generationToken="generation-2", directoryExactId="child-1",
                              canonicalPath="/parent/child",
                              ancestorExactIds=["root-1", "parent-1", "child-1"],
                              items=[{"exactId": "nested-1", "kind": "File", "serverName": "nested.txt"}],
                              totalCount=1)

        class Budget:
            def call(self, operation): return operation(30.0)

        class Target:
            target_id = "target-1"

            def __init__(self): self.calls = []; self.reads = []
            def open_target_root(self, uid, remaining):
                self.calls.append(("root", uid)); return {"kind": "RootOpened"}
            def read_target_inventory(self, payload, expected_id, expected_path, expected_ancestors, remaining):
                self.calls.append(("read", expected_id, expected_path, expected_ancestors))
                return copy.deepcopy(self.reads.pop(0))
            def enter_target_child(self, payload, observed_parent, remaining):
                self.calls.append(("enter", observed_parent))
                return {"kind": "DirectoryEntered", "navigationAttempted": True}
            def restore_source(self, payload, remaining):
                self.calls.append(("restore", payload["sourceUri"])); return {"kind": "SourceRestored"}

        class Connector:
            browser_id = binding()["browserId"]
            def __init__(self, target): self.target = target
            def get_target(self, target_id, _remaining):
                return self.target if target_id == self.target.target_id else None

        target = Target(); connector = Connector(target); budget = Budget()
        self.assertEqual({"kind": "RootOpened", "navigationState": "RouteOpened"},
                         target_baseline.dispatch_bound("OpenTransferTargetRoot", binding(), connector, budget=budget))

        target.reads = [parent, child]
        enter_payload = {**binding(), "expectedParent": header(), "childExactId": "child-1",
                         "childServerName": "child", "childCanonicalPath": "/parent/child"}
        self.assertEqual({"kind": "DirectoryEntered", "navigationState": "RouteOpened"},
                         target_baseline.dispatch_bound("EnterTransferTargetDirectory", enter_payload,
                                                        connector, budget=budget))
        self.assertNotIn("items", enter_payload["expectedParent"])
        self.assertEqual(parent, next(call[1] for call in target.calls if call[0] == "enter"))

        target.reads = [parent, copy.deepcopy(parent)]
        read_payload = {**binding(), "expectedDirectoryExactId": "parent-1",
                        "expectedCanonicalPath": "/parent",
                        "expectedAncestorExactIds": ["root-1", "parent-1"]}
        result = target_baseline.dispatch_bound("ReadTransferTargetDirectory", read_payload,
                                                connector, budget=budget)
        self.assertEqual(protocol.compute_target_inventory_fingerprint(parent), result["observationFingerprint"])

        target.reads = [parent]
        self.assertEqual({"kind": "SourceRestored", "navigationState": "RouteOpened"},
                         target_baseline.dispatch_bound("OpenTransferSourceAfterTargetRead",
                                                        {**binding(), "expectedTarget": header()},
                                                        connector, budget=budget))

    def test_bound_dispatch_distinguishes_pre_dispatch_denial_and_post_dispatch_unknown(self):
        import transfer_target_baseline as target_baseline

        class Budget:
            def call(self, operation): return operation(30.0)
        class RaisingTarget:
            target_id = "target-1"
            def open_target_root(self, _uid, _remaining): raise RuntimeError("redacted")
        class Connector:
            def __init__(self, browser_id): self.browser_id = browser_id; self.target = RaisingTarget()
            def get_target(self, _target_id, _remaining): return self.target

        denied = target_baseline.dispatch_bound("OpenTransferTargetRoot", binding(),
                    Connector("55555555-5555-5555-5555-555555555555"), budget=Budget())
        self.assertEqual({"kind": "BrowserUnavailable", "navigationState": "KnownNotDispatched"}, denied)
        unknown = target_baseline.dispatch_bound("OpenTransferTargetRoot", binding(),
                                                 Connector(binding()["browserId"]), budget=Budget())
        self.assertEqual({"kind": "BrowserUnavailable", "navigationState": "RouteOutcomeUnknown"}, unknown)

    def test_enter_uses_internal_click_phase_for_navigation_state(self):
        import transfer_target_baseline as target_baseline

        class Budget:
            def call(self, operation): return operation(30.0)
        class Target:
            target_id = "target-1"
            def __init__(self, entered): self.entered = entered; self.click_calls = 0
            def read_target_inventory(self, _payload, _id, _path, _ancestors, _remaining):
                return raw_inventory()
            def enter_target_child(self, _payload, _parent, _remaining):
                self.click_calls += 1
                if isinstance(self.entered, BaseException): raise self.entered
                return copy.deepcopy(self.entered)
        class Connector:
            browser_id = binding()["browserId"]
            def __init__(self, target): self.target = target
            def get_target(self, _target_id, _remaining): return self.target

        payload = {**binding(), "expectedParent": header(), "childExactId": "child-1",
                   "childServerName": "child", "childCanonicalPath": "/parent/child"}
        for kind in ("PageNotValidated", "LoginRequired", "VerificationRequired"):
            target = Target({"kind": kind, "navigationAttempted": False})
            result = target_baseline.dispatch_bound("EnterTransferTargetDirectory", payload,
                                                    Connector(target), budget=Budget())
            self.assertEqual({"kind": kind, "navigationState": "KnownNotDispatched"}, result)
            self.assertEqual(1, target.click_calls)
        target = Target({"kind": "OutcomeUnknown", "navigationAttempted": True})
        self.assertEqual({"kind": "BrowserUnavailable", "navigationState": "RouteOutcomeUnknown"},
                         target_baseline.dispatch_bound("EnterTransferTargetDirectory", payload,
                                                        Connector(target), budget=Budget()))
        target = Target(RuntimeError("redacted"))
        self.assertEqual({"kind": "BrowserUnavailable", "navigationState": "RouteOutcomeUnknown"},
                         target_baseline.dispatch_bound("EnterTransferTargetDirectory", payload,
                                                        Connector(target), budget=Budget()))

    def test_source_restore_rechecks_complete_expected_target_before_navigation_dispatch(self):
        import transfer_target_baseline as target_baseline

        class Budget:
            def call(self, operation): return operation(30.0)
        class Target:
            target_id = "target-1"
            def __init__(self, observed): self.observed = observed; self.restores = 0; self.read_args = None
            def read_target_inventory(self, payload, exact_id, path, ancestors, _remaining):
                self.read_args = (exact_id, path, ancestors)
                return copy.deepcopy(self.observed)
            def restore_source(self, _payload, _remaining):
                self.restores += 1
                return {"kind": "SourceRestored"}
        class Connector:
            browser_id = binding()["browserId"]
            def __init__(self, target): self.target = target
            def get_target(self, _target_id, _remaining): return self.target

        payload = {**binding(), "expectedTarget": header()}
        changed = raw_inventory(generationToken="changed")
        target = Target(changed)
        denied = target_baseline.dispatch_bound("OpenTransferSourceAfterTargetRead", payload,
                                                Connector(target), budget=Budget())
        self.assertEqual({"kind": "PageNotValidated", "navigationState": "KnownNotDispatched"}, denied)
        self.assertEqual(0, target.restores)
        self.assertEqual(("parent-1", "/parent", ["root-1", "parent-1"]), target.read_args)

        target = Target(raw_inventory())
        opened = target_baseline.dispatch_bound("OpenTransferSourceAfterTargetRead", payload,
                                                Connector(target), budget=Budget())
        self.assertEqual({"kind": "SourceRestored", "navigationState": "RouteOpened"}, opened)
        self.assertEqual(1, target.restores)

    def test_budget_stop_immediately_before_navigation_or_click_is_known_not_dispatched(self):
        import transfer_source
        import transfer_target_baseline as target_baseline

        class Budget:
            def __init__(self, stop_at, kind): self.calls = 0; self.stop_at = stop_at; self.kind = kind
            def call(self, operation):
                self.calls += 1
                if self.calls == self.stop_at: raise transfer_source._Stopped(self.kind)
                return operation(30.0)
        class Target:
            target_id = "target-1"
            def __init__(self): self.navigations = 0
            def open_target_root(self, _uid, _remaining):
                self.navigations += 1; return {"kind": "RootOpened"}
            def read_target_inventory(self, _payload, _id, _path, _ancestors, _remaining):
                return raw_inventory()
            def restore_source(self, _payload, _remaining):
                self.navigations += 1; return {"kind": "SourceRestored"}
        class Connector:
            browser_id = binding()["browserId"]
            def __init__(self, target): self.target = target
            def get_target(self, _target_id, _remaining): return self.target

        target = Target()
        root = target_baseline.dispatch_bound("OpenTransferTargetRoot", binding(), Connector(target),
                                              budget=Budget(7, "Cancelled"))
        self.assertEqual({"kind": "Cancelled", "navigationState": "KnownNotDispatched"}, root)
        self.assertEqual(0, target.navigations)

        target = Target()
        restore = target_baseline.dispatch_bound("OpenTransferSourceAfterTargetRead",
                    {**binding(), "expectedTarget": header()}, Connector(target), budget=Budget(16, "TimedOut"))
        self.assertEqual({"kind": "TimedOut", "navigationState": "KnownNotDispatched"}, restore)
        self.assertEqual(0, target.navigations)

    def test_main_dispatches_all_four_actions_only_to_fixed_target_handler(self):
        actions = {
            "OpenTransferTargetRoot": (binding(), {"kind": "RootOpened", "navigationState": "RouteOpened"}),
            "EnterTransferTargetDirectory": ({**binding(), "expectedParent": header(), "childExactId": "child-1",
                                               "childServerName": "child", "childCanonicalPath": "/parent/child"},
                                              {"kind": "DirectoryEntered", "navigationState": "RouteOpened"}),
            "ReadTransferTargetDirectory": ({**binding(), "expectedDirectoryExactId": "parent-1",
                                               "expectedCanonicalPath": "/parent",
                                               "expectedAncestorExactIds": ["root-1", "parent-1"]}, inventory()),
            "OpenTransferSourceAfterTargetRead": ({**binding(), "expectedTarget": header()},
                                                   {"kind": "SourceRestored", "navigationState": "RouteOpened"}),
        }
        calls = []
        for index, (action, (payload, expected)) in enumerate(actions.items(), start=1):
            handshake = request()
            operation = request(action, payload)
            operation["correlationId"] = f"11111111-1111-1111-1111-{index:012d}"
            output, events = io.BytesIO(), io.StringIO()

            def handler(actual_action, actual_payload, expected=expected):
                calls.append((actual_action, actual_payload))
                return copy.deepcopy(expected)

            code = main.run(io.BytesIO(protocol.encode_message(handshake) + protocol.encode_message(operation)),
                            output, events, helper_pid=9876, parent_pid=1234,
                            target_baseline_handler=handler)
            self.assertEqual(0, code)
            self.assertEqual("", events.getvalue())
            stream = io.BytesIO(output.getvalue())
            protocol.read_message(stream, response=True)
            response = protocol.read_message(stream, response=True)
            self.assertEqual(expected, response["result"])
        self.assertEqual([(action, payload) for action, (payload, _) in actions.items()], calls)

    def test_four_actions_and_strict_result_pairs_roundtrip(self):
        actions = {
            "OpenTransferTargetRoot": (binding(), {"kind": "RootOpened", "navigationState": "RouteOpened"}),
            "EnterTransferTargetDirectory": ({**binding(), "expectedParent": header(), "childExactId": "child-1",
                                               "childServerName": "child", "childCanonicalPath": "/parent/child"},
                                              {"kind": "DirectoryEntered", "navigationState": "RouteOpened"}),
            "ReadTransferTargetDirectory": ({**binding(), "expectedDirectoryExactId": "parent-1",
                                               "expectedCanonicalPath": "/parent",
                                               "expectedAncestorExactIds": ["root-1", "parent-1"]}, inventory()),
            "OpenTransferSourceAfterTargetRead": ({**binding(), "expectedTarget": header()},
                                                   {"kind": "SourceRestored", "navigationState": "RouteOpened"}),
        }
        for action, (payload, result) in actions.items():
            with self.subTest(action=action):
                operation = request(action, payload)
                self.assertEqual(operation, protocol.read_message(io.BytesIO(protocol.encode_message(operation))))
                response = protocol.response_for(operation, result)
                protocol.validate_response_for(operation, response)
        operation = request("OpenTransferTargetRoot", binding())
        with self.assertRaises(protocol.ProtocolError):
            protocol.response_for(operation, {"kind": "DirectoryEntered", "navigationState": "RouteOpened"})

    def test_observed_response_must_match_request_binding_and_expected_directory_exactly(self):
        operation = request("ReadTransferTargetDirectory", {**binding(),
                            "expectedDirectoryExactId": "parent-1", "expectedCanonicalPath": "/parent",
                            "expectedAncestorExactIds": ["root-1", "parent-1"]})
        protocol.validate_response_for(operation, protocol.response_for(operation, inventory()))
        variants = (
            {"browserId": "55555555-5555-5555-5555-555555555555"},
            {"targetId": "target-2"}, {"devToolsEndpoint": "http://127.0.0.1:43128/"},
            {"baiduUserId": "uid-2"},
            {"directoryExactId": "parent-2", "ancestorExactIds": ["root-1", "parent-2"]},
            {"canonicalPath": "/other"}, {"ancestorExactIds": ["other-root", "parent-1"]},
        )
        for changes in variants:
            with self.subTest(changes=changes):
                with self.assertRaises(protocol.ProtocolError):
                    protocol.response_for(operation, inventory(**changes))

    def test_omitted_expected_directory_means_root_and_rejects_nested_observed_inventory(self):
        operation = request("ReadTransferTargetDirectory", {**binding(),
                            "expectedDirectoryExactId": None, "expectedCanonicalPath": None,
                            "expectedAncestorExactIds": None})
        root = inventory(directoryExactId="root-1", canonicalPath="/", ancestorExactIds=["root-1"])
        protocol.validate_response_for(operation, protocol.response_for(operation, root))
        with self.assertRaises(protocol.ProtocolError):
            protocol.response_for(operation, inventory())

    def test_response_for_rejects_internally_valid_inventory_with_mismatched_binding(self):
        payload = {**binding(), "expectedDirectoryExactId": "parent-1",
                   "expectedCanonicalPath": "/parent",
                   "expectedAncestorExactIds": ["root-1", "parent-1"]}
        operation = request("ReadTransferTargetDirectory", payload)
        mismatched = inventory(targetId="target-2")
        with self.assertRaises(protocol.ProtocolError):
            protocol.response_for(operation, mismatched)

    def test_main_emits_protocol_error_and_no_action_response_for_mismatched_inventory(self):
        payload = {**binding(), "expectedDirectoryExactId": "parent-1",
                   "expectedCanonicalPath": "/parent",
                   "expectedAncestorExactIds": ["root-1", "parent-1"]}
        operation = request("ReadTransferTargetDirectory", payload)
        mismatched = inventory(targetId="target-2")
        operation["correlationId"] = "11111111-1111-1111-1111-000000000002"
        incoming = protocol.encode_message(request()) + protocol.encode_message(operation)
        output, events = io.BytesIO(), io.StringIO()
        code = main.run(io.BytesIO(incoming), output, events, helper_pid=9876, parent_pid=1234,
                        target_baseline_handler=lambda _action, _payload: copy.deepcopy(mismatched))
        self.assertEqual(2, code)
        self.assertEqual("HELPER_PROTOCOL_ERROR\n", events.getvalue())
        stream = io.BytesIO(output.getvalue())
        self.assertEqual("HandshakeAccepted", protocol.read_message(stream, response=True)["result"]["kind"])
        self.assertIsNone(protocol.read_message(stream, response=True))

    def test_navigation_state_is_exact_and_post_dispatch_unknown_is_explicit(self):
        operation = request("OpenTransferTargetRoot", binding())
        for result in ({"kind": "TimedOut", "navigationState": "KnownNotDispatched"},
                       {"kind": "LoginRequired", "navigationState": "RouteOutcomeUnknown"},
                       {"kind": "RootOpened", "navigationState": "RouteOpened"}):
            protocol.response_for(operation, result)
        for result in ({"kind": "RootOpened", "navigationState": "KnownNotDispatched"},
                       {"kind": "TimedOut", "navigationState": "RouteOpened"},
                       {"kind": "TimedOut", "navigationState": "Unknown"}):
            with self.assertRaises(protocol.ProtocolError): protocol.response_for(operation, result)

    def test_enter_has_header_only_is_small_and_rejects_dynamic_fields(self):
        payload = {**binding(), "expectedParent": header(), "childExactId": "child-1",
                   "childServerName": "child", "childCanonicalPath": "/parent/child"}
        encoded = protocol.encode_message(request("EnterTransferTargetDirectory", payload))
        self.assertLess(len(encoded), 65_536)
        self.assertNotIn("items", payload["expectedParent"])
        for field in ("items", "script", "selector", "url"):
            with self.subTest(field=field), self.assertRaises(protocol.ProtocolError):
                protocol.validate_request(request("EnterTransferTargetDirectory", {**payload, field: []}))

    def test_enter_maximum_small_fields_fit_below_64k_and_path_or_aggregate_overage_is_rejected(self):
        segments = ['"' * (56 if index == 62 else 60) for index in range(63)]
        parent_path = "/" + "/".join(segments)
        self.assertEqual(3839, len(parent_path.encode("utf-8")))
        ancestors = [f"{index:03d}" + "a" * 125 for index in range(64)]
        child_id, child_name = "c" * 128, '"' * 256
        bound = {**binding(), "targetId": "t" * 256}
        parent = raw_inventory(items=[{"exactId": child_id, "kind": "Folder", "serverName": child_name}],
            browserId=bound["browserId"], targetId=bound["targetId"], baiduUserId=bound["expectedBaiduUserId"],
            generationToken='"' * 256, directoryExactId=ancestors[-1], canonicalPath=parent_path,
            ancestorExactIds=ancestors, totalCount=1, enumerationStepCount=64)
        parent_fingerprint = protocol.compute_target_inventory_fingerprint(parent)
        expected_parent = {field: parent[field] for field in (
            "scope", "browserId", "targetId", "devToolsEndpoint", "baiduUserId", "generationToken",
            "directoryExactId", "canonicalPath", "ancestorExactIds", "totalCount", "reachedEnd",
            "isStable", "enumerationStepCount")}
        expected_parent["parentObservationFingerprint"] = parent_fingerprint
        payload = {**bound, "expectedParent": expected_parent, "childExactId": child_id,
                   "childServerName": child_name, "childCanonicalPath": parent_path + "/" + child_name}
        operation = request("EnterTransferTargetDirectory", payload)
        encoded = protocol.encode_message(operation)
        self.assertLess(len(encoded), 65_536)

        over_parent = copy.deepcopy(expected_parent)
        over_parent["canonicalPath"] = parent_path + "x"
        over_raw = {**parent, "canonicalPath": over_parent["canonicalPath"]}
        over_parent["parentObservationFingerprint"] = protocol.compute_target_inventory_fingerprint(over_raw)
        with self.assertRaises(protocol.ProtocolError):
            protocol.validate_request(request("EnterTransferTargetDirectory", {
                **bound, "expectedParent": over_parent, "childExactId": child_id,
                "childServerName": child_name,
                "childCanonicalPath": over_parent["canonicalPath"] + "/" + child_name}))

        compact = encoded[4:]
        oversized = compact[:-1] + b" " * (65_532 - len(compact)) + b"}"
        with self.assertRaises(protocol.ProtocolError):
            protocol.read_message(io.BytesIO(struct.pack(">I", len(oversized)) + oversized))

    def test_inventory_fingerprint_and_bounds_are_strict(self):
        value = inventory()
        self.assertEqual("3a0645f30f6e3386f0c26111bb512957df3eb36de54fd4663b735e64283a17c8",
                         value["observationFingerprint"])
        reversed_value = raw_inventory(items=list(reversed(value["items"])))
        self.assertEqual(value["observationFingerprint"],
                         protocol.compute_target_inventory_fingerprint(reversed_value))
        operation = request("ReadTransferTargetDirectory", {**binding(), "expectedDirectoryExactId": "parent-1",
                            "expectedCanonicalPath": "/parent", "expectedAncestorExactIds": ["root-1", "parent-1"]})
        protocol.response_for(operation, value)
        invalid = (
            inventory(scope="FolderCandidates"), inventory(reachedEnd=False), inventory(isStable=False),
            inventory(enumerationStepCount=65), inventory(totalCount=1),
            inventory(ancestorExactIds=["parent-1"]), inventory(observationFingerprint="0" * 64),
        )
        duplicate = inventory(items=[{"exactId": "same", "kind": "File", "serverName": "a"},
                                     {"exactId": "same", "kind": "Folder", "serverName": "b"}])
        for candidate in (*invalid, duplicate):
            with self.assertRaises(protocol.ProtocolError): protocol.response_for(operation, candidate)

    def test_1024_worst_case_items_fit_frame_and_1025_is_rejected(self):
        items = [{"exactId": f"{index:04d}" + "x" * 124,
                  "kind": "File" if index % 2 == 0 else "Folder", "serverName": '"' * 256}
                 for index in range(1024)]
        value = inventory(items=items, totalCount=1024, enumerationStepCount=64)
        operation = request("ReadTransferTargetDirectory", {**binding(), "expectedDirectoryExactId": "parent-1",
                            "expectedCanonicalPath": "/parent", "expectedAncestorExactIds": ["root-1", "parent-1"]})
        encoded = protocol.encode_message(protocol.response_for(operation, value), response=True)
        self.assertLessEqual(len(encoded), protocol.MAX_FRAME_BYTES)
        too_many = items + [{"exactId": "extra", "kind": "File", "serverName": "x"}]
        with self.assertRaises(protocol.ProtocolError):
            protocol.response_for(operation, inventory(items=too_many, totalCount=1025))

    def test_root_open_fixed_script_in_node_vm(self):
        import transfer_target_baseline as target
        node = shutil.which("node")
        self.assertIsNotNone(node, "offline fixed-script tests require Node")
        result = subprocess.run([node, str(Path(__file__).with_name("transfer_target_baseline_harness.cjs"))],
            input=json.dumps({"mode": "root", "rootScript": target.TARGET_ROOT_PROOF_SCRIPT}),
            capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("target baseline assertions passed", result.stdout)

    def test_parent_read_fixed_script_in_node_vm(self):
        import transfer_target_baseline as target
        node = shutil.which("node")
        self.assertIsNotNone(node)
        result = subprocess.run([node, str(Path(__file__).with_name("transfer_target_baseline_harness.cjs"))],
            input=json.dumps({"mode": "parent", "parentScript": target.TARGET_PARENT_READ_SCRIPT}),
            capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("target baseline assertions passed", result.stdout)

    def test_drission_read_adapter_forwards_three_null_root_proof_fields(self):
        captured = []
        root = raw_inventory(directoryExactId="root-1", canonicalPath="/",
                             ancestorExactIds=["root-1"])
        class Tab:
            tab_id = "target-1"
            def run_js(self, script, *args, **_kwargs):
                captured.append((script, args))
                return copy.deepcopy(root)

        value = main._DrissionTarget(Tab()).read_target_inventory(binding(), None, None, None, 30.0)
        self.assertEqual(root, value)
        self.assertEqual((None, None, None), captured[0][1][5:8])

    def test_exact_child_enter_fixed_script_in_node_vm(self):
        import transfer_target_baseline as target
        node = shutil.which("node")
        self.assertIsNotNone(node)
        result = subprocess.run([node, str(Path(__file__).with_name("transfer_target_baseline_harness.cjs"))],
            input=json.dumps({"mode": "enter", "parentScript": target.TARGET_PARENT_READ_SCRIPT,
                              "enterScript": target.TARGET_ENTER_CHILD_SCRIPT,
                              "parentFingerprint": protocol.compute_target_inventory_fingerprint(root_inventory())}),
            capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("target baseline assertions passed", result.stdout)

    def test_exact_child_enter_walks_paged_parent_before_clicking_current_virtual_node(self):
        import transfer_target_baseline as target
        node = shutil.which("node")
        self.assertIsNotNone(node)
        result = subprocess.run(
            [node, str(Path(__file__).with_name("transfer_target_baseline_harness.cjs"))],
            input=json.dumps({"mode": "paged-enter", "parentScript": target.TARGET_PARENT_READ_SCRIPT,
                              "enterScript": target.TARGET_ENTER_CHILD_SCRIPT,
                              "pagedParentFingerprint": protocol.compute_target_inventory_fingerprint(paged_root_inventory())}),
            capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("target baseline assertions passed", result.stdout)

    def test_post_entry_read_fixed_script_in_node_vm(self):
        import transfer_target_baseline as target
        node = shutil.which("node")
        self.assertIsNotNone(node)
        result = subprocess.run([node, str(Path(__file__).with_name("transfer_target_baseline_harness.cjs"))],
            input=json.dumps({"mode": "post-read", "postReadScript": target.TARGET_POST_ENTRY_READ_SCRIPT}),
            capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("target baseline assertions passed", result.stdout)

    def test_inventory_pass_one_uses_fixed_all_items_reader(self):
        import transfer_target_baseline as target
        self.assertEqual(target.TARGET_POST_ENTRY_READ_SCRIPT, target.TARGET_INVENTORY_PASS_ONE_SCRIPT)
        self.assertNotIn("arguments[8]", target.TARGET_INVENTORY_PASS_ONE_SCRIPT)

    def test_all_items_reader_walks_fixed_viewports_and_rejects_unstable_enumeration(self):
        import transfer_target_baseline as target
        node = shutil.which("node")
        self.assertIsNotNone(node)
        completed = subprocess.run(
            [node, str(Path(__file__).with_name("transfer_target_baseline_harness.cjs"))],
            input=json.dumps({"mode": "paged", "passScript": target.TARGET_INVENTORY_PASS_ONE_SCRIPT}),
            capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertIn("target baseline assertions passed", completed.stdout)

    def test_inventory_second_pass_requires_exact_wire_equality(self):
        import transfer_target_baseline as target
        stable = raw_inventory()
        result = target.select_stable_inventory_passes(copy.deepcopy(stable), copy.deepcopy(stable))
        self.assertEqual("Observed", result["kind"])
        self.assertEqual(protocol.compute_target_inventory_fingerprint(stable), result["observationFingerprint"])
        for field, value in (("generationToken", "changed"), ("totalCount", 1), ("directoryExactId", "changed"),
                             ("canonicalPath", "/changed"), ("ancestorExactIds", ["other"]),
                             ("enumerationStepCount", 2), ("items", [{"exactId": "other", "kind": "File", "serverName": "x"}])):
            second = copy.deepcopy(stable); second[field] = value
            self.assertEqual({"kind": "PageNotValidated"}, target.select_stable_inventory_passes(stable, second))
        self.assertEqual({"kind": "PageNotValidated"},
                         target.select_stable_inventory_passes(stable, {"kind": "PageNotValidated"}))

    def test_between_pass_dom_mutations_are_visible_to_equality_gate(self):
        import transfer_target_baseline as target
        node = shutil.which("node")
        self.assertIsNotNone(node)
        harness = Path(__file__).with_name("transfer_target_baseline_harness.cjs")
        for mutation in ("generation", "total", "item", "path", "ancestry", "page", "malformed"):
            completed = subprocess.run([node, str(harness)], input=json.dumps({"mode": "probe-passes",
                "passScript": target.TARGET_INVENTORY_PASS_ONE_SCRIPT, "mutation": mutation}),
                capture_output=True, text=True, encoding="utf-8", timeout=20)
            self.assertEqual(0, completed.returncode, completed.stderr)
            passes = json.loads(completed.stdout)
            self.assertEqual({"kind": "PageNotValidated"},
                             target.select_stable_inventory_passes(passes["first"], passes["second"]))

    def test_source_restore_fixed_script_in_node_vm(self):
        import transfer_target_baseline as target
        node = shutil.which("node")
        self.assertIsNotNone(node)
        completed = subprocess.run([node, str(Path(__file__).with_name("transfer_target_baseline_harness.cjs"))],
            input=json.dumps({"mode": "restore", "restoreScript": target.SOURCE_RESTORE_PROOF_SCRIPT}),
            capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertIn("target baseline assertions passed", completed.stdout)


if __name__ == "__main__":
    unittest.main()
