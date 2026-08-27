"""Strict private stdio protocol for the managed browser helper."""

from __future__ import annotations

import ipaddress
import json
import math
import struct
import uuid
from urllib.parse import urlsplit

PROTOCOL_VERSION = 1
EXECUTION_ENGINE_VERSION = "ManagedChromium-v1"
MAX_FRAME_BYTES = 65_536
MAX_OPERATION_TIMEOUT_MILLISECONDS = 120_000
MAX_REQUESTS_PER_SESSION = 1_024
REQUIRED_CAPABILITIES = ("Health", "Shutdown", "ObserveAccountIdentity")
ALLOWED_ACTIONS = ("Handshake",) + REQUIRED_CAPABILITIES
FORBIDDEN_FIELDS = frozenset(
    name.casefold()
    for name in ("cookie", "authorization", "extractionCode", "pageUrl", "pageText", "html")
)
REQUEST_FIELDS = frozenset((
    "protocolVersion", "executionEngineVersion", "correlationId", "operationId",
    "accountId", "generation", "action", "payload",
))
RESPONSE_FIELDS = frozenset((
    "protocolVersion", "executionEngineVersion", "correlationId", "operationId",
    "accountId", "generation", "action", "result",
))


class ProtocolError(Exception):
    """A safe, typed protocol failure that never echoes input."""

    def __init__(self, code: str):
        self.code = code
        super().__init__(code)

    def __str__(self) -> str:
        return f"ManagedBrowserProtocolError:{self.code}"


def _error(code: str) -> ProtocolError:
    return ProtocolError(code)


