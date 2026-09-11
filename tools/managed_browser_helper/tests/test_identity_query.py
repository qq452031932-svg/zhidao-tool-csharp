"""Offline contract tests: no browser, credentials, or network are used."""

import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import unittest

HELPER_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HELPER_ROOT))


class IdentityQueryTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("identity_query"),
                             "production fixed identity query module is missing")
        import identity_query
        self.query = identity_query

    def parse(self, body, status=200):
        return self.query.parse_query_envelope(
            {"kind": "QueryResponse", "status": status, "body": body})

    def test_exact_numeric_uid_survives_beyond_javascript_safe_integer(self):
        for uid in (1, 42, 9007199254740993, 9223372036854775807):
            with self.subTest(uid=uid):
                body = '{"errno":0,"result":{"uk":' + str(uid) + ',"username":"private-name"}}'
                self.assertEqual({"kind": "Observed", "stableBaiduUserId": str(uid)}, self.parse(body))

    def test_string_uid_is_trimmed_and_canonicalized_without_username_requirement(self):
        for uid in ("42", " 00042 ", "00000000000000000000042"):
            self.assertEqual({"kind": "Observed", "stableBaiduUserId": "42"},
                             self.parse(json.dumps({"errno": "0", "result": {"uk": uid}})))

    def test_invalid_uid_never_authenticates(self):
        for uid in (True, False, None, 0, -1, 1.0, 1.5, 9223372036854775808,
                    "", "0", "-1", "+1", "1.0", "1e2", "１２", "٤٢", "4 2",
                    "42\n", "\t42", "42\u0085", "42\u200b", "9" * 257,
                    "9223372036854775808", {}, []):
            with self.subTest(uid=repr(uid)):
                self.assertEqual({"kind": "BrowserUnavailable"},
                                 self.parse(json.dumps({"errno": 0, "result": {"uk": uid}})))

    def test_malformed_ambiguous_duplicate_nonfinite_and_missing_payloads_fail_closed(self):
        for body in ("{", "[]", "null", "{}", '{"errno":0}',
                     '{"errno":0,"result":null}', '{"errno":0,"result":{}}',
                     '{"errno":0,"errno":0,"result":{"uk":42}}',
                     '{"errno":0,"result":{"uk":42,"uk":43}}',
                     '{"errno":0,"result":{"uk":42,"other":{"x":1,"x":2}}}',
                     '{"errno":0,"result":{"uk":42,"other":NaN}}',
                     '{"errno":0,"result":{"uk":42,"other":Infinity}}',
                     '{"errno":0,"result":{"uk":42,"other":1e9999}}',
                     '{"errno":0,"result":{"uk":42}} trailing',
                     '{"errno":0,"result":{"uk":4.2e1}}',
                     '{"errno":0,"result":{"uk":42},"other":' + '[' * 1000 + '0' + ']' * 1000 + '}'):
            with self.subTest(body=body[:100]):
                self.assertEqual({"kind": "BrowserUnavailable"}, self.parse(body))

    def test_only_explicit_login_signals_are_login_required(self):
        self.assertEqual({"kind": "LoginRequired"}, self.parse("", status=401))
        for errno in (-6, "-6"):
            self.assertEqual({"kind": "LoginRequired"}, self.parse(json.dumps({"errno": errno})))
        for errno in (False, True, 0.0, -6.0, " 0", "-06", "-6 ", 1, -9, None):
            with self.subTest(errno=errno):
                self.assertEqual({"kind": "BrowserUnavailable"},
                                 self.parse(json.dumps({"errno": errno, "result": {"uk": 42}})))
        for status in (301, 302, 403, 429, 500):
            self.assertEqual({"kind": "BrowserUnavailable"},
                             self.parse('{"errno":-6,"result":{"uk":42}}', status))

    def test_private_envelope_is_strict_and_bounded(self):
        valid = {"kind": "QueryResponse", "status": 200, "body": '{"errno":0,"result":{"uk":42}}'}
        invalid = [None, "{}", [], {}, {**valid, "unexpected": "private"},
                   {**valid, "kind": []}, {**valid, "status": -1}, {**valid, "status": 600},
                   {**valid, "status": True}, {**valid, "status": 200.0},
                   {**valid, "status": "200"}, {**valid, "body": {}},
                   {**valid, "body": "x" * (65536 + 1)},
                   {**valid, "body": "中" * 22000}, {**valid, "body": "\ud800"},
                   {"kind": "TimedOut", "body": "private"},
                   {"kind": "BrowserUnavailable", "url": "private"},
                   {"kind": "Observed", "stableBaiduUserId": "42"},
                   {"kind": "Cancelled"}, {"kind": "LoginRequired"}]
        for value in invalid:
            with self.subTest(value=str(value)[:100]):
                self.assertEqual({"kind": "BrowserUnavailable"}, self.query.parse_query_envelope(value))
        for kind in ("BrowserUnavailable", "TimedOut"):
            self.assertEqual({"kind": kind}, self.query.parse_query_envelope({"kind": kind}))
        padded = valid["body"] + " " * (65536 - len(valid["body"]))
        self.assertEqual({"kind": "Observed", "stableBaiduUserId": "42"}, self.parse(padded))

    def test_wrapper_uses_only_immutable_script_and_timeout_argument(self):
        calls = []

        class Tab:
            def run_js(inner, script, *args, **kwargs):
                calls.append((script, args, kwargs))
                return {"kind": "QueryResponse", "status": 200,
                        "body": '{"errno":0,"result":{"uk":42,"username":"private"}}'}

        self.assertEqual({"kind": "Observed", "stableBaiduUserId": "42"},
                         self.query.observe_identity(Tab(), 0.25))
        script, args, kwargs = calls[0]
        self.assertEqual(self.query.IDENTITY_QUERY_SCRIPT, script)
        self.assertEqual(1, len(args))
        self.assertGreater(args[0], 0)
        self.assertLessEqual(args[0], 250)
        self.assertEqual({"timeout": 0.25}, kwargs)

    def test_wrapper_maps_exceptions_without_exporting_details(self):
        for error, kind in ((TimeoutError("private-page-body"), "TimedOut"),
                            (RuntimeError("private-cookie"), "BrowserUnavailable")):
            class Tab:
                def run_js(inner, *_args, **_kwargs):
                    raise error
            self.assertEqual({"kind": kind}, self.query.observe_identity(Tab(), 1.0))
        for remaining in (0, -1, float("nan"), float("inf"), True, "1"):
            class NeverTab:
                def run_js(inner, *_args, **_kwargs):
                    self.fail("invalid budget must not execute the query")
            result = self.query.observe_identity(NeverTab(), remaining)
            self.assertIn(result, ({"kind": "TimedOut"}, {"kind": "BrowserUnavailable"}))

    def test_actual_production_script_in_offline_node_vm(self):
        node = shutil.which("node") or (r"D:\node.js\node.exe" if Path(r"D:\node.js\node.exe").is_file() else None)
        self.assertIsNotNone(node, "Node is required for offline production-script verification; no skip")
        result = subprocess.run(
            [node, str(Path(__file__).with_name("identity_query_harness.cjs"))],
            input=json.dumps({"script": self.query.IDENTITY_QUERY_SCRIPT}),
            text=True, capture_output=True, timeout=20, check=False)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("offline script assertions passed", result.stdout)


if __name__ == "__main__":
    unittest.main()
