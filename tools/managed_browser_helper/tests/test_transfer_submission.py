"""Synthetic-only final transfer submission protocol and fixed DOM operations."""

import hashlib
import io
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import unittest
import uuid
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main
import protocol
from test_protocol import request

CONTEXT_FIELDS = ("browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri", "baiduUserId",
                  "sourceGenerationToken", "sourceObservationFingerprint", "sourceFingerprint", "sourceExactId",
                  "sourceItemKind", "selectedTargetExactId", "selectedTargetCanonicalPath",
                  "targetBaselineFingerprint", "duplicatePolicy")
READINESS_KINDS = ("Ready", "PageNotValidated", "AccountIdentityMismatch", "LoginRequired",
                   "VerificationRequired", "BrowserUnavailable", "TimedOut", "Cancelled")
OUTCOME_KINDS = ("AcceptedBound", "UnboundAcknowledgement", "ExplicitlyRejected", "Conflict",
                 "PageNotValidated", "AccountIdentityMismatch", "LoginRequired", "VerificationRequired",
                 "BrowserUnavailable", "TimedOut", "Cancelled")
VALID_SUBMIT_PAIRS = {
    ("ClickAttempted", "ClickDispatched"), ("ClickAttempted", "DispatchOutcomeUnknown"),
    ("ReadinessMismatch", "KnownNotDispatched"), ("PageNotValidated", "KnownNotDispatched"),
    ("AccountIdentityMismatch", "KnownNotDispatched"), ("LoginRequired", "KnownNotDispatched"),
    ("VerificationRequired", "KnownNotDispatched"), ("BrowserUnavailable", "KnownNotDispatched"),
    ("BrowserUnavailable", "DispatchOutcomeUnknown"), ("TimedOut", "KnownNotDispatched"),
    ("TimedOut", "DispatchOutcomeUnknown"), ("Cancelled", "KnownNotDispatched"),
    ("Cancelled", "DispatchOutcomeUnknown"),
}


def context(**changes):
    value = {"browserId": "44444444-4444-4444-4444-444444444444", "targetId": "target-1",
             "devToolsEndpoint": "http://127.0.0.1:43127/", "timeoutMilliseconds": 30000,
             "sourceUri": "https://pan.baidu.com/s/source_1?pwd=A1b2",
             "baiduUserId": "uid-1", "sourceGenerationToken": "source-generation-1",
             "sourceObservationFingerprint": "a" * 64, "sourceFingerprint": "b" * 64,
             "sourceExactId": "source-1", "sourceItemKind": "File",
             "selectedTargetExactId": "selected-target-1", "selectedTargetCanonicalPath": "/prepared",
             "targetBaselineFingerprint": "c" * 64, "duplicatePolicy": "Skip"}
    value.update(changes)
    return value


def length_hash(fields):
    digest = hashlib.sha256()
    for field in fields:
        encoded = field.encode("utf-8", errors="strict")
        digest.update(struct.pack(">I", len(encoded)))
        digest.update(encoded)
    return digest.hexdigest()


def readiness_fingerprint(value):
    return length_hash(("ManagedBrowserTransferSubmissionReadiness/v1", value["browserId"], value["targetId"],
                        value["devToolsEndpoint"], str(value["timeoutMilliseconds"]), value["sourceUri"],
                        value["baiduUserId"], value["sourceGenerationToken"],
                        value["sourceObservationFingerprint"], value["sourceFingerprint"], value["sourceExactId"],
                        value["sourceItemKind"], value["selectedTargetExactId"],
                        value["selectedTargetCanonicalPath"], value["targetBaselineFingerprint"],
                        value["duplicatePolicy"], "1"))


def official_fingerprint(request_id, target_id, source_id, destination_id):
    return length_hash(("ManagedBrowserTransferSubmissionOutcome/v1", request_id, target_id, source_id, destination_id))


def body(action, **changes):
    value = context()
    if action != "ObserveTransferSubmissionReady":
        value["readinessFingerprint"] = readiness_fingerprint(value)
    value.update(changes)
    return value


