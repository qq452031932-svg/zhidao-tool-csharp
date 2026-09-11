"""Offline-only source listing contracts: synthetic stateful connector and Node VM."""

import copy
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
from types import SimpleNamespace
from unittest.mock import patch

HELPER_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HELPER_ROOT))
import main
import protocol
from test_protocol import request

FIXTURE = json.loads((Path(__file__).with_name("fixtures") / "read_transfer_source.json").read_text(encoding="utf-8"))
DENIALS = ("ExtractionRequired", "LoginRequired", "VerificationRequired", "PageNotValidated",
           "BrowserUnavailable", "AccountIdentityMismatch", "TimedOut", "Cancelled")


def payload(**changes):
    return {**FIXTURE["request"]["payload"], "timeoutMilliseconds": 5000, **changes}


def observed(**changes):
    return {**copy.deepcopy(FIXTURE["result"]), **changes}


def raw(**changes):
    result = observed(**changes)
    result.pop("observationFingerprint")
    return result


class ListingProtocolTests(unittest.TestCase):
    def setUp(self):
        self.assertIn("ReadTransferSource", protocol.ALLOWED_ACTIONS, "source listing action missing")

    def test_capability_and_request_reuse_exact_source_validation(self):
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
        protocol.validate_request(FIXTURE["request"])
        for field in payload():
            body = payload()
            del body[field]
            with self.assertRaises(protocol.ProtocolError):
                protocol.validate_request(request("ReadTransferSource", body))
        for field, value in (("sourceUri", "https://foreign.test/private"), ("expectedBaiduUserId", "\ud800"),
                             ("browserId", "wrong"), ("targetId", ""), ("timeoutMilliseconds", True),
                             ("timeoutMilliseconds", 120001), ("devToolsEndpoint", "http://localhost:9222/"),
                             ("script", "private"), ("selector", "private")):
            with self.assertRaises(protocol.ProtocolError):
                protocol.validate_request(request("ReadTransferSource", payload(**{field: value})))

    def test_fixture_and_all_results_roundtrip(self):
        operation = FIXTURE["request"]
        self.assertEqual(operation, protocol.read_message(io.BytesIO(protocol.encode_message(operation))))
        for result in [observed()] + [{"kind": kind} for kind in DENIALS]:
            response = protocol.response_for(operation, result)
            decoded = protocol.read_message(io.BytesIO(protocol.encode_message(response, response=True)), response=True)
            self.assertEqual(response, decoded)
            protocol.validate_response_for(operation, decoded)

    def test_duplicate_fields_and_capability_drift_are_rejected(self):
        import struct
        for capabilities in (list(protocol.REQUIRED_CAPABILITIES[:-1]),
                             list(reversed(protocol.REQUIRED_CAPABILITIES)),
                             list(protocol.REQUIRED_CAPABILITIES) + ["ReadTransferSource"]):
            handshake = request()
            handshake["payload"]["requiredCapabilities"] = capabilities
            with self.assertRaisesRegex(protocol.ProtocolError, "CapabilityMismatch"):
                protocol.validate_request(handshake)
        response = protocol.response_for(FIXTURE["request"], observed())
        encoded = protocol.encode_message(response, response=True)[4:]
        for original, replacement in ((b'"kind":"Observed"', b'"kind":"Observed","kind":"Observed"'),
                                      (b'"exactId":"101"', b'"exactId":"101","exactId":"101"')):
            duplicate = encoded.replace(original, replacement, 1)
            with self.assertRaisesRegex(protocol.ProtocolError, "DuplicateField"):
                protocol.read_message(io.BytesIO(struct.pack(">I", len(duplicate)) + duplicate), response=True)

    def test_observed_shape_types_bounds_and_uniqueness(self):
        def invalid(value):
            with self.assertRaises(protocol.ProtocolError):
                protocol.response_for(FIXTURE["request"], value)
        for key in observed():
            result = observed()
            result.pop(key)
            invalid(result)
        for field, value in (("kind", "Unknown"), ("kind", []), ("baiduUserId", ""),
                             ("generationToken", ""), ("totalCount", True), ("totalCount", 2.0),
                             ("totalCount", 3), ("reachedEnd", False), ("isStable", 1),
                             ("observationFingerprint", "A" * 64), ("observationFingerprint", "a" * 63),
                             ("observationFingerprint", "x" * 64), ("items", []), ("items", None),
                             ("unknown", "secret")):
            invalid(observed(**{field: value}))
        for field in ("baiduUserId", "generationToken"):
            for value in (None, True, " x", "x\x7f", "x\x85", "\ud800", "😀" * 65):
                invalid(observed(**{field: value}))
            protocol.response_for(FIXTURE["request"], observed(**{field: "😀" * 64}))
        for field, values in (("exactId", ["", "中", "a.b", "a" * 129, "a\n", True]),
                              ("kind", ["file", "Unknown", 1]),
                              ("serverName", ["", "x\n", "\ud800", "😀" * 65, None])):
            for value in values:
                item = {**observed()["items"][0], field: value}
                invalid(observed(items=[item], totalCount=1))
        item = observed()["items"][0]
        invalid(observed(items=[item, item]))
        invalid(observed(items=[{**item, "url": "private"}], totalCount=1))
        for count in (1, 32):
            items = [{"exactId": f"id-{i:03}", "kind": "File", "serverName": "😀" * 64} for i in range(count)]
            protocol.response_for(FIXTURE["request"], observed(items=items, totalCount=count))
        invalid(observed(items=[{**item, "exactId": str(i)} for i in range(33)], totalCount=33))
        protocol.response_for(FIXTURE["request"], observed(items=[{**item, "serverName": " name "}], totalCount=1))

    def test_valid_unsorted_wire_items_are_accepted_without_losing_evidence(self):
        result = observed(items=list(reversed(observed()["items"])))
        try:
            response = protocol.response_for(FIXTURE["request"], result)
        except protocol.ProtocolError:
            self.fail("valid unique wire items should not depend on incoming ordering")
        decoded = protocol.read_message(io.BytesIO(protocol.encode_message(response, response=True)), response=True)
        self.assertEqual(result, decoded["result"])

    def test_denials_are_kind_only_and_errors_and_summaries_redact(self):
        for kind in DENIALS:
            for field in ("items", "sourceUri", "baiduUserId", "serverName"):
                with self.assertRaises(protocol.ProtocolError) as caught:
                    protocol.response_for(FIXTURE["request"], {"kind": kind, field: "private-secret"})
                self.assertNotIn("private-secret", str(caught.exception))
        response = protocol.response_for(FIXTURE["request"], observed())
        for message in (FIXTURE["request"], response):
            summary = protocol.safe_summary(message)
            for secret in (payload()["sourceUri"], payload()["expectedBaiduUserId"], "测试文件夹", "应用.apk", "generation-1"):
                self.assertNotIn(secret, summary)

    def test_separate_dispatch_and_fixed_error_events(self):
        self.assertIn("source_reader", inspect.signature(main.run).parameters)
        for result in [observed()] + [{"kind": kind} for kind in DENIALS]:
            operation = {**FIXTURE["request"], "correlationId": str(uuid.uuid4())}
            incoming = protocol.encode_message(request()) + protocol.encode_message(operation)
            out, events, calls = io.BytesIO(), io.StringIO(), []
            def reader(body):
                calls.append(body)
                return result
            code = main.run(io.BytesIO(incoming), out, events, helper_pid=9876, parent_pid=1234,
                            source_reader=reader, source_opener=lambda _: self.fail("opened"),
                            observer=lambda _: self.fail("identity query"))
            self.assertEqual(0, code)
            self.assertEqual([operation["payload"]], calls)
            self.assertEqual("", events.getvalue())
            out.seek(0)
            protocol.read_message(out, response=True)
            self.assertEqual(result, protocol.read_message(out, response=True)["result"])
        def raising(_):
            raise RuntimeError("private-uid private-name https://pan.baidu.com/s/secret")
        out, events = io.BytesIO(), io.StringIO()
        code = main.run(io.BytesIO(incoming), out, events, helper_pid=9876, parent_pid=1234, source_reader=raising)
        self.assertEqual(3, code)
        self.assertEqual("HELPER_RUNTIME_ERROR\n", events.getvalue())
        self.assertNotIn(b"private", out.getvalue())


