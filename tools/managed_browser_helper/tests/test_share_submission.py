import hashlib
import json
import shutil
import struct
import subprocess
import sys
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace


ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import main
import protocol
import share_submission


def _fingerprint_exact_ids(values):
    digest = hashlib.sha256()
    for value in sorted(values, key=lambda item: item.encode("utf-16-be")):
        encoded = value.encode("utf-8")
        digest.update(struct.pack(">I", len(encoded)))
        digest.update(encoded)
    return digest.hexdigest()


def context(mode="Fixed", fixed_code="A1b2", exact_item_ids=None):
    exact_item_ids = ["item-1"] if exact_item_ids is None else exact_item_ids
    return {
        "browserId": "44444444-4444-4444-4444-444444444444",
        "targetId": "target-1",
        "devToolsEndpoint": "http://127.0.0.1:43127/",
        "timeoutMilliseconds": 30000,
        "baiduUserId": "uid-1",
        "targetDirectoryExactId": "directory-1",
        "targetCanonicalPath": "/prepared",
        "exactItemIds": exact_item_ids,
        "exactItemSetFingerprint": _fingerprint_exact_ids(exact_item_ids),
        "codeMode": mode,
        "fixedCode": fixed_code,
    }


def entry(share_id="old-share", share_url="https://pan.baidu.com/s/old_share",
          exact_item_ids=None):
    return {
        "shareId": share_id,
        "shareUrl": share_url,
        "exactItemIds": ["item-1"] if exact_item_ids is None else exact_item_ids,
        "isPermanent": True,
        "isAccessUnlimited": True,
        "codeMode": "Fixed",
        "effectiveCode": "A1b2",
    }


def listing(entries=None):
    entries = [entry()] if entries is None else entries
    value = {
        "kind": "Observed",
        **context(),
        "totalCount": len(entries),
        "reachedEnd": True,
        "isStable": True,
        "entries": entries,
    }
    value["shareIdsFingerprint"] = protocol.compute_share_ids_fingerprint(
        item["shareId"] for item in entries)
    value["observationFingerprint"] = protocol.compute_share_listing_fingerprint(value)
    return value


def request(action, payload):
    return {
        "protocolVersion": protocol.PROTOCOL_VERSION,
        "executionEngineVersion": protocol.EXECUTION_ENGINE_VERSION,
        "correlationId": str(uuid.uuid4()),
        "operationId": str(uuid.uuid4()),
        "accountId": str(uuid.uuid4()),
        "generation": 1,
        "action": action,
        "payload": payload,
    }


def submit_payload(action="SubmitShareOnce"):
    baseline = listing()
    return {
        **context(),
        "baselineShareIdsFingerprint": baseline["shareIdsFingerprint"],
        "baselineObservationFingerprint": baseline["observationFingerprint"],
    }