class TransferSubmissionProtocolTests(unittest.TestCase):
    def validate(self, action, value):
        try:
            protocol.validate_request(request(action, value))
            return "Accepted"
        except protocol.ProtocolError as exc:
            return exc.code

    def test_exact_v3_capability_order_and_three_actions(self):
        self.assertEqual(3, protocol.PROTOCOL_VERSION)
        self.assertEqual("ManagedChromium-v3", protocol.EXECUTION_ENGINE_VERSION)
        self.assertEqual(24, len(protocol.REQUIRED_CAPABILITIES))
        self.assertEqual(("ObserveTransferSubmissionReady", "SubmitTransferOnce",
                          "ObserveTransferSubmissionOutcome"), protocol.TRANSFER_SUBMISSION_ACTIONS)
        for action in protocol.TRANSFER_SUBMISSION_ACTIONS:
            self.assertEqual("Accepted", self.validate(action, body(action)))

    def test_requests_are_exact_bound_and_forbid_dynamic_dom_or_secret_fields(self):
        for action in ("ObserveTransferSubmissionReady", "SubmitTransferOnce", "ObserveTransferSubmissionOutcome"):
            valid = body(action)
            for field in tuple(valid):
                changed = dict(valid); changed.pop(field)
                self.assertEqual("InvalidPayload", self.validate(action, changed), (action, field))
            for field in ("selector", "script", "url", "buttonText", "name", "toast", "pageText", "cookie"):
                self.assertEqual("ForbiddenField", self.validate(action, {**valid, field: "private"}), (action, field))
            self.assertEqual("UnknownField", self.validate(action, {**valid, "retry": True}))
        for field, value in (("browserId", "wrong"), ("targetId", ""),
                             ("devToolsEndpoint", "http://localhost:1/"), ("baiduUserId", ""),
                             ("sourceGenerationToken", ""), ("sourceObservationFingerprint", "0"),
                             ("sourceFingerprint", "0"), ("sourceExactId", "bad/id"),
                             ("sourceItemKind", "file"), ("selectedTargetExactId", "bad/id"),
                             ("selectedTargetCanonicalPath", "prepared"),
                             ("targetBaselineFingerprint", "0"), ("duplicatePolicy", "skip")):
            self.assertEqual("InvalidPayload", self.validate("ObserveTransferSubmissionReady", body(
                "ObserveTransferSubmissionReady", **{field: value})), field)
        for source_uri in ("", " https://pan.baidu.com/s/source_1", "http://pan.baidu.com/s/source_1",
                           "https://foreign.test/s/source_1", "https://pan.baidu.com/s/source_1/",
                           "https://pan.baidu.com/s/source_1?pwd=abc", "https://pan.baidu.com/s/source_1?pwd=abcd&x=1"):
            self.assertEqual("InvalidPayload", self.validate("ObserveTransferSubmissionReady", body(
                "ObserveTransferSubmissionReady", sourceUri=source_uri)), source_uri)
        self.assertEqual("InvalidPayload", self.validate("SubmitTransferOnce", body(
            "SubmitTransferOnce", readinessFingerprint="9" * 64)))

    def test_readiness_fingerprint_matches_csharp_known_answer(self):
        multilingual = context(targetId="目标-一", baiduUserId="用户-甲", sourceGenerationToken="代次-乙",
                               selectedTargetCanonicalPath="/准备/应用", duplicatePolicy="Rename")
        self.assertEqual("cb5757dad5aa8a2d2a5fca0dbd616a4ae9fd696c88a2147291c4c2367cd7c4b0",
                         readiness_fingerprint(multilingual))
        self.assertEqual(readiness_fingerprint(multilingual), protocol.compute_transfer_readiness_fingerprint(multilingual))
        self.assertNotEqual(readiness_fingerprint(multilingual),
                            readiness_fingerprint({**multilingual, "timeoutMilliseconds": 29999}))

    def test_submit_fingerprint_and_ready_response_bind_timeout(self):
        stale = body("SubmitTransferOnce")
        stale["timeoutMilliseconds"] = 29999
        self.assertEqual("InvalidPayload", self.validate("SubmitTransferOnce", stale))

        operation = request("ObserveTransferSubmissionReady", body("ObserveTransferSubmissionReady"))
        changed = context(timeoutMilliseconds=29999)
        result = {"kind": "Ready", **changed, "eligibleControlCount": 1,
                  "readinessFingerprint": readiness_fingerprint(changed)}
        response = request("ObserveTransferSubmissionReady", {})
        response.pop("payload"); response["result"] = result
        with self.assertRaisesRegex(protocol.ProtocolError, "ResponseIdentityMismatch"):
            protocol.validate_response_for(operation, response)

    def test_strict_result_shapes_dispatch_matrix_and_official_fingerprint(self):
        ready_operation = request("ObserveTransferSubmissionReady", body("ObserveTransferSubmissionReady"))
        ready = {"kind": "Ready", **context(), "eligibleControlCount": 1,
                 "readinessFingerprint": readiness_fingerprint(context())}
        protocol.validate_response_for(ready_operation, protocol.response_for(ready_operation, ready))
        for kind in READINESS_KINDS[1:]:
            self.assertEqual({"kind": kind}, protocol.response_for(ready_operation, {"kind": kind})["result"])

        submit_operation = request("SubmitTransferOnce", body("SubmitTransferOnce"))
        kinds = ("ClickAttempted", "ReadinessMismatch", "PageNotValidated", "AccountIdentityMismatch",
                 "LoginRequired", "VerificationRequired", "BrowserUnavailable", "TimedOut", "Cancelled")
        states = ("KnownNotDispatched", "ClickDispatched", "DispatchOutcomeUnknown")
        for kind in kinds:
            for state in states:
                result = {"kind": kind, "dispatchState": state}
                if (kind, state) in VALID_SUBMIT_PAIRS:
                    self.assertEqual(result, protocol.response_for(submit_operation, result)["result"])
                else:
                    with self.assertRaises(protocol.ProtocolError): protocol.response_for(submit_operation, result)

        outcome_operation = request("ObserveTransferSubmissionOutcome", body("ObserveTransferSubmissionOutcome"))
        accepted = {"kind": "AcceptedBound", "officialRequestId": "请求-甲",
                    "exactTargetId": context()["selectedTargetExactId"], "sourceExactId": context()["sourceExactId"],
                    "destinationExactId": "destination-1",
                    "officialObservationFingerprint": "782c48017a8b9f8424b48788a746a59546076e20b7480096ab1ebf03164b0d73"}
        self.assertEqual(accepted, protocol.response_for(outcome_operation, accepted)["result"])
        self.assertEqual(accepted["officialObservationFingerprint"], official_fingerprint(
            "请求-甲", "selected-target-1", "source-1", "destination-1"))
        for kind in OUTCOME_KINDS[1:]:
            self.assertEqual({"kind": kind}, protocol.response_for(outcome_operation, {"kind": kind})["result"])
            with self.assertRaises(protocol.ProtocolError):
                protocol.response_for(outcome_operation, {"kind": kind, "officialRequestId": "private"})
        for changed in ({**accepted, "officialRequestId": ""},
                        {**accepted, "exactTargetId": "other-target"},
                        {**accepted, "sourceExactId": "other-source"},
                        {**accepted, "destinationExactId": "bad/id"},
                        {**accepted, "officialObservationFingerprint": "9" * 64}):
            with self.assertRaises(protocol.ProtocolError): protocol.response_for(outcome_operation, changed)

    def test_ready_response_must_match_every_request_binding_field_and_action_result_pair(self):
        operation = request("ObserveTransferSubmissionReady", body("ObserveTransferSubmissionReady"))
        for field, value in (("browserId", "55555555-5555-5555-5555-555555555555"), ("targetId", "target-2"),
                             ("devToolsEndpoint", "http://127.0.0.1:43128/"),
                             ("sourceUri", "https://pan.baidu.com/s/other_source?pwd=C3d4"),
                             ("baiduUserId", "uid-2"),
                             ("sourceExactId", "source-2"), ("selectedTargetExactId", "selected-target-2")):
            changed = context(**{field: value})
            result = {"kind": "Ready", **changed, "eligibleControlCount": 1,
                      "readinessFingerprint": readiness_fingerprint(changed)}
            response = request("ObserveTransferSubmissionReady", {})
            response.pop("payload"); response["result"] = result
            with self.assertRaisesRegex(protocol.ProtocolError, "ResponseIdentityMismatch"):
                protocol.validate_response_for(operation, response)
        for action, result in (("ObserveTransferSubmissionReady", {"kind": "Healthy"}),
                               ("SubmitTransferOnce", {"kind": "Ready"}),
                               ("ObserveTransferSubmissionOutcome", {"kind": "ClickAttempted",
                                                                      "dispatchState": "ClickDispatched"})):
            with self.assertRaises(protocol.ProtocolError): protocol.response_for(request(action, body(action)), result)