class Boundary:
    def __init__(self):
        self.now, self.cancelled = 0.0, False
        self.browser, self.id = payload()["browserId"], payload()["targetId"]
        self.url, self.target = payload()["sourceUri"], self
        self.stage, self.listing = {"kind": "SourceOpened"}, raw()
        self.calls, self.hook = [], lambda _: None

    def called(self, name, remaining=None):
        if remaining is not None:
            assert 0 < remaining <= 5
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
        assert target_id == payload()["targetId"]
        self.called("target", remaining)
        return self.target

    def current_url(self, remaining):
        self.called("url", remaining)
        return self.url

    def observe_source_stage(self, remaining):
        self.called("stage", remaining)
        return self.stage

    def observe_source_listing(self, expected_uid, expected_source_url, remaining):
        assert expected_uid == payload()["expectedBaiduUserId"]
        assert expected_source_url == self.url
        self.called("listing", remaining)
        return self.listing

    def navigate(self, *_):
        raise AssertionError("navigation forbidden")

    def observe_identity(self, *_):
        raise AssertionError("identity query forbidden on share")


class ListingRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("transfer_source_listing"), "fixed source listing module missing")
        import transfer_source_listing
        self.source = transfer_source_listing
        self.boundary = Boundary()

    def read(self, **changes):
        b = self.boundary
        return self.source.read_bound_source(payload(**changes), b, classify_route=main.classify_route,
                                             monotonic=lambda: b.now, is_cancelled=lambda: b.cancelled)

    def test_computed_fixture_fingerprint_and_readonly_result(self):
        self.assertEqual(FIXTURE["result"]["observationFingerprint"],
                         self.source.compute_source_fingerprint(payload()["sourceUri"], raw()))
        self.assertEqual(observed(), self.read())
        self.assertEqual(1, self.boundary.calls.count("listing"))
        self.assertNotIn("identity", self.boundary.calls)
        self.boundary.url = payload()["sourceUri"].split("?")[0]
        self.assertEqual(observed(), self.read())
        for changed in ("sourceUri", "baiduUserId", "generationToken", "serverName", "kind", "exactId"):
            result, uri = raw(), payload()["sourceUri"]
            if changed == "sourceUri":
                uri = uri.split("?")[0]
            elif changed == "kind":
                result["items"][0][changed] = "File"
            elif changed in result:
                result[changed] += "-changed"
            else:
                result["items"][0][changed] += "-changed"
            self.assertNotEqual(observed()["observationFingerprint"], self.source.compute_source_fingerprint(uri, result))

    def test_unsorted_raw_items_are_normalized_and_hash_is_order_independent(self):
        unsorted = raw(items=list(reversed(raw()["items"])))
        self.boundary.listing = unsorted
        self.assertEqual(observed(), self.read())
        self.assertEqual(["202", "101"], [item["exactId"] for item in unsorted["items"]])
        self.assertEqual(observed()["observationFingerprint"],
                         self.source.compute_source_fingerprint(payload()["sourceUri"], unsorted))

    def test_routes_and_typed_stages_fail_closed_without_listing(self):
        for url, kind in ((main.PAN_ROOT, "PageNotValidated"), (main.PASSPORT_ROOT, "LoginRequired"),
                          ("https://foreign.test/", "PageNotValidated"),
                          (payload()["sourceUri"] + "#x", "PageNotValidated"),
                          (payload()["sourceUri"].replace("a1B2", "other"), "PageNotValidated")):
            self.boundary = Boundary()
            self.boundary.url = url
            self.assertEqual({"kind": kind}, self.read())
            self.assertNotIn("stage", self.boundary.calls)
        for stage in [{"kind": k} for k in DENIALS if k != "AccountIdentityMismatch"] + [None, {}, [], {"kind": []},
                      {"kind": "Unknown"}, {"kind": "SourceOpened", "items": []}]:
            self.boundary = Boundary()
            self.boundary.stage = stage
            expected = stage["kind"] if type(stage) is dict and set(stage) == {"kind"} and stage["kind"] in DENIALS else "PageNotValidated"
            self.assertEqual({"kind": expected}, self.read())
            self.assertNotIn("listing", self.boundary.calls)

    def test_raw_metadata_revalidated_and_mismatch_is_typed(self):
        cases = [None, {}, [], {"kind": []}, {"kind": "Unknown"}, observed(), raw(items=[]), raw(totalCount=True),
                 raw(isStable=False), raw(reachedEnd=False), raw(baiduUserId="\ud800"), raw(generationToken=""),
                 raw(items=[{"exactId": "secret", "kind": "file", "serverName": "name"}], totalCount=1),
                 raw(items=[raw()["items"][0]] * 33, totalCount=33)]
        for value in cases:
            self.boundary = Boundary()
            self.boundary.listing = value
            self.assertEqual({"kind": "PageNotValidated"}, self.read())
        self.boundary.listing = raw(baiduUserId="other")
        self.assertEqual({"kind": "AccountIdentityMismatch"}, self.read())
        for kind in DENIALS:
            self.boundary.listing = {"kind": kind}
            self.assertEqual({"kind": kind}, self.read())
            self.boundary.listing = {"kind": kind, "private": "secret"}
            self.assertEqual({"kind": "PageNotValidated"}, self.read())

    def test_every_call_deadline_cancellation_and_exception_priority(self):
        self.assertEqual("Observed", self.read()["kind"])
        baseline = list(self.boundary.calls)
        for index in range(len(baseline)):
            for stop in ("TimedOut", "Cancelled"):
                for throws in (False, True):
                    with self.subTest(index=index, stop=stop, throws=throws):
                        self.boundary = Boundary()
                        def hook(_):
                            if len(self.boundary.calls) == index + 1:
                                self.boundary.now = 6 if stop == "TimedOut" else 0
                                self.boundary.cancelled = stop == "Cancelled"
                                if throws:
                                    raise RuntimeError("private-secret")
                        self.boundary.hook = hook
                        self.assertEqual({"kind": stop}, self.read())
                        self.assertEqual(index + 1, len(self.boundary.calls))

    def test_binding_changes_around_each_read_and_url_changes(self):
        for operation in ("url", "stage", "listing"):
            for field, value in (("browser", "wrong"), ("id", "wrong"), ("target", None), ("target", object())):
                self.boundary = Boundary()
                self.boundary.hook = lambda name: setattr(self.boundary, field, value) if name == operation else None
                self.assertEqual({"kind": "BrowserUnavailable"}, self.read())
        for operation in ("stage", "listing"):
            for url in (main.PASSPORT_ROOT, main.PAN_ROOT, payload()["sourceUri"].split("?")[0]):
                self.boundary = Boundary()
                self.boundary.hook = lambda name: setattr(self.boundary, "url", url) if name == operation else None
                self.assertEqual({"kind": "PageNotValidated"}, self.read())
        self.boundary = Boundary()
        self.read()
        baseline = list(self.boundary.calls)
        for operation in ("stage", "listing"):
            index = max(i for i in range(baseline.index(operation)) if baseline[i] == "target")
            for field, value in (("browser", "wrong"), ("id", "wrong"), ("target", None), ("target", object())):
                self.boundary = Boundary()
                def hook(_):
                    if len(self.boundary.calls) == index + 1:
                        setattr(self.boundary, field, value)
                self.boundary.hook = hook
                self.assertEqual({"kind": "BrowserUnavailable"}, self.read())
                self.assertNotIn(operation, self.boundary.calls)

    def test_attachment_budget_and_invalid_payload_never_attach(self):
        b = self.boundary
        def attach(budget):
            b.called("attach", budget.remaining())
            return b
        def read_with(body):
            return self.source.read_with_connector(body, attach, classify_route=main.classify_route,
                                                   monotonic=lambda: b.now, is_cancelled=lambda: b.cancelled)
        self.assertEqual(observed(), read_with(payload()))
        for stop in ("TimedOut", "Cancelled"):
            for throws in (False, True):
                b.now, b.cancelled, b.calls = 0, False, []
                def hook(_):
                    b.now, b.cancelled = (6, False) if stop == "TimedOut" else (0, True)
                    if throws:
                        raise RuntimeError("private")
                b.hook = hook
                self.assertEqual({"kind": stop}, read_with(payload()))
                self.assertEqual(["attach"], b.calls)
        b.calls = []
        self.assertEqual({"kind": "BrowserUnavailable"}, read_with(payload(script="private")))
        self.assertEqual([], b.calls)

    def test_attachment_budget_is_not_reset_and_pre_cancel_never_connects(self):
        b = self.boundary
        def attach(_):
            b.now = 4.9
            return b
        b.hook = lambda name: setattr(b, "now", 5.0) if name == "stage" else None
        self.assertEqual({"kind": "TimedOut"}, self.source.read_with_connector(
            payload(), attach, classify_route=main.classify_route, monotonic=lambda: b.now))
        self.assertNotIn("listing", b.calls)
        self.assertEqual({"kind": "Cancelled"}, self.source.read_with_connector(
            payload(), lambda _: self.fail("pre-cancelled attach"), classify_route=main.classify_route,
            is_cancelled=lambda: True))

    def test_unexpired_boundary_exceptions_redacted_and_no_retry(self):
        for operation in ("browser", "target", "target-id", "url", "stage", "listing"):
            self.boundary = Boundary()
            def hook(name):
                if name == operation:
                    raise RuntimeError("private-name private-uid " + payload()["sourceUri"])
            self.boundary.hook = hook
            self.assertEqual({"kind": "BrowserUnavailable"}, self.read())
            self.assertEqual(1, self.boundary.calls.count(operation))
        for field, value in (("browser", "wrong"), ("id", "wrong"), ("target", None)):
            self.boundary = Boundary()
            setattr(self.boundary, field, value)
            self.assertEqual({"kind": "BrowserUnavailable"}, self.read())
            self.assertNotIn("listing", self.boundary.calls)

    def test_script_adapter_rejects_invalid_budget_uid_result_and_redacts_errors(self):
        def forbidden(*_, **__):
            self.fail("invalid argument executed script")
        tab = SimpleNamespace(run_js=forbidden)
        for remaining in (0, -1, float("nan"), float("inf"), True, "1"):
            self.assertIn(self.source.observe_source_listing(tab, payload()["expectedBaiduUserId"], payload()["sourceUri"], remaining),
                          ({"kind": "TimedOut"}, {"kind": "BrowserUnavailable"}))
        for uid in ("", "\ud800", "😀" * 65):
            self.assertEqual({"kind": "BrowserUnavailable"}, self.source.observe_source_listing(tab, uid, payload()["sourceUri"], 1))
        for source_url in (None, [], "", payload()["sourceUri"] + "\n", "https://foreign.test/s/a"):
            self.assertEqual({"kind": "BrowserUnavailable"}, self.source.observe_source_listing(
                tab, payload()["expectedBaiduUserId"], source_url, 1))
        for value in (None, [], {"kind": "Observed", "html": "private"},
                      {**raw(), "items": [raw()["items"][0]] * 10000},
                      {**raw(), "unexpected": {"nested": {"secret": []}}},
                      raw(generationToken="x" * 100000)):
            tab = SimpleNamespace(run_js=lambda *_args, **_kwargs: value)
            self.assertEqual({"kind": "PageNotValidated"}, self.source.observe_source_listing(
                tab, payload()["expectedBaiduUserId"], payload()["sourceUri"], 1))
        def raises(*_, **__):
            raise RuntimeError("private-uid private-name")
        self.assertEqual({"kind": "BrowserUnavailable"}, self.source.observe_source_listing(
            SimpleNamespace(run_js=raises), payload()["expectedBaiduUserId"], payload()["sourceUri"], 1))

    def test_fixed_script_adapter_numeric_budget_only_and_existing_only_attach(self):
        self.assertTrue(hasattr(main._DrissionTarget, "observe_source_listing"))
        self.assertTrue(hasattr(main, "read_transfer_source"))
        calls = []
        def run_js(script, uid, milliseconds, expected_source_url, **kwargs):
            calls.append((script, uid, milliseconds, expected_source_url, kwargs))
            return raw()
        target = main._DrissionTarget(SimpleNamespace(run_js=run_js))
        self.assertEqual(raw(), target.observe_source_listing(payload()["expectedBaiduUserId"], payload()["sourceUri"], 0.25))
        self.assertEqual([(self.source.SOURCE_LISTING_SCRIPT, payload()["expectedBaiduUserId"], 250,
                           payload()["sourceUri"], {"timeout": 0.25})], calls)
        driver_calls = []
        class Options:
            def set_address(self, address):
                driver_calls.append(("address", address))
                return self
            def existing_only(self, value):
                driver_calls.append(("existing", value))
                return self
        b = self.boundary
        def chromium(options):
            driver_calls.append(("attach", type(options).__name__))
            return b
        with patch.dict(sys.modules, {"DrissionPage": SimpleNamespace(Chromium=chromium, ChromiumOptions=Options)}), \
                patch.object(main, "_DrissionConnector", side_effect=lambda browser: browser):
            self.assertEqual(observed(), main.read_transfer_source(payload()))
        self.assertEqual([("address", "127.0.0.1:9222"), ("existing", True), ("attach", "Options")], driver_calls)

    def test_real_adapters_with_fake_driver_never_navigate_query_identity_or_write(self):
        calls = []
        class Options:
            def set_address(inner, address):
                calls.append(("address", address))
                return inner
            def existing_only(inner, existing):
                calls.append(("existing", existing))
                return inner
        class Tab:
            tab_id = payload()["targetId"]
            url = payload()["sourceUri"]
            def get(inner, *_args, **_kwargs):
                self.fail("navigation")
            def run_js(inner, script, *args, **kwargs):
                calls.append(("script", script))
                if script == main.transfer_source.SOURCE_STAGE_SCRIPT:
                    self.assertEqual(1, len(args))
                    return {"kind": "SourceOpened"}
                self.assertEqual(self.source.SOURCE_LISTING_SCRIPT, script)
                self.assertEqual(payload()["expectedBaiduUserId"], args[0])
                self.assertEqual(3, len(args))
                self.assertEqual(payload()["sourceUri"], args[2])
                return raw()
        tab = Tab()
        class Browser:
            id = payload()["browserId"]
            tab_ids = [payload()["targetId"]]
            def __init__(inner, options):
                self.assertIsInstance(options, Options)
            def get_tab(inner, target_id):
                self.assertEqual(payload()["targetId"], target_id)
                calls.append(("target", target_id))
                return tab
        with patch.dict(sys.modules, {"DrissionPage": SimpleNamespace(Chromium=Browser, ChromiumOptions=Options)}):
            self.assertEqual(observed(), main.read_transfer_source(payload()))
        self.assertEqual(1, sum(c[0] == "target" for c in calls))
        self.assertEqual(2, sum(c[0] == "script" for c in calls))
        self.assertEqual([("address", "127.0.0.1:9222"), ("existing", True)], calls[:2])

    def test_each_attachment_call_checks_deadline_and_cancel_even_when_raising(self):
        real_budget = self.source.OperationBudget
        for trigger in ("options", "address", "existing", "browser"):
            for stop in ("TimedOut", "Cancelled"):
                for raises in (False, True):
                    b, calls = Boundary(), []
                    def called(name):
                        calls.append(name)
                        if name == trigger:
                            b.now, b.cancelled = (6, False) if stop == "TimedOut" else (0, True)
                            if raises:
                                raise RuntimeError("private-uid")
                    class Options:
                        def __init__(inner):
                            called("options")
                        def set_address(inner, _address):
                            called("address")
                            return inner
                        def existing_only(inner, value):
                            self.assertIs(True, value)
                            called("existing")
                            return inner
                    def browser(_):
                        called("browser")
                        return b
                    def budget(timeout, _monotonic, is_cancelled):
                        return real_budget(timeout, lambda: b.now, is_cancelled)
                    with patch.dict(sys.modules, {"DrissionPage": SimpleNamespace(Chromium=browser, ChromiumOptions=Options)}), \
                            patch.object(self.source, "OperationBudget", side_effect=budget):
                        self.assertEqual({"kind": stop}, main.read_transfer_source(payload(), is_cancelled=lambda: b.cancelled))
                    self.assertEqual(trigger, calls[-1])
                    self.assertEqual([], b.calls)

    def test_actual_fixed_script_against_synthetic_node_vm(self):
        node = shutil.which("node") or (r"D:\node.js\node.exe" if Path(r"D:\node.js\node.exe").is_file() else None)
        self.assertIsNotNone(node, "Node required; offline verification cannot skip")
        result = subprocess.run([node, str(Path(__file__).with_name("transfer_source_listing_harness.cjs"))],
                                input=json.dumps({"script": self.source.SOURCE_LISTING_SCRIPT}),
                                capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("offline listing assertions passed", result.stdout)

    def test_wrong_share_aba_during_actual_script_cannot_borrow_outer_original_url(self):
        node = shutil.which("node") or (r"D:\node.js\node.exe" if Path(r"D:\node.js\node.exe").is_file() else None)
        self.assertIsNotNone(node, "Node required; offline verification cannot skip")
        b = self.boundary
        def run_js(script, uid, milliseconds, expected_source_url, **kwargs):
            self.assertEqual(payload()["sourceUri"], b.url)
            result = subprocess.run([node, str(Path(__file__).with_name("transfer_source_listing_harness.cjs"))],
                input=json.dumps({"script": script, "probe": {"uid": uid, "budget": milliseconds,
                    "expectedSourceUrl": expected_source_url, "href": "https://pan.baidu.com/s/WRONG_SOURCE"}}),
                capture_output=True, text=True, encoding="utf-8", timeout=20)
            self.assertEqual(0, result.returncode, result.stderr)
            return json.loads(result.stdout)
        target = main._DrissionTarget(SimpleNamespace(run_js=run_js))
        b.observe_source_listing = target.observe_source_listing
        self.assertEqual({"kind": "PageNotValidated"}, self.read())
        self.assertEqual(payload()["sourceUri"], b.url)
        self.assertEqual(3, b.calls.count("url"))


if __name__ == "__main__":
    unittest.main()
