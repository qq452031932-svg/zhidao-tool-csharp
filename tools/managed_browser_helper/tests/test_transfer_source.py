"""Synthetic-only source-opening contracts. No browser or network is used."""

import importlib.util
import inspect
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import unittest
import uuid
from unittest.mock import patch
from types import SimpleNamespace

HELPER_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HELPER_ROOT))
import main
import protocol
from test_protocol import request

SOURCE = "https://pan.baidu.com/s/Ab_-09?pwd=A1b2"
KINDS = ("SourceOpened", "ExtractionRequired", "LoginRequired", "VerificationRequired",
         "PageNotValidated", "BrowserUnavailable", "AccountIdentityMismatch", "TimedOut", "Cancelled")


def payload(**changes):
    return {"browserId": "44444444-4444-4444-4444-444444444444", "targetId": "target-1",
            "devToolsEndpoint": "http://127.0.0.1:43127/", "timeoutMilliseconds": 5000,
            "sourceUri": SOURCE, "expectedBaiduUserId": "expected-private-uid", **changes}


class TransferSourceProtocolTests(unittest.TestCase):
    def validate(self, body):
        try:
            protocol.validate_request(request("OpenTransferSource", body))
            return "Accepted"
        except protocol.ProtocolError as exc:
            return exc.code

    def test_fixed_capability_order_and_canonical_request(self):
        self.assertEqual(("Health", "Shutdown", "ObserveAccountIdentity", "OpenTransferSource",
                          "ReadTransferSource", "ReadTransferDirectory", "OpenTransferPicker",
                          "OpenTransferDirectoryRoot", "EnterTransferDirectory",
                          "BeginCreateTransferDirectory", "ConfirmCreateTransferDirectory",
                          "CancelTransferPicker", "ObserveTransferPickerClosed",
                          "OpenTransferTargetRoot", "EnterTransferTargetDirectory",
                          "ReadTransferTargetDirectory", "OpenTransferSourceAfterTargetRead",
                          "ObserveTransferSubmissionReady", "SubmitTransferOnce",
                          "ObserveTransferSubmissionOutcome", "ReadShareOfficialListing",
                          "SubmitShareOnce", "ObserveShareSubmissionOutcome", "SubmitExtractionCode"),
                         protocol.REQUIRED_CAPABILITIES)
        self.assertEqual("Accepted", self.validate(payload()))
        for uri in (SOURCE, "https://pan.baidu.com/s/a", "https://pan.baidu.com/s/" + "a" * 128):
            self.assertEqual("Accepted", self.validate(payload(sourceUri=uri)))

    def test_uri_allowlist_is_ascii_fullmatch_without_normalization(self):
        invalid = [None, [], 1, "", " " + SOURCE, SOURCE + "\n", SOURCE + "#", SOURCE + "?",
                   SOURCE + "&x=1", SOURCE + "&pwd=A1b2", SOURCE.replace("https", "http", 1),
                   SOURCE.replace("https", "HTTPS", 1), SOURCE.replace("pan", "PAN", 1),
                   SOURCE.replace("pan.baidu.com", "user@pan.baidu.com"),
                   SOURCE.replace("pan.baidu.com", "pan.baidu.com:443"),
                   SOURCE.replace("pan.baidu.com", "sub.pan.baidu.com"),
                   SOURCE.replace("pan.baidu.com", "pan.baidu.com.evil.test"),
                   SOURCE.replace("/s/", "\\s\\"), SOURCE.replace("Ab_-09", "%41b_-09"),
                   SOURCE.replace("Ab_-09", "中"), SOURCE.replace("Ab_-09", "a" * 129),
                   "https://pan.baidu.com/s/", "https://pan.baidu.com/s/a/", "https://pan.baidu.com/s/../a",
                   "https://pan.baidu.com/s/a?pwd=１２３４", "https://pan.baidu.com/s/a?pwd=A_12",
                   "https://pan.baidu.com/s/a?pwd=abc", "https://pan.baidu.com/s/a?pwd=abcde",
                   "https://pan.baidu.com/s/a?PWD=abcd", "https://pan.baidu.com/s/a?pwd=ab%31"]
        for uri in invalid:
            with self.subTest(uri=repr(uri)):
                self.assertEqual("InvalidPayload", self.validate(payload(sourceUri=uri)))

    def test_identity_bounded_opaque_and_original_binding_validation(self):
        for uid in ("x", "😀" * 64, "a" * 256):
            self.assertEqual("Accepted", self.validate(payload(expectedBaiduUserId=uid)))
        for uid in (None, 12, True, [], "", " x", "x ", "x\n", "x\x7f", "😀" * 65, "\ud800"):
            self.assertEqual("InvalidPayload", self.validate(payload(expectedBaiduUserId=uid)))
        for field, value in (("browserId", "wrong"), ("targetId", ""),
                             ("devToolsEndpoint", "http://localhost:43127/"),
                             ("timeoutMilliseconds", True), ("timeoutMilliseconds", 120001)):
            self.assertEqual("InvalidPayload", self.validate(payload(**{field: value})))

    def test_exact_fields_forbidden_keys_and_no_data_results(self):
        self.assertEqual("Accepted", self.validate(payload()))
        for key in payload():
            body = payload()
            body.pop(key)
            self.assertEqual("InvalidPayload", self.validate(body))
        for key in ("stableBaiduUserId",):
            self.assertEqual("UnknownField", self.validate(payload(**{key: "private"})))
        for key in ("cookie", "authorization", "extractionCode", "pageUrl", "pageText", "html",
                    "url", "script", "selector"):
            self.assertEqual("ForbiddenField", self.validate(payload(**{key: "private"})))
        for kind in KINDS:
            response = protocol.response_for(request("OpenTransferSource", payload()), {"kind": kind})
            self.assertEqual({"kind": kind}, response["result"])
            for key in ("sourceUri", "expectedBaiduUserId", "inventory", "exactId"):
                with self.assertRaises(protocol.ProtocolError):
                    protocol.response_for(request("OpenTransferSource", payload()), {"kind": kind, key: "private"})
        for result in ({}, None, [], {"kind": []}, {"kind": "Observed"}, {"kind": "Unknown"}):
            with self.assertRaises(protocol.ProtocolError):
                protocol.response_for(request("OpenTransferSource", payload()), result)

    def test_request_summary_never_exports_source_or_identity(self):
        self.assertEqual("Accepted", self.validate(payload()))
        summary = protocol.safe_summary(request("OpenTransferSource", payload()))
        for value in (SOURCE, "A1b2", "expected-private-uid", "target-1", payload()["browserId"]):
            self.assertNotIn(value, summary)

    def test_shared_csharp_wire_fixture_is_byte_identical_and_all_results_roundtrip(self):
        fixture_path = Path(__file__).with_name("fixtures") / "open_transfer_source.json"
        self.assertTrue(fixture_path.is_file(), "shared cross-language fixture must exist")
        fixture = fixture_path.read_bytes().strip()
        operation = json.loads(fixture)
        self.assertEqual(fixture, protocol.encode_message(operation)[4:])
        self.assertEqual(operation, protocol.read_message(io.BytesIO(protocol.encode_message(operation))))
        for kind in KINDS:
            response = protocol.response_for(operation, {"kind": kind})
            decoded = protocol.read_message(io.BytesIO(protocol.encode_message(response, response=True)), response=True)
            self.assertEqual({"kind": kind}, decoded["result"])
            protocol.validate_response_for(operation, decoded)

    def test_capability_negotiation_rejects_missing_duplicate_reordered_and_extra_actions(self):
        for capabilities in (["Health", "Shutdown", "ObserveAccountIdentity"],
                             ["Health", "Shutdown", "OpenTransferSource", "ObserveAccountIdentity"],
                             list(protocol.REQUIRED_CAPABILITIES) + ["OpenTransferSource"],
                             list(protocol.REQUIRED_CAPABILITIES) + ["ExecuteScript"]):
            operation = request()
            operation["payload"]["requiredCapabilities"] = capabilities
            with self.assertRaisesRegex(protocol.ProtocolError, "CapabilityMismatch"):
                protocol.validate_request(operation)

    def test_dispatch_has_separate_injected_opener_and_safe_failures(self):
        self.assertIn("source_opener", inspect.signature(main.run).parameters)
        for kind in KINDS:
            calls = []
            operation = {**request("OpenTransferSource", payload()), "correlationId": str(uuid.uuid4())}
            incoming = protocol.encode_message(request()) + protocol.encode_message(operation)
            out, events = io.BytesIO(), io.StringIO()
            def opener(body):
                calls.append(body)
                return {"kind": kind}
            code = main.run(io.BytesIO(incoming), out, events, helper_pid=9876, parent_pid=1234,
                            source_opener=opener, observer=lambda _: self.fail("wrong dispatcher"))
            self.assertEqual(0, code)
            self.assertEqual([payload()], calls)
            self.assertEqual("", events.getvalue())
            out.seek(0)
            protocol.read_message(out, response=True)
            self.assertEqual({"kind": kind}, protocol.read_message(out, response=True)["result"])
            self.assertNotIn(b"expected-private-uid", out.getvalue())
            self.assertNotIn(b"A1b2", out.getvalue())
        def raising(_):
            raise RuntimeError(SOURCE + " private-cookie")
        out, events = io.BytesIO(), io.StringIO()
        code = main.run(io.BytesIO(incoming), out, events, helper_pid=9876, parent_pid=1234,
                        source_opener=raising)
        self.assertEqual(3, code)
        self.assertEqual("HELPER_RUNTIME_ERROR\n", events.getvalue())
        self.assertNotIn(b"private", out.getvalue())