class TransferSubmissionDispatcherTests(unittest.TestCase):
    def test_main_dispatches_each_new_action_only_to_dedicated_handler(self):
        self.assertIn("transfer_submission_handler", __import__("inspect").signature(main.run).parameters)
        calls = []
        operations = []
        for index, action in enumerate(protocol.TRANSFER_SUBMISSION_ACTIONS, start=1):
            operation = request(action, body(action)); operation["correlationId"] = str(uuid.UUID(int=index))
            operations.append(operation)
        incoming = protocol.encode_message(request()) + b"".join(protocol.encode_message(value) for value in operations)
        output, events = io.BytesIO(), io.StringIO()
        def handler(action, payload):
            calls.append((action, payload))
            if action == "ObserveTransferSubmissionReady":
                return {"kind": "PageNotValidated"}
            if action == "SubmitTransferOnce":
                return {"kind": "ReadinessMismatch", "dispatchState": "KnownNotDispatched"}
            return {"kind": "UnboundAcknowledgement"}
        self.assertEqual(0, main.run(io.BytesIO(incoming), output, events, helper_pid=9876, parent_pid=1234,
                                     transfer_submission_handler=handler))
        self.assertEqual([(item["action"], item["payload"]) for item in operations], calls)
        self.assertEqual("", events.getvalue())


