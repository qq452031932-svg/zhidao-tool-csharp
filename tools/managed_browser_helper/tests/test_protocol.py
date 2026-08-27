import io
import json
import os
import struct
import sys
import threading
import time
import unittest
import uuid

HELPER_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if HELPER_ROOT not in sys.path:
    sys.path.insert(0, HELPER_ROOT)

import protocol
import main


CORRELATION_ID = "11111111-1111-1111-1111-111111111111"
OPERATION_ID = "22222222-2222-2222-2222-222222222222"
ACCOUNT_ID = "33333333-3333-3333-3333-333333333333"
NONCE = "a" * 64


def request(action="Handshake", payload=None):
    if payload is None:
        payload = {
            "nonce": NONCE,
            "parentProcessId": 1234,
            "requiredCapabilities": list(protocol.REQUIRED_CAPABILITIES),
        }
    return {
        "protocolVersion": protocol.PROTOCOL_VERSION,
        "executionEngineVersion": protocol.EXECUTION_ENGINE_VERSION,
        "correlationId": CORRELATION_ID,
        "operationId": OPERATION_ID,
        "accountId": ACCOUNT_ID,
        "generation": 1,
        "action": action,
        "payload": payload,
    }


def response(action, result):
    value = request(action, {})
    value.pop("payload")
    value["result"] = result
    return value


class FragmentedReader:
    def __init__(self, data, chunks=(1, 2, 3, 5, 8)):
        self._data = data
        self._chunks = chunks
        self._offset = 0
        self._index = 0

    def read(self, size):
        if self._offset >= len(self._data):
            return b""
        chunk = self._chunks[self._index % len(self._chunks)]
        self._index += 1
        count = min(size, chunk, len(self._data) - self._offset)
        result = self._data[self._offset:self._offset + count]
        self._offset += count
        return result