class ShareSubmissionProtocolTests(unittest.TestCase):
    def test_fixed_actions_and_strict_payloads_round_trip(self):
        self.assertEqual("b90828c325b3af76097628fa68c165c0c16574cd3b4b4971e0f4c4d407ccb824",
                         listing()["shareIdsFingerprint"])
        self.assertEqual("375f3c893fb7d2143779b9d747b382d4cf84df58a3edc37b3a5284d1e0f9835c",
                         listing()["observationFingerprint"])
        self.assertEqual("04eac615c92d026ec5e5f082a5f887bd77f8b9d169422e40c90464e97e63b6ba",
                         protocol.compute_share_outcome_fingerprint(
                             "request-1", "new-share", "https://pan.baidu.com/s/new_share", ["item-1"],
                             True, True, "Fixed", "A1b2"))
        cases = (
            ("ReadShareOfficialListing", context(), listing()),
            ("SubmitShareOnce", submit_payload(),
             {"kind": "ClickAttempted", "dispatchState": "ClickDispatched"}),
            ("ObserveShareSubmissionOutcome", submit_payload(), {
                "kind": "AcceptedBound", "adapterRequestId": "request-1", "shareId": "new-share",
                "shareUrl": "https://pan.baidu.com/s/new_share", "exactItemIds": ["item-1"],
                "isPermanent": True, "isAccessUnlimited": True, "codeMode": "Fixed",
                "effectiveCode": "A1b2", "officialObservationFingerprint":
                    protocol.compute_share_outcome_fingerprint(
                        "request-1", "new-share", "https://pan.baidu.com/s/new_share", ["item-1"],
                        True, True, "Fixed", "A1b2"),
            }),
        )
        for action, payload, result in cases:
            message = request(action, payload)
            protocol.validate_request(message)
            protocol.validate_response(protocol.response_for(message, result))

    def test_random_and_fixed_codes_are_the_only_settings(self):
        protocol.validate_share_submission_request("ReadShareOfficialListing", context("Random", None))
        protocol.validate_share_submission_request("ReadShareOfficialListing", context("Fixed", "Z9y8"))
        for payload in (context("Random", "A1b2"), context("Fixed", None),
                        context("Fixed", "abc"), context("Unknown", None)):
            with self.assertRaises(protocol.ProtocolError):
                protocol.validate_share_submission_request("ReadShareOfficialListing", payload)

    def test_arbitrary_navigation_script_selector_switch_and_bulk_fields_are_rejected(self):
        for field in ("selector", "autoFillExtractionCodeSelector", "autoFillExtractionCode",
                      "script", "url", "targetUrl", "items", "batch", "retry"):
            if field == "items":
                private_value = ["item-1", "item-2"]
            elif field == "autoFillExtractionCode":
                private_value = False
            else:
                private_value = "private"
            payload = {**context(), field: private_value}
            with self.assertRaises(protocol.ProtocolError):
                protocol.validate_share_submission_request("ReadShareOfficialListing", payload)

    def test_listing_requires_complete_stable_exact_item_sets(self):
        protocol.validate_share_listing_result(listing())
        bad = listing()
        bad["entries"][0]["shareUrl"] = "https://evil.example/s/share"
        with self.assertRaises(protocol.ProtocolError):
            protocol.validate_share_listing_result(bad)
        bad = listing()
        bad["entries"][0]["isPermanent"] = False
        with self.assertRaises(protocol.ProtocolError):
            protocol.validate_share_listing_result(bad)
        bad = listing()
        bad["entries"][0]["exactItemIds"] = []
        with self.assertRaises(protocol.ProtocolError):
            protocol.validate_share_listing_result(bad)

    def test_multi_object_context_listing_and_outcome_round_trip(self):
        items = ["item-1", "item-2"]
        multi_context = context(exact_item_ids=items)
        multi_entry = entry(exact_item_ids=items)
        value = listing([multi_entry])
        value.update(multi_context)
        value["observationFingerprint"] = protocol.compute_share_listing_fingerprint(value)
        protocol.validate_share_submission_request("ReadShareOfficialListing", multi_context)
        protocol.validate_share_listing_result(value)
        outcome = {
            "kind": "AcceptedBound", "adapterRequestId": "request-2", "shareId": "new-share-2",
            "shareUrl": "https://pan.baidu.com/s/new_share_2", "exactItemIds": items,
            "isPermanent": True, "isAccessUnlimited": True, "codeMode": "Fixed",
            "effectiveCode": "A1b2",
        }
        outcome["officialObservationFingerprint"] = protocol.compute_share_outcome_fingerprint(
            outcome["adapterRequestId"], outcome["shareId"], outcome["shareUrl"],
            items, True, True, "Fixed", "A1b2")
        protocol.validate_share_outcome_result(outcome)