class RuntimeBoundary:
    def __init__(self):
        self.browser_id = context()["browserId"]
        self.target_id = context()["targetId"]
        self.target = self
        self.calls = []
        self.after = None

    def get_target(self, target_id, _remaining):
        self.calls.append("target")
        return self.target if target_id == self.target_id else None

    def observe_transfer_submission_ready(self, payload, remaining):
        self.calls.append("ready")
        value = {"kind": "Ready", **payload, "eligibleControlCount": 1,
                 "readinessFingerprint": readiness_fingerprint(payload)}
        if self.after: self.after()
        return value

    def submit_transfer_once(self, payload, remaining):
        self.calls.append("submit")
        value = {"kind": "ClickAttempted", "dispatchState": "ClickDispatched"}
        if self.after: self.after()
        return value

    def observe_transfer_submission_outcome(self, payload, remaining):
        self.calls.append("outcome")
        value = {"kind": "AcceptedBound", "officialRequestId": "请求-甲",
                 "exactTargetId": payload["selectedTargetExactId"], "sourceExactId": payload["sourceExactId"],
                 "destinationExactId": "destination-1",
                 "officialObservationFingerprint": official_fingerprint(
                     "请求-甲", payload["selectedTargetExactId"], payload["sourceExactId"], "destination-1")}
        if self.after: self.after()
        return value