class ProtocolTests(unittest.TestCase):
    def test_entrypoint_disables_bytecode_writes_before_runtime_imports(self):
        self.assertTrue(sys.dont_write_bytecode)

    def test_frame_is_big_endian_utf8_and_round_trips(self):
        encoded = protocol.encode_message(request())
        self.assertEqual(len(encoded) - 4, struct.unpack(">I", encoded[:4])[0])
        self.assertFalse(encoded[4:].startswith(b"\xef\xbb\xbf"))
        self.assertEqual(request(), protocol.read_message(io.BytesIO(encoded)))

    def test_fragmented_and_coalesced_frames_are_read_once(self):
        first = protocol.encode_message(request())
        second = protocol.encode_message(request("Health", {}))
        reader = FragmentedReader(first + second)
        self.assertEqual("Handshake", protocol.read_message(reader)["action"])
        self.assertEqual("Health", protocol.read_message(reader)["action"])
        self.assertIsNone(protocol.read_message(reader))

    def test_clean_eof_differs_from_truncated_header_and_payload(self):
        self.assertIsNone(protocol.read_message(io.BytesIO()))
        with self.assertRaisesRegex(protocol.ProtocolError, "UnexpectedEof"):
            protocol.read_message(io.BytesIO(b"\x00\x00"))
        with self.assertRaisesRegex(protocol.ProtocolError, "UnexpectedEof"):
            protocol.read_message(io.BytesIO(b"\x00\x00\x00\x04{"))

    def test_invalid_frame_size_is_rejected_without_payload_read(self):
        for length in (0, protocol.MAX_FRAME_BYTES + 1):
            with self.subTest(length=length):
                with self.assertRaisesRegex(protocol.ProtocolError, "FrameSizeInvalid"):
                    protocol.read_message(io.BytesIO(struct.pack(">I", length)))

    def test_malformed_utf8_json_duplicate_and_non_finite_numbers_are_rejected(self):
        cases = (
            (b"\xff", "MalformedUtf8"),
            (b"{", "MalformedJson"),
            (b'{"x":1,"x":2}', "DuplicateField"),
            (b'{"x":NaN}', "MalformedJson"),
        )
        for body, code in cases:
            with self.subTest(code=code):
                with self.assertRaisesRegex(protocol.ProtocolError, code):
                    protocol.read_message(io.BytesIO(struct.pack(">I", len(body)) + body))

    def test_json_nesting_is_bounded_consistently_with_csharp(self):
        value = request()
        nested = {}
        value["unexpected"] = nested
        for _ in range(20):
            child = {}
            nested["x"] = child
            nested = child

        body = json.dumps(value, separators=(",", ":")).encode("utf-8")

        with self.assertRaisesRegex(protocol.ProtocolError, "MalformedJson"):
            protocol.read_message(io.BytesIO(struct.pack(">I", len(body)) + body))

    def test_unknown_version_engine_action_and_fields_are_typed_failures(self):
        variants = (
            ({"protocolVersion": 2}, "ProtocolVersionMismatch"),
            ({"protocolVersion": True}, "ProtocolVersionMismatch"),
            ({"protocolVersion": 1.0}, "ProtocolVersionMismatch"),
            ({"executionEngineVersion": "WebView2-v1"}, "ExecutionEngineVersionMismatch"),
            ({"action": "ExecuteJavaScript"}, "UnknownAction"),
            ({"unexpected": True}, "UnknownField"),
        )
        for changes, code in variants:
            with self.subTest(code=code):
                value = request()
                value.update(changes)
                with self.assertRaisesRegex(protocol.ProtocolError, code):
                    protocol.validate_request(value)

    def test_invalid_payload_shapes_share_the_csharp_error_code(self):
        for payload in ([], {"nonce": NONCE, "requiredCapabilities": list(protocol.REQUIRED_CAPABILITIES)}):
            with self.subTest(payload=payload):
                value = request()
                value["payload"] = payload
                with self.assertRaisesRegex(protocol.ProtocolError, "InvalidPayload"):
                    protocol.validate_request(value)

    def test_every_body_field_type_and_format_failure_matches_csharp(self):
        handshake_request = request()
        observe_request = request("ObserveAccountIdentity", {
            "browserId": "44444444-4444-4444-4444-444444444444",
            "targetId": "target-1",
            "devToolsEndpoint": "http://127.0.0.1:43127/",
            "timeoutMilliseconds": 30000,
        })
        handshake_response = response("Handshake", {
            "kind": "HandshakeAccepted",
            "nonce": NONCE,
            "parentProcessId": 1234,
            "helperProcessId": 9876,
            "capabilities": list(protocol.REQUIRED_CAPABILITIES),
        })
        health_response = response("Health", {"kind": "Healthy"})
        shutdown_response = response("Shutdown", {"kind": "ShuttingDown"})
        observed_response = response("ObserveAccountIdentity", {
            "kind": "Observed", "stableBaiduUserId": "stable-uid",
        })
        request_invalid_before_capabilities = json.loads(json.dumps(handshake_request))
        request_invalid_before_capabilities["payload"]["nonce"] = "short"
        response_invalid_before_capabilities = json.loads(json.dumps(handshake_response))
        response_invalid_before_capabilities["result"]["nonce"] = "short"
        cases = (
            ("request nonce type", handshake_request, "payload", "nonce", 1, "InvalidPayload"),
            ("request nonce format", handshake_request, "payload", "nonce", "short", "InvalidPayload"),
            ("request parent pid type", handshake_request, "payload", "parentProcessId", "1234", "InvalidPayload"),
            ("request parent pid format", handshake_request, "payload", "parentProcessId", 0, "InvalidPayload"),
            ("request capabilities type", handshake_request, "payload", "requiredCapabilities", "Health", "InvalidPayload"),
            ("request capabilities item type", handshake_request, "payload", "requiredCapabilities", ["Health", 1, "ObserveAccountIdentity"], "InvalidPayload"),
            ("request capabilities set", handshake_request, "payload", "requiredCapabilities", ["Health", "Shutdown"], "CapabilityMismatch"),
            ("request capabilities order", handshake_request, "payload", "requiredCapabilities", ["Shutdown", "Health", "ObserveAccountIdentity"], "CapabilityMismatch"),
            ("request invalid schema precedes capability mismatch", request_invalid_before_capabilities, "payload", "requiredCapabilities", ["Health", "Shutdown"], "InvalidPayload"),
            ("request browser id type", observe_request, "payload", "browserId", 1, "InvalidPayload"),
            ("request browser id format", observe_request, "payload", "browserId", "not-a-guid", "InvalidPayload"),
            ("request target id type", observe_request, "payload", "targetId", 1, "InvalidPayload"),
            ("request target id format", observe_request, "payload", "targetId", "", "InvalidPayload"),
            ("request endpoint type", observe_request, "payload", "devToolsEndpoint", 1, "InvalidPayload"),
            ("request endpoint format", observe_request, "payload", "devToolsEndpoint", "http://localhost:43127/", "InvalidPayload"),
            ("request timeout type", observe_request, "payload", "timeoutMilliseconds", "1", "InvalidPayload"),
            ("request timeout numeric type", observe_request, "payload", "timeoutMilliseconds", 1.0, "InvalidPayload"),
            ("request timeout format", observe_request, "payload", "timeoutMilliseconds", 0, "InvalidPayload"),
            ("response handshake kind type", handshake_response, "result", "kind", 1, "InvalidPayload"),
            ("response handshake kind format", handshake_response, "result", "kind", "Healthy", "InvalidPayload"),
            ("response nonce type", handshake_response, "result", "nonce", 1, "InvalidPayload"),
            ("response nonce format", handshake_response, "result", "nonce", "short", "InvalidPayload"),
            ("response parent pid type", handshake_response, "result", "parentProcessId", "1234", "InvalidPayload"),
            ("response parent pid format", handshake_response, "result", "parentProcessId", 0, "InvalidPayload"),
            ("response helper pid type", handshake_response, "result", "helperProcessId", "9876", "InvalidPayload"),
            ("response helper pid format", handshake_response, "result", "helperProcessId", 0, "InvalidPayload"),
            ("response capabilities type", handshake_response, "result", "capabilities", "Health", "InvalidPayload"),
            ("response capabilities item type", handshake_response, "result", "capabilities", ["Health", 1, "ObserveAccountIdentity"], "InvalidPayload"),
            ("response capabilities set", handshake_response, "result", "capabilities", ["Health", "Shutdown"], "CapabilityMismatch"),
            ("response capabilities order", handshake_response, "result", "capabilities", ["Shutdown", "Health", "ObserveAccountIdentity"], "CapabilityMismatch"),
            ("response invalid schema precedes capability mismatch", response_invalid_before_capabilities, "result", "capabilities", ["Health", "Shutdown"], "InvalidPayload"),
            ("response health kind type", health_response, "result", "kind", 1, "InvalidPayload"),
            ("response health kind format", health_response, "result", "kind", "ShuttingDown", "InvalidPayload"),
            ("response shutdown kind type", shutdown_response, "result", "kind", 1, "InvalidPayload"),
            ("response shutdown kind format", shutdown_response, "result", "kind", "Healthy", "InvalidPayload"),
            ("response observed kind type", observed_response, "result", "kind", 1, "InvalidPayload"),
            ("response observed kind format", observed_response, "result", "kind", "Unknown", "InvalidPayload"),
            ("response uid type", observed_response, "result", "stableBaiduUserId", 1, "InvalidPayload"),
            ("response uid format", observed_response, "result", "stableBaiduUserId", "", "InvalidPayload"),
        )

        for name, template, body_name, field, invalid_value, expected in cases:
            with self.subTest(name=name):
                value = json.loads(json.dumps(template))
                value[body_name][field] = invalid_value
                validator = protocol.validate_response if body_name == "result" else protocol.validate_request
                with self.assertRaises(protocol.ProtocolError) as caught:
                    validator(value)
                self.assertEqual(expected, caught.exception.code)

    def test_secret_like_keys_are_rejected_recursively_without_echo(self):
        for key in ("cookie", "Authorization", "extractionCode"):
            with self.subTest(key=key):
                value = request()
                value["payload"][key] = "do-not-echo"
                with self.assertRaises(protocol.ProtocolError) as caught:
                    protocol.validate_request(value)
                self.assertEqual("ForbiddenField", caught.exception.code)
                self.assertNotIn("do-not-echo", str(caught.exception))

    def test_handshake_capability_set_is_fixed(self):
        value = request()
        value["payload"]["requiredCapabilities"] = ["ExecuteJavaScript"]
        with self.assertRaisesRegex(protocol.ProtocolError, "CapabilityMismatch"):
            protocol.validate_request(value)

    def test_observe_identity_request_and_result_are_strict(self):
        observe = request("ObserveAccountIdentity", {
            "browserId": "44444444-4444-4444-4444-444444444444",
            "targetId": "target-1",
            "devToolsEndpoint": "http://127.0.0.1:43127/",
            "timeoutMilliseconds": 30000,
        })
        protocol.validate_request(observe)
        for field in ("targetId", "browserId"):
            invalid = json.loads(json.dumps(observe))
            invalid["payload"][field] = ""
            with self.assertRaises(protocol.ProtocolError):
                protocol.validate_request(invalid)

        observed = protocol.response_for(observe, {
            "kind": "Observed", "stableBaiduUserId": "stable-secret-uid"
        })
        protocol.validate_response(observed)
        self.assertNotIn("stable-secret-uid", protocol.safe_summary(observed))
        leaked = json.loads(json.dumps(observed))
        leaked["result"]["pageUrl"] = "https://pan.baidu.com/private"
        with self.assertRaisesRegex(protocol.ProtocolError, "ForbiddenField"):
            protocol.validate_response(leaked)

    def test_text_and_endpoint_negative_corpus_matches_csharp(self):
        base = request("ObserveAccountIdentity", {
            "browserId": "44444444-4444-4444-4444-444444444444",
            "targetId": "😀" * 64,
            "devToolsEndpoint": "http://127.0.0.1:43127/",
            "timeoutMilliseconds": 1,
        })
        protocol.validate_request(base)
        for target in ("😀" * 65, "target\u0085id", "target\x00id"):
            with self.subTest(target=repr(target)):
                invalid = json.loads(json.dumps(base))
                invalid["payload"]["targetId"] = target
                with self.assertRaisesRegex(protocol.ProtocolError, "InvalidPayload"):
                    protocol.validate_request(invalid)
        for endpoint in (
            "http://127.0.0.1/",
            "http://127.0.0.1:80/",
            "http://[::1]:43127/",
            "http://localhost:43127/",
            "HTTP://127.0.0.1:43127/",
            "http://127.000.000.001:43127/",
        ):
            with self.subTest(endpoint=endpoint):
                invalid = json.loads(json.dumps(base))
                invalid["payload"]["devToolsEndpoint"] = endpoint
                with self.assertRaisesRegex(protocol.ProtocolError, "InvalidPayload"):
                    protocol.validate_request(invalid)

    def test_only_exact_https_top_level_routes_are_allowed(self):
        self.assertEqual("PanRoot", main.classify_route("https://pan.baidu.com/"))
        self.assertEqual("PassportRoot", main.classify_route("https://passport.baidu.com/"))
        for value in (
            "http://pan.baidu.com/",
            "https://user@pan.baidu.com/",
            "https://pan.baidu.com:444/",
            "https://sub.pan.baidu.com/",
            "https://pan.baidu.com.evil.test/",
            "https://pan.baidu.com/disk/home",
            "https://pan.baidu.com/?query=1",
        ):
            with self.subTest(value=value):
                self.assertEqual("Rejected", main.classify_route(value))

    def test_identity_html_mapping_returns_only_typed_result(self):
        logged_out = '<html><body data-baidu-login-state="logged-out"></body></html>'
        logged_in = '<html><body><i data-baidu-stable-uid="uid-123"></i></body></html>'
        missing = '<html><body data-baidu-login-state="logged-in"></body></html>'
        changed = '<html><body><i data-baidu-stable-uid="uid-other"></i></body></html>'

        self.assertEqual({"kind": "LoginRequired"}, main.map_identity_fixture(logged_out))
        self.assertEqual(
            {"kind": "Observed", "stableBaiduUserId": "uid-123"},
            main.map_identity_fixture(logged_in))
        self.assertEqual({"kind": "BrowserUnavailable"}, main.map_identity_fixture(missing))
        self.assertEqual(
            {"kind": "Observed", "stableBaiduUserId": "uid-other"},
            main.map_identity_fixture(changed))
        self.assertNotIn("html", json.dumps(main.map_identity_fixture(logged_in)).lower())

    def test_bound_observer_navigates_only_to_pan_root_and_observes_only_there(self):
        payload = request("ObserveAccountIdentity", {
            "browserId": "44444444-4444-4444-4444-444444444444",
            "targetId": "target-1",
            "devToolsEndpoint": "http://127.0.0.1:43127/",
            "timeoutMilliseconds": 30000,
        })["payload"]
        target = FakeTarget(
            "target-1",
            ["https://evil.test/", "https://passport.baidu.com/", "https://pan.baidu.com/"],
            {"kind": "Observed", "stableBaiduUserId": "uid-123"},
        )
        connector = FakeConnector(payload["browserId"], target)

        result = main.observe_bound_target(
            payload, connector, monotonic=lambda: 0.0, sleeper=lambda _: target.advance())

        self.assertEqual({"kind": "Observed", "stableBaiduUserId": "uid-123"}, result)
        self.assertEqual(["https://pan.baidu.com/"], target.navigations)
        self.assertEqual(1, target.identity_reads)

    def test_bound_observer_waits_through_manual_passport_login_before_observing_again(self):
        payload = request("ObserveAccountIdentity", {
            "browserId": "44444444-4444-4444-4444-444444444444",
            "targetId": "target-1",
            "devToolsEndpoint": "http://127.0.0.1:43127/",
            "timeoutMilliseconds": 30000,
        })["payload"]
        target = FakeTarget(
            "target-1",
            ["https://pan.baidu.com/", "https://passport.baidu.com/", "https://pan.baidu.com/"],
            [
                {"kind": "LoginRequired"},
                {"kind": "Observed", "stableBaiduUserId": "uid-123"},
            ],
        )

        result = main.observe_bound_target(
            payload,
            FakeConnector(payload["browserId"], target),
            monotonic=lambda: 0.0,
            sleeper=lambda _: target.advance(),
        )

        self.assertEqual({"kind": "Observed", "stableBaiduUserId": "uid-123"}, result)
        self.assertEqual(2, target.identity_reads)

    def test_bound_observer_rejects_changed_binding_disconnect_timeout_and_cancellation(self):
        payload = request("ObserveAccountIdentity", {
            "browserId": "44444444-4444-4444-4444-444444444444",
            "targetId": "target-1",
            "devToolsEndpoint": "http://127.0.0.1:43127/",
            "timeoutMilliseconds": 1,
        })["payload"]
        passport = FakeTarget("target-1", ["https://passport.baidu.com/"], {"kind": "Observed", "stableBaiduUserId": "never"})

        wrong_browser = main.observe_bound_target(
            payload, FakeConnector(str(uuid.uuid4()), passport), monotonic=lambda: 0.0, sleeper=lambda _: None)
        wrong_target = main.observe_bound_target(
            payload, FakeConnector(payload["browserId"], FakeTarget("other", ["https://pan.baidu.com/"], {"kind": "LoginRequired"})),
            monotonic=lambda: 0.0, sleeper=lambda _: None)
        disconnected = main.observe_bound_target(
            payload, FakeConnector(payload["browserId"], None), monotonic=lambda: 0.0, sleeper=lambda _: None)
        times = iter((0.0, 0.002))
        timed_out = main.observe_bound_target(
            payload, FakeConnector(payload["browserId"], passport), monotonic=lambda: next(times), sleeper=lambda _: None)
        cancelled = main.observe_bound_target(
            payload, FakeConnector(payload["browserId"], passport), monotonic=lambda: 0.0,
            sleeper=lambda _: None, is_cancelled=lambda: True)

        self.assertEqual({"kind": "BrowserUnavailable"}, wrong_browser)
        self.assertEqual({"kind": "BrowserUnavailable"}, wrong_target)
        self.assertEqual({"kind": "BrowserUnavailable"}, disconnected)
        self.assertEqual({"kind": "TimedOut"}, timed_out)
        self.assertEqual({"kind": "Cancelled"}, cancelled)
        self.assertEqual(0, passport.identity_reads)

    def test_soft_deadline_rechecks_after_slow_sync_identity_call_and_discards_observed(self):
        payload = request("ObserveAccountIdentity", {
            "browserId": "44444444-4444-4444-4444-444444444444",
            "targetId": "target-1",
            "devToolsEndpoint": "http://127.0.0.1:43127/",
            "timeoutMilliseconds": 1,
        })["payload"]
        target = FakeTarget(
            "target-1", ["https://pan.baidu.com/"],
            {"kind": "Observed", "stableBaiduUserId": "must-be-discarded"},
            identity_delay=0.05,
        )

        result = main.observe_bound_target(
            payload, FakeConnector(payload["browserId"], target))

        self.assertEqual({"kind": "TimedOut"}, result)

    def test_drission_connector_uses_actual_browser_id_not_payload_value(self):
        actual = "44444444-4444-4444-4444-444444444444"
        supplied = "55555555-5555-5555-5555-555555555555"
        connector = main._DrissionConnector(FakeChromium(actual))

        self.assertEqual(actual, connector.browser_id)
        payload = request("ObserveAccountIdentity", {
            "browserId": supplied,
            "targetId": "target-1",
            "devToolsEndpoint": "http://127.0.0.1:43127/",
            "timeoutMilliseconds": 1,
        })["payload"]
        self.assertEqual(
            {"kind": "BrowserUnavailable"},
            main.observe_bound_target(payload, connector, monotonic=lambda: 0.0, sleeper=lambda _: None),
        )

    def test_drission_navigation_disables_driver_retries_and_intervals(self):
        tab = FakeDrissionTab()

        main._DrissionTarget(tab).navigate(main.PAN_ROOT, 0.125)

        self.assertEqual(
            [(main.PAN_ROOT, {"timeout": 0.125, "retry": 0, "interval": 0})],
            tab.get_calls,
        )

    def test_server_requires_handshake_rejects_duplicate_ids_and_shuts_down(self):
        health = {**request("Health", {}), "correlationId": str(uuid.uuid4())}
        incoming = b"".join((
            protocol.encode_message(request()),
            protocol.encode_message(health),
            protocol.encode_message({**request("Shutdown", {}), "correlationId": str(uuid.uuid4())}),
        ))
        output = io.BytesIO()
        events = io.StringIO()

        exit_code = main.run(io.BytesIO(incoming), output, events, helper_pid=9876, parent_pid=1234)

        self.assertEqual(0, exit_code)
        output.seek(0)
        handshake = protocol.read_message(output, response=True)
        health = protocol.read_message(output, response=True)
        shutdown = protocol.read_message(output, response=True)
        self.assertEqual("HandshakeAccepted", handshake["result"]["kind"])
        self.assertEqual(9876, handshake["result"]["helperProcessId"])
        self.assertEqual("Healthy", health["result"]["kind"])
        self.assertEqual("ShuttingDown", shutdown["result"]["kind"])
        self.assertEqual("", events.getvalue())

    def test_handshake_rejects_wrong_parent_pid_without_echoing_authentication(self):
        output = io.BytesIO()
        events = io.StringIO()

        exit_code = main.run(
            io.BytesIO(protocol.encode_message(request())), output, events,
            helper_pid=9876, parent_pid=4321)

        self.assertEqual(2, exit_code)
        self.assertEqual(b"", output.getvalue())
        self.assertEqual("HELPER_PROTOCOL_ERROR\n", events.getvalue())

    def test_correlation_tracking_is_bounded_by_fixed_session_request_limit(self):
        health = {**request("Health", {}), "correlationId": str(uuid.uuid4())}
        incoming = protocol.encode_message(request()) + protocol.encode_message(health)
        output = io.BytesIO()
        events = io.StringIO()

        exit_code = main.run(
            io.BytesIO(incoming), output, events, helper_pid=9876, parent_pid=1234,
            max_requests=2)

        self.assertEqual(0, exit_code)
        output.seek(0)
        self.assertEqual("HandshakeAccepted", protocol.read_message(output, response=True)["result"]["kind"])
        self.assertEqual("Healthy", protocol.read_message(output, response=True)["result"]["kind"])
        self.assertIsNone(protocol.read_message(output, response=True))
        self.assertEqual("HELPER_SESSION_LIMIT_REACHED\n", events.getvalue())
        self.assertEqual(1024, protocol.MAX_REQUESTS_PER_SESSION)

    def test_safe_summary_never_echoes_unvalidated_action_or_generation(self):
        invalid = {"action": "secret-action", "generation": "secret-generation"}
        self.assertEqual(
            "ManagedBrowserMessage { Invalid = true, Identities = [REDACTED], Body = [REDACTED] }",
            protocol.safe_summary(invalid),
        )
        self.assertNotIn("secret", protocol.safe_summary(invalid))

    def test_server_protocol_failure_emits_only_safe_event_code(self):
        incoming = protocol.encode_message(request("Health", {}))
        output = io.BytesIO()
        events = io.StringIO()

        exit_code = main.run(io.BytesIO(incoming), output, events, helper_pid=9876, parent_pid=1234)

        self.assertEqual(2, exit_code)
        self.assertEqual(b"", output.getvalue())
        self.assertEqual("HELPER_PROTOCOL_ERROR\n", events.getvalue())

    def test_read_cancellation_is_checked_between_fragments(self):
        cancelled = threading.Event()

        class CancellingReader(FragmentedReader):
            def read(self, size):
                part = super().read(size)
                cancelled.set()
                return part

        with self.assertRaisesRegex(protocol.ProtocolError, "Cancelled"):
            protocol.read_message(
                CancellingReader(protocol.encode_message(request())),
                is_cancelled=cancelled.is_set)