class ShareSubmissionBoundaryTests(unittest.TestCase):
    def test_fixed_tab_adapters_read_submit_once_and_observe_without_caller_programs(self):
        calls, isolated = [], []

        def run_js(script, *args, **kwargs):
            calls.append((script, args, kwargs))
            if script == share_submission.READ_SHARE_OFFICIAL_LISTING_SCRIPT:
                value = listing()
                return {key: item for key, item in value.items()
                        if key not in ("shareIdsFingerprint", "observationFingerprint")}
            return {
                "kind": "AcceptedBound", "adapterRequestId": "request-1", "shareId": "new-share",
                "shareUrl": "https://pan.baidu.com/s/new_share", "exactItemIds": ["item-1"],
                "isPermanent": True, "isAccessUnlimited": True, "codeMode": "Fixed",
                "effectiveCode": "A1b2",
            }

        def run_share_submit_isolated(*args, **kwargs):
            isolated.append((args, kwargs))
            return {"kind": "ClickAttempted", "dispatchState": "ClickDispatched"}

        tab = SimpleNamespace(run_js=run_js, run_share_submit_isolated=run_share_submit_isolated)
        observed = share_submission.read_official_listing(tab, context(), 1)
        submitted = share_submission.submit_once(tab, submit_payload(), 1)
        outcome = share_submission.observe_outcome(tab, submit_payload(), 1)
        self.assertEqual("Observed", observed["kind"])
        self.assertEqual({"kind": "ClickAttempted", "dispatchState": "ClickDispatched"}, submitted)
        self.assertEqual("AcceptedBound", outcome["kind"])
        self.assertEqual(1, len(isolated))
        isolated_args, isolated_kwargs = isolated[0]
        self.assertEqual(6, len(isolated_args))
        self.assertFalse(any(type(value) is bool for value in isolated_args))
        self.assertNotIn("selector", isolated_args[0])
        self.assertNotIn("autoFillExtractionCodeSelector", isolated_args[0])
        self.assertNotIn("autoFillExtractionCode", isolated_args[0])
        self.assertEqual({"timeout": 1}, isolated_kwargs)
        self.assertEqual(3, len(calls))
        self.assertNotIn(share_submission.SUBMIT_SHARE_ONCE_SCRIPT, [item[0] for item in calls])

    def test_isolated_world_setup_is_known_not_dispatched_and_runtime_loss_is_unknown(self):
        payload = submit_payload()
        self.assertEqual(
            {"kind": "BrowserUnavailable", "dispatchState": "KnownNotDispatched"},
            share_submission.submit_once(SimpleNamespace(), payload, 1))

        class RuntimeFails:
            tab_id = context()["targetId"]
            url = "https://pan.baidu.com/disk/main#/index?category=all"

            def run_js(self, *_args, **_kwargs):
                value = listing()
                return {key: item for key, item in value.items()
                        if key not in ("shareIdsFingerprint", "observationFingerprint")}

            def run_cdp(self, command, **_kwargs):
                if command == "Page.getFrameTree":
                    return {"frameTree": {"frame": {"id": "frame", "url": self.url}}}
                if command == "Page.createIsolatedWorld":
                    return {"executionContextId": 1}
                raise RuntimeError("private")

        result = main._DrissionTarget(RuntimeFails()).submit_share_once(payload, 1)
        self.assertEqual(
            {"kind": "BrowserUnavailable", "dispatchState": "DispatchOutcomeUnknown"}, result)


class ShareSubmissionDomTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if shutil.which("node") is None:
            raise unittest.SkipTest("Node.js is required for the synthetic VM harness")

    def run_harness(self, mode, exact_item_ids=None):
        selected_context = context(exact_item_ids=exact_item_ids)
        baseline = listing([entry(exact_item_ids=selected_context["exactItemIds"])])
        baseline.update(selected_context)
        baseline["observationFingerprint"] = protocol.compute_share_listing_fingerprint(baseline)
        value = {
            "mode": mode,
            "context": selected_context,
            "baselineShareIdsFingerprint": baseline["shareIdsFingerprint"],
            "baselineObservationFingerprint": baseline["observationFingerprint"],
            "listingScript": share_submission.READ_SHARE_OFFICIAL_LISTING_SCRIPT,
            "submitScript": share_submission.SUBMIT_SHARE_ONCE_SCRIPT,
            "outcomeScript": share_submission.OBSERVE_SHARE_SUBMISSION_OUTCOME_SCRIPT,
        }
        completed = subprocess.run(
            ["node", str(Path(__file__).with_name("share_submission_harness.cjs"))],
            input=json.dumps(value, ensure_ascii=False), text=True, encoding="utf-8",
            capture_output=True, timeout=15, check=False)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("share submission assertions passed", completed.stdout)

    def test_listing_is_read_only_complete_stable_and_exact(self):
        self.run_harness("listing")

    def test_submit_reproves_and_uses_one_native_click(self):
        self.run_harness("submit")

    def test_multi_object_submit_selects_the_exact_set_with_one_native_click(self):
        self.run_harness("submit", ["item-1", "item-2"])

    def test_outcome_is_read_only_and_binds_exact_official_result(self):
        self.run_harness("outcome")


if __name__ == "__main__":
    unittest.main()