class Boundary:
    """A stateful fake of the existing connector/target boundary, not the orchestrator."""
    def __init__(self):
        self.now = 0.0
        self.cancelled = False
        self.browser = payload()["browserId"]
        self.id = "target-1"
        self.url = main.PAN_ROOT
        self.target = self
        self.identity = {"kind": "Observed", "stableBaiduUserId": payload()["expectedBaiduUserId"]}
        self.stage = {"kind": "SourceOpened"}
        self.calls = []
        self.hook = lambda _: None
        self.navigations = []

    def called(self, name, budget=None):
        if budget is not None:
            assert 0 < budget <= 5
        self.calls.append(name)
        self.hook(name)

    @property
    def browser_id(self):
        self.called("browser")
        return self.browser

    @property
    def target_id(self):
        self.called("target-id")
        return self.id

    def get_target(self, target_id, remaining):
        assert target_id == "target-1"
        self.called("get-target", remaining)
        return self.target

    def current_url(self, remaining):
        self.called("url", remaining)
        return self.url

    def observe_identity(self, remaining):
        self.called("identity", remaining)
        return self.identity

    def navigate(self, uri, remaining):
        self.navigations.append(uri)
        self.url = uri
        self.called("navigate", remaining)

    def observe_source_stage(self, remaining):
        self.called("stage", remaining)
        return self.stage


class TransferSourceOrchestratorTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("transfer_source"), "fixed source-opening module missing")
        import transfer_source
        self.source = transfer_source
        self.boundary = Boundary()

    def open(self, **changes):
        b = self.boundary
        return self.source.open_bound_source(payload(**changes), b, classify_route=main.classify_route,
                                             monotonic=lambda: b.now, is_cancelled=lambda: b.cancelled)

    def test_matching_identity_navigates_once_then_returns_only_page_hint(self):
        for route in (main.PAN_ROOT, "https://pan.baidu.com/disk/main#/index?category=all"):
            self.boundary = Boundary()
            self.boundary.url = route
            self.assertEqual({"kind": "SourceOpened"}, self.open())
            self.assertEqual([SOURCE], self.boundary.navigations)
            self.assertEqual(1, self.boundary.calls.count("identity"))
            self.assertEqual(1, self.boundary.calls.count("stage"))

    def test_initial_route_precondition_never_reloads_or_queries_shares(self):
        for url, kind in ((main.PASSPORT_ROOT, "LoginRequired"), (SOURCE, "PageNotValidated"),
                          ("https://foreign.test/", "PageNotValidated"), ("", "PageNotValidated")):
            self.boundary = Boundary()
            self.boundary.url = url
            self.assertEqual({"kind": kind}, self.open())
            self.assertEqual([], self.boundary.navigations)
            self.assertNotIn("identity", self.boundary.calls)

    def test_mismatched_or_malformed_identity_can_never_navigate(self):
        cases = [(None, "BrowserUnavailable"), ({}, "BrowserUnavailable"), ([], "BrowserUnavailable"),
                 ({"kind": "Observed"}, "BrowserUnavailable"), ({"kind": []}, "BrowserUnavailable"),
                 ({"kind": "Observed", "stableBaiduUserId": "other"}, "AccountIdentityMismatch")]
        for uid in (None, 42, True, "", " x", "x\n", "x" * 257, "\ud800"):
            cases.append(({"kind": "Observed", "stableBaiduUserId": uid}, "BrowserUnavailable"))
        for kind in ("LoginRequired", "BrowserUnavailable", "TimedOut", "Cancelled"):
            cases.append(({"kind": kind}, kind))
            cases.append(({"kind": kind, "private": "secret"}, "BrowserUnavailable"))
        cases.append(({**self.boundary.identity, "private": "secret"}, "BrowserUnavailable"))
        for identity, expected in cases:
            with self.subTest(identity=repr(identity)):
                self.boundary = Boundary()
                self.boundary.identity = identity
                self.assertEqual({"kind": expected}, self.open())
                self.assertEqual([], self.boundary.navigations)
                self.assertNotIn("stage", self.boundary.calls)

    def test_wrong_initial_binding_is_unavailable_without_navigation(self):
        for field, value in (("browser", "wrong"), ("id", "wrong"), ("target", None)):
            self.boundary = Boundary()
            setattr(self.boundary, field, value)
            self.assertEqual({"kind": "BrowserUnavailable"}, self.open())
            self.assertEqual([], self.boundary.navigations)

    def test_binding_changes_after_every_operation_are_rejected(self):
        for operation in ("url", "identity", "navigate", "stage"):
            for field, value in (("browser", "wrong"), ("id", "wrong"), ("target", None), ("target", object())):
                with self.subTest(operation=operation, field=field):
                    self.boundary = Boundary()
                    self.boundary.hook = lambda name: setattr(self.boundary, field, value) if name == operation else None
                    self.assertEqual({"kind": "BrowserUnavailable"}, self.open())
                    if operation in ("url", "identity"):
                        self.assertEqual([], self.boundary.navigations)
                    if operation == "navigate":
                        self.assertNotIn("stage", self.boundary.calls)

    def test_identity_navigation_and_stage_never_run_with_changed_pre_call_binding(self):
        self.assertEqual({"kind": "SourceOpened"}, self.open())
        baseline = list(self.boundary.calls)
        for operation in ("identity", "navigate", "stage"):
            operation_index = baseline.index(operation)
            binding_index = max(i for i in range(operation_index) if baseline[i] == "get-target")
            for field, value in (("browser", "wrong"), ("id", "wrong"), ("target", None), ("target", object())):
                with self.subTest(operation=operation, field=field):
                    self.boundary = Boundary()
                    def hook(_name):
                        if len(self.boundary.calls) == binding_index + 1:
                            setattr(self.boundary, field, value)
                    self.boundary.hook = hook
                    self.assertEqual({"kind": "BrowserUnavailable"}, self.open())
                    self.assertNotIn(operation, self.boundary.calls)
                    if operation in ("identity", "navigate"):
                        self.assertEqual([], self.boundary.navigations)

    def test_identity_url_must_stay_exact_and_only_original_share_query_or_removed_is_allowed(self):
        for changed in (main.PASSPORT_ROOT, "https://pan.baidu.com/disk/main#/index?category=all", SOURCE):
            self.boundary = Boundary()
            self.boundary.hook = lambda name: setattr(self.boundary, "url", changed) if name == "identity" else None
            self.assertEqual({"kind": "BrowserUnavailable"}, self.open())
            self.assertEqual([], self.boundary.navigations)
        for changed, kind in ((SOURCE, "SourceOpened"), (SOURCE.split("?")[0], "SourceOpened"),
                              (main.PASSPORT_ROOT, "LoginRequired"), (main.PAN_ROOT, "PageNotValidated"),
                              (SOURCE.replace("Ab_-09", "other"), "PageNotValidated"),
                              (SOURCE.replace("A1b2", "abcd"), "PageNotValidated"),
                              (SOURCE + "#x", "PageNotValidated"),
                              (SOURCE + "&extra=1", "PageNotValidated")):
            self.boundary = Boundary()
            self.boundary.hook = lambda name: setattr(self.boundary, "url", changed) if name == "navigate" else None
            self.assertEqual({"kind": kind}, self.open())
            if kind != "SourceOpened":
                self.assertNotIn("stage", self.boundary.calls)

    def test_stage_change_of_exact_url_discards_hint_even_if_same_share_path(self):
        for changed in (SOURCE.split("?")[0], main.PASSPORT_ROOT, main.PAN_ROOT):
            self.boundary = Boundary()
            self.boundary.hook = lambda name: setattr(self.boundary, "url", changed) if name == "stage" else None
            self.assertEqual({"kind": "PageNotValidated"}, self.open())

    def test_stage_result_is_finite_kind_only_and_does_not_accept_identity_or_inventory(self):
        for kind in ("SourceOpened", "ExtractionRequired", "LoginRequired", "VerificationRequired", "PageNotValidated"):
            self.boundary = Boundary()
            self.boundary.stage = {"kind": kind}
            self.assertEqual({"kind": kind}, self.open())
        for result in (None, {}, [], {"kind": []}, {"kind": "Observed"}, {"kind": "Unknown"},
                       {"kind": "SourceOpened", "inventory": ["secret"]}, {"kind": "AccountIdentityMismatch"}):
            self.boundary = Boundary()
            self.boundary.stage = result
            self.assertEqual({"kind": "PageNotValidated"}, self.open())

    def test_every_boundary_call_checks_timeout_and_cancel_even_when_call_raises(self):
        self.assertEqual({"kind": "SourceOpened"}, self.open())
        normal_calls = list(self.boundary.calls)
        for index, operation in enumerate(normal_calls):
            for condition in ("timeout", "cancel"):
                for raises in (False, True):
                    with self.subTest(index=index, operation=operation, condition=condition, raises=raises):
                        self.boundary = Boundary()
                        def hook(_name):
                            if len(self.boundary.calls) == index + 1:
                                if condition == "timeout":
                                    self.boundary.now = 6.0
                                else:
                                    self.boundary.cancelled = True
                                if raises:
                                    raise RuntimeError("private-cookie " + SOURCE)
                        self.boundary.hook = hook
                        self.assertEqual({"kind": "TimedOut" if condition == "timeout" else "Cancelled"}, self.open())
                        self.assertEqual(index + 1, len(self.boundary.calls))

    def test_unexpired_exceptions_redacted_and_never_retry(self):
        for operation in ("browser", "get-target", "target-id", "url", "identity", "navigate", "stage"):
            self.boundary = Boundary()
            def hook(name):
                if name == operation:
                    raise TimeoutError("private-cookie " + SOURCE)
            self.boundary.hook = hook
            self.assertEqual({"kind": "BrowserUnavailable"}, self.open())
            self.assertLessEqual(len(self.boundary.navigations), 1)
        self.boundary = Boundary()
        self.boundary.cancelled = True
        self.assertEqual({"kind": "Cancelled"}, self.open())
        self.assertEqual([], self.boundary.calls)

    def test_invalid_direct_payload_is_rejected_before_boundary_access(self):
        for changes in ({"sourceUri": "https://foreign.test/"}, {"expectedBaiduUserId": ""},
                        {"script": "private"}, {"devToolsEndpoint": "http://foreign.test/"}):
            self.boundary = Boundary()
            self.assertEqual({"kind": "BrowserUnavailable"}, self.open(**changes))
            self.assertEqual([], self.boundary.calls)


class TransferSourceScriptTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("transfer_source"), "fixed source-opening module missing")
        import transfer_source
        self.source = transfer_source

    def test_actual_builtin_script_offline_vm(self):
        node = shutil.which("node") or (r"D:\node.js\node.exe" if Path(r"D:\node.js\node.exe").is_file() else None)
        self.assertIsNotNone(node, "Node required for offline verification; no skip")
        result = subprocess.run([node, str(Path(__file__).with_name("transfer_source_harness.cjs"))],
                                input=json.dumps({"script": self.source.SOURCE_STAGE_SCRIPT}),
                                text=True, capture_output=True, timeout=20, check=False)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("offline source script assertions passed", result.stdout)

    def test_drission_target_runs_fixed_script_with_only_budget_no_input_or_click(self):
        calls = []
        class Tab:
            def run_js(self, script, *args, **kwargs):
                calls.append((script, args, kwargs))
                return {"kind": "ExtractionRequired"}
        self.assertEqual({"kind": "ExtractionRequired"}, main._DrissionTarget(Tab()).observe_source_stage(0.25))
        script, args, kwargs = calls[0]
        self.assertEqual(self.source.SOURCE_STAGE_SCRIPT, script)
        self.assertEqual(1, len(args))
        self.assertTrue(0 < args[0] <= 250)
        self.assertEqual({"timeout": 0.25}, kwargs)

    def test_wrapper_rejects_invalid_budget_and_redacts_exceptions_and_extra_data(self):
        for remaining in (0, -1, float("nan"), float("inf"), True, "1"):
            class NeverTab:
                def run_js(inner, *_args, **_kwargs):
                    self.fail("invalid budget must not execute script")
            self.assertIn(self.source.observe_source_stage(NeverTab(), remaining),
                          ({"kind": "TimedOut"}, {"kind": "BrowserUnavailable"}))
        for value in (None, [], {"kind": "SourceOpened", "html": "private"}):
            class Tab:
                def run_js(inner, *_args, **_kwargs):
                    return value
            self.assertEqual({"kind": "PageNotValidated"}, self.source.observe_source_stage(Tab(), 1))
        class ThrowingTab:
            def run_js(inner, *_args, **_kwargs):
                raise TimeoutError("private-page-data")
        self.assertEqual({"kind": "BrowserUnavailable"}, self.source.observe_source_stage(ThrowingTab(), 1))


