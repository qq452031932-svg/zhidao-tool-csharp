"""Offline picker opening: strict protocol, fake driver, actual fixed script in Node VM."""

import importlib.util
import inspect
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
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main
import protocol
from test_protocol import request
from test_transfer_source_listing import Boundary as SourceBoundary, observed, payload as source_payload

KINDS = ("PickerOpened", "PageNotValidated", "AccountIdentityMismatch", "LoginRequired",
         "VerificationRequired", "BrowserUnavailable", "TimedOut", "Cancelled", "OutcomeUnknown")


def payload(**changes):
    return {**source_payload(), "expectedSource": observed(), **changes}


class PickerProtocolTests(unittest.TestCase):
    def setUp(self):
        self.assertIn("OpenTransferPicker", protocol.ALLOWED_ACTIONS, "picker action missing")

    def test_fixed_capability_request_and_kind_only_roundtrips(self):
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
        operation = request("OpenTransferPicker", payload())
        self.assertEqual(operation, protocol.read_message(io.BytesIO(protocol.encode_message(operation))))
        for kind in KINDS:
            response = protocol.response_for(operation, {"kind": kind})
            self.assertEqual(response, protocol.read_message(io.BytesIO(protocol.encode_message(response, response=True)), response=True))
            for field in ("items", "baiduUserId", "sourceUri", "html", "detail"):
                with self.assertRaises(protocol.ProtocolError):
                    protocol.response_for(operation, {"kind": kind, field: "private"})
        for result in (None, [], {}, {"kind": []}, {"kind": "Opened"}, {"kind": "ExtractionRequired"}):
            with self.assertRaises(protocol.ProtocolError):
                protocol.response_for(operation, result)

    def test_strict_binding_source_shape_uid_and_fingerprint_before_dispatch(self):
        cases = []
        for field in payload():
            body = payload()
            del body[field]
            cases.append(body)
        for field, value in (("browserId", "wrong"), ("targetId", ""), ("timeoutMilliseconds", True),
                             ("timeoutMilliseconds", 120001), ("devToolsEndpoint", "http://localhost:9222/"),
                             ("sourceUri", "https://foreign.invalid/s/x"), ("expectedBaiduUserId", "other"),
                             ("expectedSource", {"kind": "PageNotValidated"}), ("script", "private"),
                             ("selector", "private"), ("expectedSource", None)):
            cases.append(payload(**{field: value}))
        for field, value in (("baiduUserId", "other"), ("generationToken", "changed"), ("totalCount", True),
                             ("isStable", False), ("reachedEnd", False), ("observationFingerprint", "0" * 64),
                             ("items", []), ("kind", []), ("html", "private")):
            cases.append(payload(expectedSource=observed(**{field: value})))
        for field in observed():
            value = observed()
            del value[field]
            cases.append(payload(expectedSource=value))
        for field, value in (("serverName", "other"), ("kind", "File"), ("exactId", "different")):
            source = observed()
            source["items"][0][field] = value
            cases.append(payload(expectedSource=source))
        for body in cases:
            with self.subTest(body=body), self.assertRaises(protocol.ProtocolError):
                protocol.validate_request(request("OpenTransferPicker", body))
        protocol.validate_request(request("OpenTransferPicker", payload(expectedSource=observed(items=list(reversed(observed()["items"]))))))

    def test_duplicate_fields_correlations_and_redaction(self):
        operation = request("OpenTransferPicker", payload())
        operation["correlationId"] = str(uuid.uuid4())
        encoded = protocol.encode_message(operation)[4:]
        duplicate = encoded.replace(b'"kind":"Observed"', b'"kind":"Observed","kind":"Observed"')
        with self.assertRaisesRegex(protocol.ProtocolError, "DuplicateField"):
            protocol.read_message(io.BytesIO(struct.pack(">I", len(duplicate)) + duplicate))
        incoming = protocol.encode_message(request()) + protocol.encode_message(operation) * 2
        out, events, calls = io.BytesIO(), io.StringIO(), []
        code = main.run(io.BytesIO(incoming), out, events, helper_pid=9876, parent_pid=1234,
                        picker_opener=lambda body: calls.append(body) or {"kind": "PickerOpened"})
        self.assertEqual(2, code)
        self.assertEqual([payload()], calls)
        self.assertEqual("HELPER_PROTOCOL_ERROR\n", events.getvalue())
        for secret in (payload()["sourceUri"], "测试文件夹", payload()["expectedBaiduUserId"]):
            self.assertNotIn(secret, protocol.safe_summary(operation))

    def test_separate_dispatch_and_kind_only_validation(self):
        self.assertIn("picker_opener", inspect.signature(main.run).parameters)
        for kind in KINDS:
            operation = request("OpenTransferPicker", payload())
            operation["correlationId"] = str(uuid.uuid4())
            incoming = protocol.encode_message(request()) + protocol.encode_message(operation)
            out, events, calls = io.BytesIO(), io.StringIO(), []
            code = main.run(io.BytesIO(incoming), out, events, helper_pid=9876, parent_pid=1234,
                            picker_opener=lambda body: calls.append(body) or {"kind": kind},
                            directory_reader=lambda _: self.fail("directory read"),
                            source_opener=lambda _: self.fail("source open"))
            self.assertEqual(0, code)
            self.assertEqual([payload()], calls)
            self.assertEqual("", events.getvalue())
            out.seek(0)
            protocol.read_message(out, response=True)
            response = protocol.read_message(out, response=True)
            protocol.validate_response_for(operation, response)
            self.assertEqual({"kind": kind}, response["result"])

    def test_package_required_and_copy_lists_include_module(self):
        script = (Path(__file__).resolve().parents[3] / "scripts" / "build_managed_browser_helper.ps1").read_text(encoding="utf-8")
        self.assertGreaterEqual(script.count("'transfer_picker.py'"), 2)