class TransferSubmissionRuntimeTests(unittest.TestCase):
    def setUp(self):
        import transfer_submission
        self.submission = transfer_submission

    def dispatch(self, action, boundary=None, **changes):
        boundary = boundary or RuntimeBoundary()
        return self.submission.dispatch_bound(action, body(action, **changes), boundary,
                                              budget=self.submission.OperationBudget(30000, lambda: 0, lambda: False))

    def test_each_action_uses_one_exact_bound_target_call(self):
        for action, call, expected in (("ObserveTransferSubmissionReady", "ready", "Ready"),
                                       ("SubmitTransferOnce", "submit", "ClickAttempted"),
                                       ("ObserveTransferSubmissionOutcome", "outcome", "AcceptedBound")):
            boundary = RuntimeBoundary(); result = self.dispatch(action, boundary)
            self.assertEqual(expected, result["kind"])
            self.assertEqual(1, boundary.calls.count(call)); self.assertNotIn("navigate", boundary.calls)

    def test_binding_loss_before_submit_is_known_not_dispatched_and_after_click_is_unknown(self):
        boundary = RuntimeBoundary(); boundary.browser_id = "55555555-5555-5555-5555-555555555555"
        self.assertEqual({"kind": "BrowserUnavailable", "dispatchState": "KnownNotDispatched"},
                         self.dispatch("SubmitTransferOnce", boundary))
        boundary = RuntimeBoundary(); boundary.after = lambda: setattr(boundary, "target", None)
        self.assertEqual({"kind": "BrowserUnavailable", "dispatchState": "DispatchOutcomeUnknown"},
                         self.dispatch("SubmitTransferOnce", boundary))
        self.assertEqual(1, boundary.calls.count("submit"))

    def test_fixed_tab_adapters_add_only_deterministic_fingerprints(self):
        calls, isolated_calls = [], []
        def run_js(script, *args, **kwargs):
            calls.append((script, args, kwargs))
            if script == self.submission.OBSERVE_TRANSFER_SUBMISSION_READY_SCRIPT:
                return {"kind": "Ready", **context(), "eligibleControlCount": 1}
            return {"kind": "AcceptedBound", "officialRequestId": "请求-甲",
                    "exactTargetId": "selected-target-1", "sourceExactId": "source-1",
                    "destinationExactId": "destination-1"}
        def run_transfer_submit_isolated(*args, **kwargs):
            isolated_calls.append((args, kwargs))
            return {"kind": "ClickAttempted", "dispatchState": "ClickDispatched"}
        tab = SimpleNamespace(run_js=run_js, run_transfer_submit_isolated=run_transfer_submit_isolated)
        ready = self.submission.observe_readiness(tab, context(), 1)
        submit = self.submission.submit_once(tab, body("SubmitTransferOnce"), 1)
        outcome = self.submission.observe_outcome(tab, body("ObserveTransferSubmissionOutcome"), 1)
        self.assertEqual(readiness_fingerprint(context()), ready["readinessFingerprint"])
        self.assertEqual({"kind": "ClickAttempted", "dispatchState": "ClickDispatched"}, submit)
        self.assertEqual(official_fingerprint("请求-甲", "selected-target-1", "source-1", "destination-1"),
                         outcome["officialObservationFingerprint"])
        self.assertEqual(2, len(calls)); self.assertTrue(all("timeout" in item[2] for item in calls))
        self.assertEqual(1, len(isolated_calls)); self.assertIn("timeout", isolated_calls[0][1])
        self.assertNotIn(self.submission.SUBMIT_TRANSFER_ONCE_SCRIPT, [item[0] for item in calls])

    def test_drission_submit_uses_one_fresh_fixed_isolated_world_and_never_main_world_run_js(self):
        calls = []
        class Tab:
            tab_id = context()["targetId"]
            def run_js(self, *_args, **_kwargs):
                self.fail("submit must never evaluate in the page main world")
            def run_cdp(inner, command, **kwargs):
                calls.append((command, kwargs))
                if command == "Page.getFrameTree":
                    return {"frameTree": {"frame": {"id": "top-frame", "url": context()["sourceUri"]}}}
                if command == "Page.createIsolatedWorld":
                    return {"executionContextId": 71}
                return {"result": {"type": "object", "value": {
                    "kind": "ClickAttempted", "dispatchState": "ClickDispatched"}}}
        result = main._CdpTarget(Tab()).submit_transfer_once(body("SubmitTransferOnce"), 1)
        self.assertEqual({"kind": "ClickAttempted", "dispatchState": "ClickDispatched"}, result)
        self.assertEqual(["Page.getFrameTree", "Page.createIsolatedWorld", "Runtime.callFunctionOn"],
                         [item[0] for item in calls])
        self.assertEqual({"frameId": "top-frame", "grantUniveralAccess": False}, calls[1][1])
        runtime = calls[2][1]
        self.assertEqual(71, runtime["executionContextId"])
        self.assertIs(runtime["awaitPromise"], False); self.assertIs(runtime["userGesture"], True)
        self.assertEqual("function(){" + self.submission.SUBMIT_TRANSFER_ONCE_SCRIPT + "\n}",
                         runtime["functionDeclaration"])
        self.assertNotIn("worldName", calls[1][1])
        self.assertEqual(body("SubmitTransferOnce")["readinessFingerprint"],
                         runtime["arguments"][1]["value"])

    def test_isolated_world_setup_failure_is_known_not_dispatched_but_runtime_loss_is_unknown(self):
        self.assertEqual({"kind": "PageNotValidated", "dispatchState": "KnownNotDispatched"},
                         self.submission.submit_once(SimpleNamespace(), body("SubmitTransferOnce"), 1))
        wrong_calls = []
        class WrongTarget:
            tab_id = "other-target"
            def run_cdp(self, *_args, **_kwargs):
                wrong_calls.append(True); raise RuntimeError("must not create a world")
        self.assertEqual({"kind": "PageNotValidated", "dispatchState": "KnownNotDispatched"},
                         main._CdpTarget(WrongTarget()).submit_transfer_once(
                             body("SubmitTransferOnce"), 1))
        self.assertEqual([], wrong_calls)
        class SetupFails:
            tab_id = context()["targetId"]
            def run_cdp(self, *_args, **_kwargs): raise RuntimeError("private")
        self.assertEqual({"kind": "PageNotValidated", "dispatchState": "KnownNotDispatched"},
                         main._CdpTarget(SetupFails()).submit_transfer_once(
                             body("SubmitTransferOnce"), 1))
        class RuntimeFails:
            tab_id = context()["targetId"]
            def run_cdp(self, command, **_kwargs):
                if command == "Page.getFrameTree":
                    return {"frameTree": {"frame": {"id": "frame", "url": context()["sourceUri"]}}}
                if command == "Page.createIsolatedWorld": return {"executionContextId": 1}
                raise RuntimeError("private")
        self.assertEqual({"kind": "BrowserUnavailable", "dispatchState": "DispatchOutcomeUnknown"},
                         main._CdpTarget(RuntimeFails()).submit_transfer_once(
                             body("SubmitTransferOnce"), 1))


class TransferSubmissionDomTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if shutil.which("node") is None:
            raise unittest.SkipTest("Node.js is required for the synthetic VM harness")

    def run_harness(self, mode):
        import transfer_submission
        value = {"mode": mode, "context": context(),
                 "readinessFingerprint": readiness_fingerprint(context()),
                 "readinessScript": transfer_submission.OBSERVE_TRANSFER_SUBMISSION_READY_SCRIPT,
                 "submitScript": transfer_submission.SUBMIT_TRANSFER_ONCE_SCRIPT,
                 "outcomeScript": transfer_submission.OBSERVE_TRANSFER_SUBMISSION_OUTCOME_SCRIPT}
        completed = subprocess.run(["node", str(Path(__file__).with_name("transfer_submission_harness.cjs"))],
                                   input=json.dumps(value, ensure_ascii=False), text=True,
                                   capture_output=True, timeout=15, check=False)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("assertions passed", completed.stdout)

    def test_readiness_synthetic_dom_is_exact_and_fails_closed(self): self.run_harness("readiness")
    def test_submit_reproves_synchronously_and_clicks_at_most_once(self): self.run_harness("submit")
    def test_outcome_is_read_only_and_accepts_only_official_exact_mapping(self): self.run_harness("outcome")


if __name__ == "__main__":
    unittest.main()