class TransferSourceAttachTests(unittest.TestCase):
    def test_connector_factory_consumes_same_deadline_and_cancellation_wins_exceptions(self):
        import transfer_source
        for condition in ("timeout", "cancel"):
            for raises in (False, True):
                boundary = Boundary()
                def attach(budget):
                    budget.remaining()
                    if condition == "timeout":
                        boundary.now = 6
                    else:
                        boundary.cancelled = True
                    if raises:
                        raise RuntimeError("private-source " + SOURCE)
                    return boundary
                result = transfer_source.open_with_connector(payload(), attach,
                    classify_route=main.classify_route, monotonic=lambda: boundary.now,
                    is_cancelled=lambda: boundary.cancelled)
                self.assertEqual({"kind": "TimedOut" if condition == "timeout" else "Cancelled"}, result)
                self.assertEqual([], boundary.calls)
        def forbidden_attach(_budget):
            self.fail("cancelled/invalid request may not attach")
        self.assertEqual({"kind": "Cancelled"}, transfer_source.open_with_connector(
            payload(), forbidden_attach, classify_route=main.classify_route, is_cancelled=lambda: True))
        self.assertEqual({"kind": "BrowserUnavailable"}, transfer_source.open_with_connector(
            payload(sourceUri="https://foreign.test/"), forbidden_attach, classify_route=main.classify_route))

    def test_attachment_budget_is_not_reset_before_navigation(self):
        import transfer_source
        boundary = Boundary()
        def attach(_budget):
            boundary.now = 4.5
            return boundary
        def hook(name):
            if name == "identity":
                boundary.now = 5
        boundary.hook = hook
        result = transfer_source.open_with_connector(payload(), attach, classify_route=main.classify_route,
            monotonic=lambda: boundary.now, is_cancelled=lambda: boundary.cancelled)
        self.assertEqual({"kind": "TimedOut"}, result)
        self.assertEqual([], boundary.navigations)

    def test_existing_only_exact_endpoint_and_target_flow_using_fake_drission_module(self):
        self.assertTrue(hasattr(main, "open_transfer_source"), "production attach wrapper missing")
        calls = []
        class Options:
            def set_address(inner, address):
                calls.append(("address", address))
                return inner
            def existing_only(inner, value):
                calls.append(("existing-only", value))
                return inner
        class Tab:
            tab_id = "target-1"
            url = main.PAN_ROOT
            def get(inner, uri, **kwargs):
                calls.append(("get", uri, kwargs))
                inner.url = uri
            def run_js(inner, script, *_args, **_kwargs):
                if script == main.identity_query.IDENTITY_QUERY_SCRIPT:
                    return {"kind": "QueryResponse", "status": 200, "body": '{"errno":0,"result":{"uk":42}}'}
                return {"kind": "SourceOpened"}
        tab = Tab()
        class Browser:
            id = payload()["browserId"]
            tab_ids = ["target-1"]
            def __init__(inner, options):
                self.assertIsInstance(options, Options)
            def get_tab(inner, target_id):
                calls.append(("get-tab", target_id))
                return tab
        with patch.dict(sys.modules, {"DrissionPage": SimpleNamespace(Chromium=Browser, ChromiumOptions=Options)}):
            self.assertEqual({"kind": "SourceOpened"}, main.open_transfer_source(payload(expectedBaiduUserId="42")))
        self.assertEqual(("address", "127.0.0.1:43127"), calls[0])
        self.assertEqual(("existing-only", True), calls[1])
        self.assertEqual([("get-tab", "target-1")], [c for c in calls if c[0] == "get-tab"])
        navigations = [c for c in calls if c[0] == "get"]
        self.assertEqual(1, len(navigations))
        self.assertEqual(SOURCE, navigations[0][1])
        self.assertEqual(0, navigations[0][2]["retry"])
        self.assertEqual(0, navigations[0][2]["interval"])


if __name__ == "__main__":
    unittest.main()