class Boundary(SourceBoundary):
    def __init__(self):
        super().__init__()
        self.picker_result = {"kind": "PickerOpened"}

    def open_transfer_picker(self, expected_uid, expected_source_url, expected_source, remaining):
        assert expected_uid == payload()["expectedBaiduUserId"]
        assert expected_source_url == self.url
        assert expected_source == payload()["expectedSource"]
        self.called("picker", remaining)
        return self.picker_result


class PickerRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("transfer_picker"), "fixed picker module missing")
        import transfer_picker
        self.picker = transfer_picker
        self.boundary = Boundary()

    def open(self, **changes):
        b = self.boundary
        return self.picker.open_bound_picker(payload(**changes), b, classify_route=main.classify_route,
                                             monotonic=lambda: b.now, is_cancelled=lambda: b.cancelled)

    def test_exact_binding_and_queryless_source_dispatch_once(self):
        self.assertEqual({"kind": "PickerOpened"}, self.open())
        self.assertEqual(1, self.boundary.calls.count("picker"))
        self.assertNotIn("listing", self.boundary.calls)
        self.boundary = Boundary()
        self.boundary.url = payload()["sourceUri"].split("?")[0]
        self.assertEqual({"kind": "PickerOpened"}, self.open())

    def test_invalid_payload_never_attaches_and_denials_never_dispatch(self):
        for body in (payload(script="private"), payload(expectedSource=observed(observationFingerprint="0" * 64)),
                     payload(expectedBaiduUserId="other")):
            self.assertEqual({"kind": "PageNotValidated"}, self.picker.open_with_connector(
                body, lambda _: self.fail("attach"), classify_route=main.classify_route))
        for url, kind in ((main.PASSPORT_ROOT, "LoginRequired"), (main.PAN_ROOT, "PageNotValidated"),
                          (payload()["sourceUri"] + "#x", "PageNotValidated"),
                          (payload()["sourceUri"].replace("a1B2", "C3d4"), "PageNotValidated")):
            self.boundary = Boundary()
            self.boundary.url = url
            self.assertEqual({"kind": kind}, self.open())
            self.assertNotIn("picker", self.boundary.calls)
        for stage in ("LoginRequired", "VerificationRequired", "ExtractionRequired", "PageNotValidated", "TimedOut", "Cancelled"):
            self.boundary = Boundary()
            self.boundary.stage = {"kind": stage}
            self.assertEqual({"kind": "PageNotValidated" if stage == "ExtractionRequired" else stage}, self.open())
            self.assertNotIn("picker", self.boundary.calls)

    def test_every_call_cancellation_deadline_exception_respects_dispatch_boundary(self):
        self.assertEqual({"kind": "PickerOpened"}, self.open())
        baseline = list(self.boundary.calls)
        dispatch = baseline.index("picker")
        for index in range(len(baseline)):
            for stop in ("TimedOut", "Cancelled", "Exception"):
                for throws in (False, True):
                    with self.subTest(index=index, stop=stop, throws=throws):
                        self.boundary = Boundary()
                        def hook(_):
                            if len(self.boundary.calls) == index + 1:
                                self.boundary.now = 6 if stop == "TimedOut" else 0
                                self.boundary.cancelled = stop == "Cancelled"
                                if throws or stop == "Exception":
                                    raise RuntimeError("private-secret")
                        self.boundary.hook = hook
                        expected = "OutcomeUnknown" if index >= dispatch else ("BrowserUnavailable" if stop == "Exception" else stop)
                        self.assertEqual({"kind": expected}, self.open())
                        self.assertEqual(index + 1, len(self.boundary.calls))
                        self.assertLessEqual(self.boundary.calls.count("picker"), 1)

    def test_binding_change_before_and_after_every_target_call(self):
        for operation in ("url", "stage", "picker"):
            for field, value in (("browser", "wrong"), ("id", "wrong"), ("target", None), ("target", object())):
                self.boundary = Boundary()
                self.boundary.hook = lambda name: setattr(self.boundary, field, value) if name == operation else None
                self.assertEqual({"kind": "OutcomeUnknown" if operation == "picker" else "BrowserUnavailable"}, self.open())
                self.assertEqual(int(operation == "picker"), self.boundary.calls.count("picker"))
        for operation in ("stage", "picker"):
            self.boundary = Boundary()
            self.boundary.hook = lambda name: setattr(self.boundary, "url", main.PAN_ROOT) if name == operation else None
            self.assertEqual({"kind": "OutcomeUnknown" if operation == "picker" else "PageNotValidated"}, self.open())
        self.boundary = Boundary()
        self.open()
        baseline = list(self.boundary.calls)
        for operation in ("stage", "picker"):
            index = max(i for i in range(baseline.index(operation)) if baseline[i] == "target")
            for field, value in (("browser", "wrong"), ("id", "wrong"), ("target", None), ("target", object())):
                self.boundary = Boundary()
                def hook(_):
                    if len(self.boundary.calls) == index + 1:
                        setattr(self.boundary, field, value)
                self.boundary.hook = hook
                self.assertEqual({"kind": "BrowserUnavailable"}, self.open())
                self.assertNotIn(operation, self.boundary.calls)

    def test_attachment_is_budgeted_and_precancel_never_attaches(self):
        for stop in ("TimedOut", "Cancelled", "Exception"):
            b = Boundary()
            def attach(_):
                b.now, b.cancelled = (6 if stop == "TimedOut" else 0), stop == "Cancelled"
                if stop == "Exception":
                    raise RuntimeError("private")
                return b
            result = self.picker.open_with_connector(payload(), attach, classify_route=main.classify_route,
                monotonic=lambda: b.now, is_cancelled=lambda: b.cancelled)
            self.assertEqual({"kind": "BrowserUnavailable" if stop == "Exception" else stop}, result)
            self.assertEqual([], b.calls)
        self.assertEqual({"kind": "Cancelled"}, self.picker.open_with_connector(payload(),
            lambda _: self.fail("attach"), classify_route=main.classify_route, is_cancelled=lambda: True))
        b = self.boundary
        def attach(_):
            b.now = 4.9
            return b
        b.hook = lambda name: setattr(b, "now", 5) if name == "stage" else None
        self.assertEqual({"kind": "TimedOut"}, self.picker.open_with_connector(payload(), attach,
            classify_route=main.classify_route, monotonic=lambda: b.now))
        self.assertNotIn("picker", b.calls)

    def test_malformed_dispatched_result_is_uncertain_typed_script_denials_preserved(self):
        for value in (None, [], {}, {"kind": []}, {"kind": "ExtractionRequired"}, {"kind": "PickerOpened", "private": "secret"}):
            self.boundary = Boundary()
            self.boundary.picker_result = value
            self.assertEqual({"kind": "OutcomeUnknown"}, self.open())
        for kind in KINDS:
            self.boundary = Boundary()
            self.boundary.picker_result = {"kind": kind}
            self.assertEqual({"kind": kind}, self.open())

    def test_fixed_adapter_prevalidation_and_transport_uncertainty(self):
        self.assertTrue(hasattr(main._CdpTarget, "open_transfer_picker"))
        uid, url, source = payload()["expectedBaiduUserId"], payload()["sourceUri"], observed()
        tab = SimpleNamespace(run_js=lambda *_args, **_kw: self.fail("invalid dispatch"))
        for remaining in (0, -1, True, float("nan"), float("inf"), "1"):
            self.assertIn(self.picker.open_picker(tab, uid, url, source, remaining)["kind"], ("TimedOut", "PageNotValidated"))
        for bad_uid, bad_url, bad_source in (("other", url, source), (uid, "https://foreign.invalid/", source),
                                            (uid, url, {}), (uid, url, observed(observationFingerprint="Z" * 64))):
            self.assertEqual({"kind": "PageNotValidated"}, self.picker.open_picker(tab, bad_uid, bad_url, bad_source, 1))
        calls = []
        tab.run_js = lambda *args, **kw: calls.append((args, kw)) or {"kind": "PickerOpened"}
        self.assertEqual({"kind": "PickerOpened"}, main._CdpTarget(tab).open_transfer_picker(uid, url, source, .25))
        self.assertEqual([((self.picker.OPEN_TRANSFER_PICKER_SCRIPT, uid, 250, url, source), {"timeout": .25})], calls)
        def raises(*_args, **_kwargs):
            raise RuntimeError("private transport")
        tab.run_js = raises
        self.assertEqual({"kind": "OutcomeUnknown"}, self.picker.open_picker(tab, uid, url, source, 1))
        tab.run_js = lambda *_args, **_kwargs: {"kind": "PickerOpened", "extra": "private"}
        self.assertEqual({"kind": "OutcomeUnknown"}, self.picker.open_picker(tab, uid, url, source, 1))

    def test_actual_adapters_existing_only_with_fake_driver(self):
        calls = []
        class Options:
            def set_address(inner, address):
                calls.append(("address", address))
                return inner
            def existing_only(inner, existing):
                calls.append(("existing", existing))
                return inner
        class Tab:
            tab_id, url = payload()["targetId"], payload()["sourceUri"]
            def get(inner, *_args, **_kwargs):
                self.fail("navigation")
            def run_js(inner, script, *args, **kwargs):
                calls.append(("script", script))
                if script == main.transfer_source.SOURCE_STAGE_SCRIPT:
                    return {"kind": "SourceOpened"}
                self.assertEqual(self.picker.OPEN_TRANSFER_PICKER_SCRIPT, script)
                self.assertEqual(payload()["expectedSource"], args[3])
                return {"kind": "PickerOpened"}
        class Browser:
            id, tab_ids = payload()["browserId"], [payload()["targetId"]]
            def __init__(inner, options):
                self.assertIsInstance(options, Options)
            def get_tab(inner, target_id):
                self.assertEqual(payload()["targetId"], target_id)
                return Tab()
        with patch.dict(sys.modules, {"cdp_client": SimpleNamespace(CdpBrowser=Browser, CdpOptions=Options)}):
            self.assertEqual({"kind": "PickerOpened"}, main.open_transfer_picker(payload()))
        self.assertEqual([("address", "127.0.0.1:9222"), ("existing", True)], calls[:2])
        self.assertEqual(2, sum(c[0] == "script" for c in calls))

    def test_each_existing_only_attachment_call_obeys_shared_budget(self):
        real_budget = self.picker.OperationBudget
        for trigger in ("options", "address", "existing", "browser"):
            for stop in ("TimedOut", "Cancelled"):
                for throws in (False, True):
                    with self.subTest(trigger=trigger, stop=stop, throws=throws):
                        b, calls = Boundary(), []
                        def called(name):
                            calls.append(name)
                            if name == trigger:
                                b.now, b.cancelled = (6, False) if stop == "TimedOut" else (0, True)
                                if throws:
                                    raise RuntimeError("private attachment")
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
                        with patch.dict(sys.modules, {"cdp_client": SimpleNamespace(CdpBrowser=browser, CdpOptions=Options)}), \
                                patch.object(self.picker, "OperationBudget", side_effect=budget):
                            self.assertEqual({"kind": stop}, main.open_transfer_picker(payload(), is_cancelled=lambda: b.cancelled))
                        self.assertEqual(trigger, calls[-1])
                        self.assertEqual([], b.calls)

    def test_actual_fixed_script_in_offline_node_vm(self):
        node = shutil.which("node") or (r"D:\node.js\node.exe" if Path(r"D:\node.js\node.exe").is_file() else None)
        self.assertIsNotNone(node, "Node required; do not skip offline proof")
        result = subprocess.run([node, str(Path(__file__).with_name("transfer_picker_harness.cjs"))],
            input=json.dumps({"script": self.picker.OPEN_TRANSFER_PICKER_SCRIPT, "expectedSource": observed()}),
            capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("offline picker assertions passed", result.stdout)


if __name__ == "__main__":
    unittest.main()