class FakeTarget:
    def __init__(self, target_id, routes, identity_result, identity_delay=0.0):
        self.target_id = target_id
        self._routes = list(routes)
        self._route_index = 0
        self._identity_results = list(identity_result) if isinstance(identity_result, list) else [identity_result]
        self.navigations = []
        self.identity_reads = 0
        self.identity_delay = identity_delay

    def current_url(self, _remaining_seconds=None):
        return self._routes[self._route_index]

    def navigate(self, value, _remaining_seconds=None):
        self.navigations.append(value)
        try:
            self._route_index = self._routes.index(value)
        except ValueError:
            self._routes.append(value)
            self._route_index = len(self._routes) - 1

    def advance(self):
        if self._route_index + 1 < len(self._routes):
            self._route_index += 1

    def observe_identity(self, _remaining_seconds=None):
        self.identity_reads += 1
        if self.identity_delay:
            time.sleep(self.identity_delay)
        index = min(self.identity_reads - 1, len(self._identity_results) - 1)
        return self._identity_results[index]


class FakeConnector:
    def __init__(self, browser_id, target):
        self.browser_id = browser_id
        self._target = target

    def get_target(self, target_id, _remaining_seconds=None):
        return self._target


class FakeChromium:
    def __init__(self, browser_id):
        self.id = browser_id
        self.tab_ids = []


class FakeDrissionTab:
    tab_id = "target-1"

    def __init__(self):
        self.get_calls = []

    def get(self, value, **kwargs):
        self.get_calls.append((value, kwargs))


if __name__ == "__main__":
    unittest.main()
