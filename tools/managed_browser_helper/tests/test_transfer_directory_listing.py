"""Offline directory protocol/adapter/fixed-script tests. Never launch a browser/helper."""
import copy
import importlib.util
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

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import main
import protocol
from test_protocol import request
from test_transfer_source_listing import Boundary as SourceBoundary

FIXTURE = json.loads((Path(__file__).with_name("fixtures") / "read_transfer_directory.json").read_text(encoding="utf-8"))
DENIALS = ("LoginRequired", "VerificationRequired", "PageNotValidated", "BrowserUnavailable", "AccountIdentityMismatch", "TimedOut", "Cancelled")

def payload(**changes):
    return {**FIXTURE["request"]["payload"], "timeoutMilliseconds": 5000, **changes}

def observed(**changes):
    return {**copy.deepcopy(FIXTURE["result"]), **changes}

def raw(**changes):
    value = observed(**changes); value.pop("observationFingerprint"); return value

class DirectoryProtocolTests(unittest.TestCase):
    def setUp(self):
        self.assertIn("ReadTransferDirectory", protocol.ALLOWED_ACTIONS, "directory action missing")

    def test_fixture_denials_roundtrip_and_redact(self):
        operation = FIXTURE["request"]
        self.assertEqual(operation, protocol.read_message(io.BytesIO(protocol.encode_message(operation))))
        for value in [observed()] + [{"kind": kind} for kind in DENIALS]:
            response = protocol.response_for(operation, value)
            self.assertEqual(response, protocol.read_message(io.BytesIO(protocol.encode_message(response, response=True)), response=True))
            self.assertNotIn("目标目录", protocol.safe_summary(response))
        for kind in DENIALS:
            with self.assertRaises(protocol.ProtocolError): protocol.response_for(operation, {"kind": kind, "items": []})

    def test_request_fields_pairing_and_canonical_path_are_strict(self):
        for field in payload():
            body = payload(); del body[field]
            with self.assertRaises(protocol.ProtocolError): protocol.validate_request(request("ReadTransferDirectory", body))
        for changes in ({"expectedParentExactId": "parent-1"}, {"expectedCanonicalPath": "/a"},
                        {"expectedParentExactId": "bad/id", "expectedCanonicalPath": "/a"},
                        {"expectedParentExactId": "parent-1", "expectedCanonicalPath": "/a/"},
                        {"sourceUri": "https://foreign.test/"}, {"script": "private"}):
            with self.assertRaises(protocol.ProtocolError): protocol.validate_request(request("ReadTransferDirectory", payload(**changes)))
        protocol.validate_request(request("ReadTransferDirectory", payload(expectedParentExactId="parent-1", expectedCanonicalPath="/a")))

    def test_exact_empty_root_and_whitespace_names_are_valid(self):
        protocol.response_for(FIXTURE["request"], observed(items=[], totalCount=0, canonicalPath="/", ancestorExactIds=["parent-1"]))
        value = observed(); value["items"][0]["serverName"] = " "
        protocol.response_for(FIXTURE["request"], value)

    def test_fields_bounds_ancestors_and_folder_only_are_enforced(self):
        def reject(value):
            with self.assertRaises(protocol.ProtocolError): protocol.response_for(FIXTURE["request"], value)
        for field in observed():
            value = observed(); del value[field]; reject(value)
        for path in ("", "a", "/a/", "//a", "/a/../b", "/a/./b", "/a\\b", " /a", "/a ", "/a\n", "/\ud800", "/" + "中"*86, "/" + "/".join(["a"]*33)):
            reject(observed(canonicalPath=path))
        for ancestors in ([], ["parent-1"], ["parent-1", "parent-1"], ["root-1", "wrong"], ["bad/id", "parent-1"], ["root-1"]*34, None):
            reject(observed(ancestorExactIds=ancestors))
        for field, value in (("totalCount", 0), ("totalCount", True), ("totalCount", 2.0), ("reachedEnd", False), ("isStable", 1),
                             ("baiduUserId", "\ud800"), ("generationToken", " "), ("parentExactId", "a/b"), ("items", None),
                             ("observationFingerprint", "A"*64), ("extra", "private")):
            reject(observed(**{field: value}))
        for field, value in (("exactId", "root-1"), ("exactId", "parent-1"), ("exactId", "202"), ("exactId", "a"*129),
                             ("kind", "File"), ("kind", []), ("serverName", ""), ("serverName", "\ud800"), ("serverName", "中"*86)):
            body = observed(); body["items"][0][field] = value; reject(body)
        reject(observed(items=[{"exactId": str(i), "kind": "Folder", "serverName": "x"} for i in range(33)], totalCount=33))

    def test_dispatch_uses_only_fixed_directory_handler(self):
        self.assertIn("directory_reader", __import__('inspect').signature(main.run).parameters)
        operation = {**FIXTURE["request"], "correlationId": str(uuid.uuid4())}
        incoming = protocol.encode_message(request()) + protocol.encode_message(operation)
        out, errors, calls = io.BytesIO(), io.StringIO(), []
        def read(body): calls.append(body); return observed()
        self.assertEqual(0, main.run(io.BytesIO(incoming), out, errors, helper_pid=321, parent_pid=1234, directory_reader=read))
        self.assertEqual([FIXTURE["request"]["payload"]], calls)
        self.assertEqual("", errors.getvalue())
        stream = io.BytesIO(out.getvalue()); protocol.read_message(stream, response=True)
        self.assertEqual(observed(), protocol.read_message(stream, response=True)["result"])

    def test_exact_path_byte_limit_and_max_depth(self):
        segments = ["a"*256]*15 + ["b"*240]
        path = "/" + "/".join(segments)
        self.assertEqual(4096, len(path.encode()))
        ancestors = ["root"]+[f"d{i}" for i in range(15)]+["parent-1"]
        protocol.response_for(FIXTURE["request"], observed(canonicalPath=path, ancestorExactIds=ancestors))
        with self.assertRaises(protocol.ProtocolError):
            protocol.response_for(FIXTURE["request"], observed(canonicalPath=path+"x", ancestorExactIds=ancestors))
        protocol.response_for(FIXTURE["request"], observed(canonicalPath="/"+"/".join(["a"]*32), ancestorExactIds=[f"d{i}" for i in range(32)]+["parent-1"]))