def _pairs_no_duplicates(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise _error("DuplicateField")
        value[key] = item
    return value


def _reject_constant(_value):
    raise _error("MalformedJson")


def _scan_forbidden(value, depth=1) -> None:
    if depth > 16:
        raise _error("MalformedJson")
    if isinstance(value, dict):
        for key, child in value.items():
            if key.casefold() in FORBIDDEN_FIELDS:
                raise _error("ForbiddenField")
            _scan_forbidden(child, depth + 1)
    elif isinstance(value, list):
        for child in value:
            _scan_forbidden(child, depth + 1)


def _exact_object(value, fields, *, body=False) -> None:
    if not isinstance(value, dict):
        raise _error("InvalidPayload" if body else "InvalidMessage")
    actual = frozenset(value)
    unknown = actual - frozenset(fields)
    if unknown:
        if any(name.casefold() in FORBIDDEN_FIELDS for name in unknown):
            raise _error("ForbiddenField")
        raise _error("UnknownField")
    if actual != frozenset(fields):
        raise _error("InvalidPayload" if body else "InvalidMessage")


def _integer(value, *, positive=True, maximum=(2 ** 63) - 1) -> bool:
    return (
        isinstance(value, int)
        and not isinstance(value, bool)
        and (value > 0 if positive else True)
        and value <= maximum
    )


def _canonical_uuid(value) -> bool:
    if not isinstance(value, str):
        return False
    try:
        parsed = uuid.UUID(value)
    except (ValueError, AttributeError):
        return False
    return parsed.int != 0 and str(parsed) == value


def _bounded_text(value, maximum=256) -> bool:
    if not isinstance(value, str) or not value or value != value.strip():
        return False
    if any(ord(character) <= 0x1f or 0x7f <= ord(character) <= 0x9f for character in value):
        return False
    try:
        return len(value.encode("utf-8", errors="strict")) <= maximum
    except UnicodeEncodeError:
        return False


def _nonce(value) -> bool:
    return (
        isinstance(value, str)
        and len(value) == 64
        and all(character in "0123456789abcdef" for character in value)
    )


def _loopback_endpoint(value) -> bool:
    if not isinstance(value, str):
        return False
    try:
        parsed = urlsplit(value)
        address = ipaddress.ip_address(parsed.hostname or "")
        port = parsed.port
    except (ValueError, TypeError):
        return False
    return (
        parsed.scheme == "http"
        and str(address) == "127.0.0.1"
        and port is not None
        and 1 <= port <= 65535
        and port != 80
        and parsed.username is None
        and parsed.password is None
        and parsed.path == "/"
        and parsed.query == ""
        and parsed.fragment == ""
        and value == f"http://127.0.0.1:{port}/"
    )


def _validate_capabilities(value) -> None:
    if not isinstance(value, list) or not all(isinstance(item, str) for item in value):
        raise _error("InvalidPayload")
    if tuple(value) != REQUIRED_CAPABILITIES:
        raise _error("CapabilityMismatch")


def _validate_common(value, expected_fields) -> str:
    _scan_forbidden(value)
    _exact_object(value, expected_fields)
    if type(value["protocolVersion"]) is not int or value["protocolVersion"] != PROTOCOL_VERSION:
        raise _error("ProtocolVersionMismatch")
    if value["executionEngineVersion"] != EXECUTION_ENGINE_VERSION:
        raise _error("ExecutionEngineVersionMismatch")
    if not all(_canonical_uuid(value[name]) for name in ("correlationId", "operationId", "accountId")):
        raise _error("InvalidMessage")
    if not _integer(value["generation"], maximum=(2 ** 63) - 1):
        raise _error("InvalidMessage")
    action = value["action"]
    if action not in ALLOWED_ACTIONS:
        raise _error("UnknownAction")
    return action


def validate_request(value) -> None:
    action = _validate_common(value, REQUEST_FIELDS)
    payload = value["payload"]
    if action == "Handshake":
        _exact_object(payload, ("nonce", "parentProcessId", "requiredCapabilities"), body=True)
        if not _nonce(payload["nonce"]) or not _integer(payload["parentProcessId"], maximum=(2 ** 31) - 1):
            raise _error("InvalidPayload")
        _validate_capabilities(payload["requiredCapabilities"])
    elif action in ("Health", "Shutdown"):
        _exact_object(payload, (), body=True)
    elif action == "ObserveAccountIdentity":
        _exact_object(
            payload,
            ("browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds"),
            body=True,
        )
        if not _canonical_uuid(payload["browserId"]):
            raise _error("InvalidPayload")
        if not _bounded_text(payload["targetId"]):
            raise _error("InvalidPayload")
        if not _loopback_endpoint(payload["devToolsEndpoint"]):
            raise _error("InvalidPayload")
        timeout = payload["timeoutMilliseconds"]
        if not _integer(timeout, maximum=(2 ** 31) - 1) or timeout > MAX_OPERATION_TIMEOUT_MILLISECONDS:
            raise _error("InvalidPayload")


def validate_response(value) -> None:
    action = _validate_common(value, RESPONSE_FIELDS)
    result = value["result"]
    if action == "Handshake":
        _exact_object(
            result,
            ("kind", "nonce", "parentProcessId", "helperProcessId", "capabilities"),
            body=True,
        )
        if result["kind"] != "HandshakeAccepted":
            raise _error("InvalidPayload")
        if not _nonce(result["nonce"]):
            raise _error("InvalidPayload")
        if (not _integer(result["parentProcessId"], maximum=(2 ** 31) - 1)
                or not _integer(result["helperProcessId"], maximum=(2 ** 31) - 1)):
            raise _error("InvalidPayload")
        _validate_capabilities(result["capabilities"])
    elif action == "Health":
        _exact_object(result, ("kind",), body=True)
        if result["kind"] != "Healthy":
            raise _error("InvalidPayload")
    elif action == "Shutdown":
        _exact_object(result, ("kind",), body=True)
        if result["kind"] != "ShuttingDown":
            raise _error("InvalidPayload")
    elif action == "ObserveAccountIdentity":
        if not isinstance(result, dict) or "kind" not in result:
            raise _error("InvalidPayload")
        kind = result["kind"]
        if kind == "Observed":
            _exact_object(result, ("kind", "stableBaiduUserId"), body=True)
            if not _bounded_text(result["stableBaiduUserId"]):
                raise _error("InvalidPayload")
        elif kind in ("LoginRequired", "BrowserUnavailable", "TimedOut", "Cancelled"):
            _exact_object(result, ("kind",), body=True)
        else:
            raise _error("InvalidPayload")


def response_for(request, result):
    validate_request(request)
    value = {
        "protocolVersion": PROTOCOL_VERSION,
        "executionEngineVersion": EXECUTION_ENGINE_VERSION,
        "correlationId": request["correlationId"],
        "operationId": request["operationId"],
        "accountId": request["accountId"],
        "generation": request["generation"],
        "action": request["action"],
        "result": result,
    }
    validate_response(value)
    return value


def validate_response_for(request, response) -> None:
    validate_request(request)
    validate_response(response)
    for field in ("correlationId", "operationId", "accountId", "generation", "action"):
        if request[field] != response[field]:
            raise _error("ResponseIdentityMismatch")


def _decode_payload(payload, *, response=False):
    try:
        text = payload.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise _error("MalformedUtf8") from exc
    try:
        value = json.loads(
            text,
            object_pairs_hook=_pairs_no_duplicates,
            parse_constant=_reject_constant,
        )
    except ProtocolError:
        raise
    except (json.JSONDecodeError, ValueError, TypeError, OverflowError) as exc:
        raise _error("MalformedJson") from exc
    if not isinstance(value, dict):
        raise _error("InvalidMessage")
    (validate_response if response else validate_request)(value)
    return value


def encode_message(value, *, response=False):
    (validate_response if response else validate_request)(value)
    try:
        payload = json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
        ).encode("utf-8")
    except (TypeError, ValueError, OverflowError) as exc:
        raise _error("InvalidMessage") from exc
    if not 1 <= len(payload) <= MAX_FRAME_BYTES:
        raise _error("FrameSizeInvalid")
    return struct.pack(">I", len(payload)) + payload