class Boundary(SourceBoundary):
    def __init__(self):
        super().__init__(); self.url = payload()["sourceUri"]; self.listing = raw()
    def observe_source_stage(self, *_): raise AssertionError("picker must not require source-stage detector")
    def observe_directory_listing(self, uid, source_url, remaining):
        assert uid == payload()["expectedBaiduUserId"] and source_url == self.url
        self.called("listing", remaining); return self.listing

class DirectoryRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("transfer_directory_listing"), "directory module missing")
        import transfer_directory_listing
        self.directory = transfer_directory_listing; self.boundary = Boundary()
    def read(self, **changes):
        b = self.boundary
        return self.directory.read_bound_directory(payload(**changes), b, classify_route=main.classify_route,
            monotonic=lambda: b.now, is_cancelled=lambda: b.cancelled)

    def test_fixture_hash_read_and_expected_directory(self):
        self.assertEqual(observed()["observationFingerprint"], self.directory.compute_directory_fingerprint(payload()["sourceUri"], raw()))
        self.assertEqual(observed(), self.read()); self.assertEqual(1, self.boundary.calls.count("listing"))
        self.assertNotIn("stage", self.boundary.calls)
        self.boundary.url = payload()["sourceUri"].split("?")[0]
        self.assertEqual(observed(), self.read(expectedParentExactId="parent-1", expectedCanonicalPath="/目标目录"))
        self.assertEqual({"kind": "PageNotValidated"}, self.read(expectedParentExactId="wrong", expectedCanonicalPath="/目标目录"))
        self.assertEqual({"kind": "PageNotValidated"}, self.read(expectedParentExactId="parent-1", expectedCanonicalPath="/wrong"))
        self.boundary.listing = raw(items=list(reversed(raw()["items"])))
        self.assertEqual(observed(), self.read())

    def test_invalid_or_partial_results_never_become_empty_success(self):
        for value in (None, {}, [], observed(), raw(totalCount=True), raw(items=[]), raw(isStable=False), raw(parentExactId="wrong"), raw(ancestorExactIds=[])):
            self.boundary.listing = value; self.assertEqual({"kind": "PageNotValidated"}, self.read())
        self.boundary.listing = raw(baiduUserId="other"); self.assertEqual({"kind": "AccountIdentityMismatch"}, self.read())
        self.boundary.listing = raw(items=[], totalCount=0)
        self.assertEqual("Observed", self.read()["kind"])
        for kind in DENIALS:
            self.boundary.listing = {"kind": kind}; self.assertEqual({"kind": kind}, self.read())
            self.boundary.listing["private"] = "secret"; self.assertEqual({"kind": "PageNotValidated"}, self.read())

    def test_route_and_binding_changes_are_denied(self):
        for url, kind in ((main.PASSPORT_ROOT, "LoginRequired"), (main.PAN_ROOT, "PageNotValidated"), ("https://foreign.test/", "PageNotValidated")):
            self.boundary = Boundary(); self.boundary.url = url
            self.assertEqual({"kind": kind}, self.read()); self.assertNotIn("listing", self.boundary.calls)
        for operation in ("url", "listing"):
            for field, value in (("browser", "wrong"), ("id", "wrong"), ("target", None), ("target", object())):
                self.boundary = Boundary()
                self.boundary.hook = lambda name: setattr(self.boundary, field, value) if name == operation else None
                self.assertEqual({"kind": "BrowserUnavailable"}, self.read())
        for url in (main.PASSPORT_ROOT, payload()["sourceUri"].split('?')[0], "https://pan.baidu.com/s/other"):
            self.boundary = Boundary(); self.boundary.hook = lambda name: setattr(self.boundary, "url", url) if name == "listing" else None
            self.assertEqual({"kind": "PageNotValidated"}, self.read())

    def test_each_boundary_checks_stop_even_if_operation_raises(self):
        self.read(); baseline = list(self.boundary.calls)
        for index in range(len(baseline)):
            for stop in ("TimedOut", "Cancelled"):
                for throws in (False, True):
                    self.boundary = Boundary()
                    def hook(_):
                        if len(self.boundary.calls) == index + 1:
                            self.boundary.now = 6 if stop == "TimedOut" else 0; self.boundary.cancelled = stop == "Cancelled"
                            if throws: raise RuntimeError("private")
                    self.boundary.hook = hook
                    self.assertEqual({"kind": stop}, self.read()); self.assertEqual(index+1, len(self.boundary.calls))

    def test_attachment_shares_budget_and_invalid_or_cancelled_input_never_attaches(self):
        b = self.boundary
        def attach(budget): b.now = 5; return b
        self.assertEqual({"kind": "TimedOut"}, self.directory.read_with_connector(payload(), attach, classify_route=main.classify_route, monotonic=lambda:b.now))
        def forbidden(_): self.fail("must not attach")
        self.assertEqual({"kind": "Cancelled"}, self.directory.read_with_connector(payload(), forbidden, classify_route=main.classify_route, is_cancelled=lambda:True))
        self.assertEqual({"kind": "BrowserUnavailable"}, self.directory.read_with_connector(payload(script="x"), forbidden, classify_route=main.classify_route))

    def test_unexpired_boundary_exceptions_are_redacted_and_not_retried(self):
        for operation in ("browser", "target", "target-id", "url", "listing"):
            self.boundary = Boundary()
            def hook(name):
                if name == operation: raise RuntimeError("private directory and uid")
            self.boundary.hook = hook
            self.assertEqual({"kind": "BrowserUnavailable"}, self.read())
            self.assertEqual(1, self.boundary.calls.count(operation))

    def test_each_real_attachment_boundary_checks_stop_including_exception(self):
        real_budget = self.directory.OperationBudget
        for trigger in ("options", "address", "existing", "browser"):
            for stop in ("TimedOut", "Cancelled"):
                for throws in (False, True):
                    b, calls = Boundary(), []
                    def called(name):
                        calls.append(name)
                        if name == trigger:
                            b.now = 6 if stop == "TimedOut" else 0; b.cancelled = stop == "Cancelled"
                            if throws: raise RuntimeError("private")
                    class Options:
                        def __init__(inner): called("options")
                        def set_address(inner, _): called("address"); return inner
                        def existing_only(inner, value): self.assertIs(True, value); called("existing"); return inner
                    def browser(_): called("browser"); return b
                    def budget(timeout, _monotonic, cancelled): return real_budget(timeout, lambda:b.now, cancelled)
                    with patch.dict(sys.modules, {"DrissionPage": SimpleNamespace(Chromium=browser, ChromiumOptions=Options)}), patch.object(self.directory, "OperationBudget", side_effect=budget):
                        self.assertEqual({"kind":stop}, main.read_transfer_directory(payload(), is_cancelled=lambda:b.cancelled))
                    self.assertEqual(trigger, calls[-1]); self.assertEqual([], b.calls)

    def test_fixed_adapter_and_existing_only_attach_with_fake_driver(self):
        calls = []
        class Options:
            def set_address(inner, address): calls.append(("address", address)); return inner
            def existing_only(inner, existing): calls.append(("existing", existing)); return inner
        class Tab:
            tab_id = payload()["targetId"]; url = payload()["sourceUri"]
            def get(inner, *_args, **_kwargs): self.fail("navigation")
            def run_js(inner, script, uid, milliseconds, source_url, **kwargs):
                self.assertEqual(self.directory.DIRECTORY_LISTING_SCRIPT, script)
                self.assertEqual(payload()["expectedBaiduUserId"], uid); self.assertEqual(payload()["sourceUri"], source_url)
                calls.append(("script",)); return raw()
        tab = Tab()
        class Browser:
            id = payload()["browserId"]; tab_ids = [payload()["targetId"]]
            def __init__(inner, _): pass
            def get_tab(inner, target_id): self.assertEqual(tab.tab_id, target_id); return tab
        with patch.dict(sys.modules, {"DrissionPage": SimpleNamespace(Chromium=Browser, ChromiumOptions=Options)}):
            self.assertEqual(observed(), main.read_transfer_directory(payload()))
        self.assertEqual([("address", "127.0.0.1:9222"), ("existing", True), ("script",)], calls)

    def test_fixed_script_rejects_bad_budget_and_unsafe_metadata(self):
        def forbidden(*_, **__): self.fail("must not execute script")
        for remaining in (0, -1, float('nan'), float('inf'), True, "1"):
            self.assertIn(self.directory.observe_directory_listing(SimpleNamespace(run_js=forbidden), payload()["expectedBaiduUserId"], payload()["sourceUri"], remaining), ({"kind":"TimedOut"}, {"kind":"BrowserUnavailable"}))
        for value in (None, {}, raw(totalCount=False), raw(items=[]), raw(canonicalPath="/../x")):
            tab = SimpleNamespace(run_js=lambda *_args, **_kwargs: value)
            self.assertEqual({"kind":"PageNotValidated"}, self.directory.observe_directory_listing(tab, payload()["expectedBaiduUserId"], payload()["sourceUri"], 1))

    def test_fixed_script_in_node_vm_no_browser(self):
        node = shutil.which("node") or (r"D:\node.js\node.exe" if Path(r"D:\node.js\node.exe").is_file() else None)
        self.assertIsNotNone(node, "offline script tests require Node")
        result = subprocess.run([node, str(Path(__file__).with_name("transfer_directory_listing_harness.cjs"))],
            input=json.dumps({"script": self.directory.DIRECTORY_LISTING_SCRIPT}), capture_output=True, text=True, encoding="utf-8", timeout=20)
        self.assertEqual(0, result.returncode, result.stdout+result.stderr)
        self.assertIn("offline directory assertions passed", result.stdout)

    def test_actual_script_rejects_wrong_share_between_equal_outer_urls(self):
        node = shutil.which("node") or (r"D:\node.js\node.exe" if Path(r"D:\node.js\node.exe").is_file() else None)
        self.assertIsNotNone(node)
        b = self.boundary
        def run_js(script, uid, milliseconds, expected_source_url, **kwargs):
            self.assertEqual(payload()["sourceUri"], b.url)
            result = subprocess.run([node, str(Path(__file__).with_name("transfer_directory_listing_harness.cjs"))],
                input=json.dumps({"script":script,"probe":{"uid":uid,"budget":milliseconds,"expectedSourceUrl":expected_source_url,"href":"https://pan.baidu.com/s/WRONG"}}),
                capture_output=True,text=True,encoding="utf-8",timeout=20)
            self.assertEqual(0,result.returncode,result.stderr); return json.loads(result.stdout)
        b.observe_directory_listing = main._DrissionTarget(SimpleNamespace(run_js=run_js)).observe_directory_listing
        self.assertEqual({"kind":"PageNotValidated"},self.read())
        self.assertEqual(payload()["sourceUri"],b.url)

if __name__ == "__main__": unittest.main()