def _read_exact(reader, count, is_cancelled, *, clean_eof=False):
    chunks = []
    remaining = count
    while remaining:
        if is_cancelled is not None and is_cancelled():
            raise _error("Cancelled")
        part = reader.read(remaining)
        if not part:
            if clean_eof and remaining == count:
                return None
            raise _error("UnexpectedEof")
        if not isinstance(part, (bytes, bytearray, memoryview)):
            raise _error("InvalidMessage")
        part = bytes(part)
        if len(part) > remaining:
            raise _error("InvalidMessage")
        chunks.append(part)
        remaining -= len(part)
    return b"".join(chunks)


def read_message(reader, *, response=False, is_cancelled=None):
    header = _read_exact(reader, 4, is_cancelled, clean_eof=True)
    if header is None:
        return None
    length = struct.unpack(">I", header)[0]
    if not 1 <= length <= MAX_FRAME_BYTES:
        raise _error("FrameSizeInvalid")
    payload = _read_exact(reader, length, is_cancelled)
    return _decode_payload(payload, response=response)


def write_message(writer, value, *, response=False):
    writer.write(encode_message(value, response=response))
    flush = getattr(writer, "flush", None)
    if flush is not None:
        flush()


def safe_summary(value) -> str:
    try:
        if not isinstance(value, dict):
            raise _error("InvalidMessage")
        if "payload" in value:
            validate_request(value)
        elif "result" in value:
            validate_response(value)
        else:
            raise _error("InvalidMessage")
    except ProtocolError:
        return "ManagedBrowserMessage { Invalid = true, Identities = [REDACTED], Body = [REDACTED] }"
    action = value["action"]
    generation = value["generation"]
    return f"ManagedBrowserMessage {{ Action = {action}, Generation = {generation}, Identities = [REDACTED], Body = [REDACTED] }}"
