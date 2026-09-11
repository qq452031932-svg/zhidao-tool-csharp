"""Strict private stdio protocol for the managed browser helper."""

from __future__ import annotations

import ipaddress
import hashlib
import json
import math
import re
import struct
import uuid
from urllib.parse import urlsplit

PROTOCOL_VERSION = 3
EXECUTION_ENGINE_VERSION = "ManagedChromium-v3"
MAX_FRAME_BYTES = 1_048_576
MAX_TARGET_ENTER_FRAME_BYTES = 65_535
MAX_TARGET_CANONICAL_PATH_BYTES = 4_096
MAX_OPERATION_TIMEOUT_MILLISECONDS = 120_000
MAX_REQUESTS_PER_SESSION = 1_024
REQUIRED_CAPABILITIES = ("Health", "Shutdown", "ObserveAccountIdentity", "OpenTransferSource", "ReadTransferSource", "ReadTransferDirectory", "OpenTransferPicker", "OpenTransferDirectoryRoot", "EnterTransferDirectory", "BeginCreateTransferDirectory", "ConfirmCreateTransferDirectory", "CancelTransferPicker", "ObserveTransferPickerClosed", "OpenTransferTargetRoot", "EnterTransferTargetDirectory", "ReadTransferTargetDirectory", "OpenTransferSourceAfterTargetRead", "ObserveTransferSubmissionReady", "SubmitTransferOnce", "ObserveTransferSubmissionOutcome", "ReadShareOfficialListing", "SubmitShareOnce", "ObserveShareSubmissionOutcome", "SubmitExtractionCode")
TARGET_BASELINE_ACTIONS = ("OpenTransferTargetRoot", "EnterTransferTargetDirectory",
                           "ReadTransferTargetDirectory", "OpenTransferSourceAfterTargetRead")
ALLOWED_ACTIONS = ("Handshake",) + REQUIRED_CAPABILITIES
TRANSFER_SUBMISSION_ACTIONS = ("ObserveTransferSubmissionReady", "SubmitTransferOnce",
                               "ObserveTransferSubmissionOutcome")
SHARE_SUBMISSION_ACTIONS = ("ReadShareOfficialListing", "SubmitShareOnce",
                            "ObserveShareSubmissionOutcome")
SESSION_OPERATION_ACTIONS = ("InspectSource", "EnsureDirectory", "SubmitTransfer",
                             "ObserveTransfer", "SubmitShare", "ObserveShare")
SESSION_OPERATION_FIELDS = ("operation", "operationId", "attemptOrdinal", "binding", "arguments")
SESSION_OPERATION_BINDING_FIELDS = ("browserId", "targetId", "devToolsEndpoint",
                                    "timeoutMilliseconds", "baiduUserId")
SESSION_OPERATION_ARGUMENT_FIELDS = {
    "InspectSource": ("sourceUri",),
    "EnsureDirectory": ("targetCanonicalPath",),
    "SubmitTransfer": ("sourceUri", "targetCanonicalPath", "duplicatePolicy"),
    "ObserveTransfer": ("adapterRequestId",),
    "SubmitShare": ("exactItemIds", "fixedCode"),
    "ObserveShare": ("adapterRequestId",),
}
SESSION_OPERATION_FORBIDDEN_FIELDS = frozenset(
    name.casefold() for name in
    ("script", "selector", "cookie", "authorization", "token", "origin", "referer",
     "url", "verb", "pageUrl", "pageText", "html", "buttonText", "toast")
)
EXTRACTION_REQUEST_FIELDS = ("browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds",
                             "sourceUri", "expectedBaiduUserId", "fixedCode")
EXTRACTION_RESULT_KINDS = ("SourceOpened", "ExplicitlyRejected", "PageNotValidated",
                           "AccountIdentityMismatch", "LoginRequired", "VerificationRequired",
                           "BrowserUnavailable", "TimedOut", "Cancelled", "OutcomeUnknown")
EXTRACTION_DISPATCH_STATES = ("KnownNotDispatched", "ClickDispatched", "ClickOutcomeUnknown")
SOURCE_RESULT_KINDS = ("SourceOpened", "ExtractionRequired", "LoginRequired", "VerificationRequired",
                       "PageNotValidated", "BrowserUnavailable", "AccountIdentityMismatch", "TimedOut", "Cancelled")
SOURCE_LISTING_DENIAL_KINDS = SOURCE_RESULT_KINDS[1:]
SOURCE_LISTING_FIELDS = ("kind", "baiduUserId", "generationToken", "totalCount", "reachedEnd", "isStable", "items")
DIRECTORY_LISTING_DENIAL_KINDS = tuple(kind for kind in SOURCE_LISTING_DENIAL_KINDS if kind != "ExtractionRequired")
DIRECTORY_LISTING_FIELDS = SOURCE_LISTING_FIELDS + ("parentExactId", "canonicalPath", "ancestorExactIds")
PICKER_RESULT_KINDS = ("PickerOpened", "PageNotValidated", "AccountIdentityMismatch", "LoginRequired",
                      "VerificationRequired", "BrowserUnavailable", "TimedOut", "Cancelled", "OutcomeUnknown")
DIRECTORY_MUTATION_ACTIONS = ("OpenTransferDirectoryRoot", "EnterTransferDirectory", "BeginCreateTransferDirectory",
                              "ConfirmCreateTransferDirectory", "CancelTransferPicker")
DIRECTORY_MUTATION_DENIAL_KINDS = ("PageNotValidated", "AccountIdentityMismatch", "LoginRequired", "VerificationRequired",
                                  "BrowserUnavailable", "TimedOut", "Cancelled", "OutcomeUnknown")
DIRECTORY_MUTATION_SUCCESS_KINDS = dict(zip(DIRECTORY_MUTATION_ACTIONS,
    ("RootOpened", "DirectoryEntered", "CreateDialogOpened", "DirectoryCreated", "PickerCancelled")))
PICKER_CLOSED_RESULT_KINDS = ("PickerClosed", "PageNotValidated", "AccountIdentityMismatch", "LoginRequired",
                              "VerificationRequired", "BrowserUnavailable", "TimedOut", "Cancelled")
TARGET_BINDING_FIELDS = ("browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds",
                         "sourceUri", "expectedBaiduUserId", "expectedSource")
TARGET_HEADER_FIELDS = ("scope", "browserId", "targetId", "devToolsEndpoint", "baiduUserId",
                        "generationToken", "directoryExactId", "canonicalPath", "ancestorExactIds",
                        "totalCount", "reachedEnd", "isStable", "enumerationStepCount",
                        "parentObservationFingerprint")
TARGET_INVENTORY_FIELDS = ("kind", "scope", "browserId", "targetId", "devToolsEndpoint", "baiduUserId",
                           "generationToken", "directoryExactId", "canonicalPath", "ancestorExactIds",
                           "totalCount", "reachedEnd", "isStable", "enumerationStepCount", "items",
                           "observationFingerprint")
TARGET_DENIAL_KINDS = ("PageNotValidated", "AccountIdentityMismatch", "LoginRequired", "VerificationRequired",
                       "BrowserUnavailable", "TimedOut", "Cancelled")
TARGET_NAVIGATION_SUCCESSES = {"OpenTransferTargetRoot": "RootOpened",
                               "EnterTransferTargetDirectory": "DirectoryEntered",
                               "OpenTransferSourceAfterTargetRead": "SourceRestored"}
TRANSFER_SUBMISSION_CONTEXT_FIELDS = (
    "browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri", "baiduUserId",
    "sourceGenerationToken", "sourceObservationFingerprint", "sourceFingerprint",
    "sourceExactId", "sourceItemKind", "selectedTargetExactId",
    "selectedTargetCanonicalPath", "targetBaselineFingerprint", "duplicatePolicy",
)
TRANSFER_READINESS_DENIAL_KINDS = ("PageNotValidated", "AccountIdentityMismatch", "LoginRequired",
                                   "VerificationRequired", "BrowserUnavailable", "TimedOut", "Cancelled")
TRANSFER_OUTCOME_KINDS = ("AcceptedBound", "UnboundAcknowledgement", "ExplicitlyRejected", "Conflict",
                          "PageNotValidated", "AccountIdentityMismatch", "LoginRequired",
                          "VerificationRequired", "BrowserUnavailable", "TimedOut", "Cancelled")
SHARE_SUBMISSION_CONTEXT_FIELDS = (
    "browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "baiduUserId",
    "targetDirectoryExactId", "targetCanonicalPath", "exactItemIds", "exactItemSetFingerprint",
    "codeMode", "fixedCode",
)
SHARE_LISTING_DENIAL_KINDS = ("PageNotValidated", "AccountIdentityMismatch", "LoginRequired",
                              "VerificationRequired", "BrowserUnavailable", "TimedOut", "Cancelled")
SHARE_OUTCOME_KINDS = TRANSFER_OUTCOME_KINDS
FORBIDDEN_FIELDS = frozenset(
    name.casefold()
    for name in ("cookie", "authorization", "extractionCode", "pageUrl", "pageText", "html",
                 "selector", "script", "url", "buttonText", "name", "toast")
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


def _scan_session_operation_forbidden(value, depth=1) -> None:
    if depth > 16:
        raise _error("MalformedJson")
    if isinstance(value, dict):
        for key, child in value.items():
            if key.casefold() in SESSION_OPERATION_FORBIDDEN_FIELDS:
                raise _error("ForbiddenField")
            _scan_session_operation_forbidden(child, depth + 1)
    elif isinstance(value, list):
        for child in value:
            _scan_session_operation_forbidden(child, depth + 1)


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


def is_canonical_source_uri(value) -> bool:
    """A narrowly approved navigation parameter, never a general URL capability."""
    return (type(value) is str and
            re.fullmatch(r"https://pan\.baidu\.com/s/[A-Za-z0-9_-]{1,128}(?:\?pwd=[A-Za-z0-9]{4})?", value) is not None)


def validate_session_operation_request(value):
    """Validate one named session operation; transport details remain helper-owned."""
    _scan_session_operation_forbidden(value)
    _exact_object(value, SESSION_OPERATION_FIELDS, body=True)
    operation = value.get("operation")
    if type(operation) is not str or operation not in SESSION_OPERATION_ACTIONS:
        raise _error("UnknownAction")
    if not _canonical_uuid(value.get("operationId")):
        raise _error("InvalidPayload")
    attempt = value.get("attemptOrdinal")
    if not _integer(attempt, maximum=3):
        raise _error("InvalidPayload")

    binding = value.get("binding")
    _exact_object(binding, SESSION_OPERATION_BINDING_FIELDS, body=True)
    _validate_binding_payload(binding)
    if not _bounded_text(binding.get("baiduUserId")):
        raise _error("InvalidPayload")

    arguments = value.get("arguments")
    _exact_object(arguments, SESSION_OPERATION_ARGUMENT_FIELDS[operation], body=True)
    if operation == "InspectSource":
        if not is_canonical_source_uri(arguments["sourceUri"]):
            raise _error("InvalidPayload")
    elif operation == "EnsureDirectory":
        if not _target_canonical_path(arguments["targetCanonicalPath"]):
            raise _error("InvalidPayload")
    elif operation == "SubmitTransfer":
        if (not is_canonical_source_uri(arguments["sourceUri"]) or
                not _target_canonical_path(arguments["targetCanonicalPath"]) or
                arguments["duplicatePolicy"] != "Rename"):
            raise _error("InvalidPayload")
    elif operation in ("ObserveTransfer", "ObserveShare"):
        if not _bounded_text(arguments["adapterRequestId"]):
            raise _error("InvalidPayload")
    elif operation == "SubmitShare":
        item_ids = arguments["exactItemIds"]
        code = arguments["fixedCode"]
        if (type(item_ids) is not list or not 1 <= len(item_ids) <= 10_000 or
                not all(_bounded_text(item) for item in item_ids) or len(set(item_ids)) != len(item_ids) or
                type(code) is not str or re.fullmatch(r"[a-z0-9]{4}", code) is None):
            raise _error("InvalidPayload")
    return value


def validate_submit_extraction_code_payload(payload) -> None:
    _exact_object(payload, EXTRACTION_REQUEST_FIELDS, body=True)
    validate_open_transfer_source_payload({
        key: payload[key] for key in EXTRACTION_REQUEST_FIELDS if key != "fixedCode"
    })
    if type(payload["fixedCode"]) is not str or re.fullmatch(r"[A-Za-z0-9]{4}", payload["fixedCode"]) is None:
        raise _error("InvalidPayload")


def validate_open_transfer_source_payload(payload) -> None:
    _scan_forbidden(payload)
    _exact_object(payload, ("browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds",
                            "sourceUri", "expectedBaiduUserId"), body=True)
    _validate_binding_payload(payload)
    if not is_canonical_source_uri(payload["sourceUri"]) or not _bounded_text(payload["expectedBaiduUserId"]):
        raise _error("InvalidPayload")


def _validate_binding_payload(payload) -> None:
    if not _canonical_uuid(payload["browserId"]):
        raise _error("InvalidPayload")
    if not _bounded_text(payload["targetId"]):
        raise _error("InvalidPayload")
    if not _loopback_endpoint(payload["devToolsEndpoint"]):
        raise _error("InvalidPayload")
    timeout = payload["timeoutMilliseconds"]
    if not _integer(timeout, maximum=(2 ** 31) - 1) or timeout > MAX_OPERATION_TIMEOUT_MILLISECONDS:
        raise _error("InvalidPayload")


def _source_server_name(value) -> bool:
    # Names are exact evidence: preserve leading/trailing spaces, without normalizing.
    if type(value) is not str or not value or len(value) > 256:
        return False
    if any(ord(character) <= 0x1f or 0x7f <= ord(character) <= 0x9f for character in value):
        return False
    try:
        return len(value.encode("utf-8", errors="strict")) <= 256
    except UnicodeEncodeError:
        return False


def validate_read_transfer_source_result(result, *, require_fingerprint=True) -> None:
    """Validate both fixed-script evidence and its strictly bounded wire representation."""
    if type(result) is not dict or type(result.get("kind")) is not str:
        raise _error("InvalidPayload")
    if result["kind"] in SOURCE_LISTING_DENIAL_KINDS:
        _exact_object(result, ("kind",), body=True)
        return
    if result["kind"] != "Observed":
        raise _error("InvalidPayload")
    fields = SOURCE_LISTING_FIELDS + (("observationFingerprint",) if require_fingerprint else ())
    _exact_object(result, fields, body=True)
    if not _bounded_text(result["baiduUserId"]) or not _bounded_text(result["generationToken"]):
        raise _error("InvalidPayload")
    items = result["items"]
    if type(items) is not list or not 1 <= len(items) <= 32:
        raise _error("InvalidPayload")
    if (type(result["totalCount"]) is not int or result["totalCount"] != len(items) or
            result["reachedEnd"] is not True or result["isStable"] is not True):
        raise _error("InvalidPayload")
    seen = set()
    for item in items:
        _exact_object(item, ("exactId", "kind", "serverName"), body=True)
        exact_id = item["exactId"]
        if (type(exact_id) is not str or re.fullmatch(r"[A-Za-z0-9_-]{1,128}", exact_id) is None or
                item["kind"] not in ("File", "Folder") or not _source_server_name(item["serverName"]) or
                exact_id in seen):
            raise _error("InvalidPayload")
        seen.add(exact_id)
    if require_fingerprint and not _nonce(result["observationFingerprint"]):
        raise _error("InvalidPayload")


def _directory_exact_id(value):
    return type(value) is str and re.fullmatch(r"[A-Za-z0-9_-]{1,128}", value) is not None


def validate_open_transfer_picker_payload(payload):
    _scan_forbidden(payload)
    source_fields = ("browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri", "expectedBaiduUserId")
    _exact_object(payload, source_fields + ("expectedSource",), body=True)
    validate_open_transfer_source_payload({field: payload[field] for field in source_fields})
    source = payload["expectedSource"]
    validate_read_transfer_source_result(source)
    if source["kind"] != "Observed" or source["baiduUserId"] != payload["expectedBaiduUserId"]:
        raise _error("InvalidPayload")
    # Delayed source-only import avoids a module initialization cycle. It never loads a driver.
    from transfer_source_listing import compute_source_fingerprint
    raw_source = {key: value for key, value in source.items() if key != "observationFingerprint"}
    if compute_source_fingerprint(payload["sourceUri"], raw_source) != source["observationFingerprint"]:
        raise _error("InvalidPayload")


def validate_open_transfer_picker_result(result):
    _exact_object(result, ("kind",), body=True)
    if type(result["kind"]) is not str or result["kind"] not in PICKER_RESULT_KINDS:
        raise _error("InvalidPayload")


def _validate_directory_snapshot_fingerprint(source_uri, directory):
    validate_read_transfer_directory_result(directory)
    if directory["kind"] != "Observed":
        raise _error("InvalidPayload")
    from transfer_directory_listing import compute_directory_fingerprint
    raw = {key: value for key, value in directory.items() if key != "observationFingerprint"}
    if compute_directory_fingerprint(source_uri, raw) != directory["observationFingerprint"]:
        raise _error("InvalidPayload")


def _validate_new_directory_segment(value):
    if (not _bounded_text(value) or value in (".", "..") or "/" in value or "\\" in value):
        raise _error("InvalidPayload")


def validate_directory_mutation_payload(action, payload):
    if action not in DIRECTORY_MUTATION_ACTIONS:
        raise _error("InvalidPayload")
    source_fields = ("browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri", "expectedBaiduUserId")
    action_fields = {
        "OpenTransferDirectoryRoot": ("rootBreadcrumbExactId",),
        "EnterTransferDirectory": ("childExactId", "childCanonicalPath"),
        "BeginCreateTransferDirectory": ("newSegment",),
        "ConfirmCreateTransferDirectory": ("newSegment",),
        "CancelTransferPicker": (),
    }[action]
    _scan_forbidden(payload)
    _exact_object(payload, source_fields + ("expectedDirectory",) + action_fields, body=True)
    validate_open_transfer_source_payload({field: payload[field] for field in source_fields})
    directory = payload["expectedDirectory"]
    _validate_directory_snapshot_fingerprint(payload["sourceUri"], directory)
    if directory["baiduUserId"] != payload["expectedBaiduUserId"]:
        raise _error("InvalidPayload")
    if action == "OpenTransferDirectoryRoot":
        if payload["rootBreadcrumbExactId"] != directory["ancestorExactIds"][0]: raise _error("InvalidPayload")
    elif action == "EnterTransferDirectory":
        child_id, child_path = payload["childExactId"], payload["childCanonicalPath"]
        if not _directory_exact_id(child_id) or not _canonical_directory_path(child_path): raise _error("InvalidPayload")
        child = next((item for item in directory["items"] if item["exactId"] == child_id), None)
        if (child is None or "/" in child["serverName"] or "\\" in child["serverName"] or
                child_path != ("/" + child["serverName"] if directory["canonicalPath"] == "/" else directory["canonicalPath"] + "/" + child["serverName"])):
            raise _error("InvalidPayload")
    elif action_fields:
        _validate_new_directory_segment(payload["newSegment"])


def validate_directory_mutation_result(action, result):
    _exact_object(result, ("kind",), body=True)
    expected = DIRECTORY_MUTATION_SUCCESS_KINDS.get(action)
    if type(result["kind"]) is not str or (result["kind"] != expected and result["kind"] not in DIRECTORY_MUTATION_DENIAL_KINDS):
        raise _error("InvalidPayload")


def validate_observe_transfer_picker_closed_payload(payload):
    _scan_forbidden(payload)
    source_fields = ("browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri", "expectedBaiduUserId")
    _exact_object(payload, source_fields + ("expectedSource",), body=True)
    validate_open_transfer_source_payload({field: payload[field] for field in source_fields})
    source = payload["expectedSource"]
    validate_read_transfer_source_result(source)
    if source["kind"] != "Observed" or source["baiduUserId"] != payload["expectedBaiduUserId"]:
        raise _error("InvalidPayload")
    from transfer_source_listing import compute_source_fingerprint
    raw = {key: value for key, value in source.items() if key != "observationFingerprint"}
    if compute_source_fingerprint(payload["sourceUri"], raw) != source["observationFingerprint"]:
        raise _error("InvalidPayload")


def validate_observe_transfer_picker_closed_result(result):
    _exact_object(result, ("kind",), body=True)
    if type(result["kind"]) is not str or result["kind"] not in PICKER_CLOSED_RESULT_KINDS:
        raise _error("InvalidPayload")


def _canonical_directory_path(value):
    if not _bounded_text(value, 4096) or not value.startswith("/") or "\\" in value:
        return False
    if value == "/":
        return True
    segments = value[1:].split("/")
    return len(segments) <= 32 and all(segment not in ("", ".", "..") and
        len(segment.encode("utf-8", errors="strict")) <= 256 for segment in segments)


def _target_canonical_path(value):
    if not _bounded_text(value, MAX_TARGET_CANONICAL_PATH_BYTES) or not value.startswith("/") or "\\" in value:
        return False
    if value == "/": return True
    segments = value[1:].split("/")
    return len(segments) <= 64 and all(segment not in ("", ".", "..") and
        _target_server_name(segment) for segment in segments)


def _target_server_name(value):
    return _source_server_name(value) and "/" not in value and "\\" not in value


def _validate_target_directory_identity(directory_id, path, ancestors):
    if not _directory_exact_id(directory_id) or not _target_canonical_path(path):
        raise _error("InvalidPayload")
    depth = 0 if path == "/" else len(path[1:].split("/"))
    if (type(ancestors) is not list or len(ancestors) != depth + 1 or not 1 <= len(ancestors) <= 65 or
            not all(_directory_exact_id(item) for item in ancestors) or len(set(ancestors)) != len(ancestors) or
            ancestors[-1] != directory_id):
        raise _error("InvalidPayload")


def _validate_target_binding(payload):
    _validate_binding_payload(payload)
    if not is_canonical_source_uri(payload["sourceUri"]) or not _bounded_text(payload["expectedBaiduUserId"]):
        raise _error("InvalidPayload")
    validate_read_transfer_source_result(payload["expectedSource"])
    source = payload["expectedSource"]
    if source["kind"] != "Observed" or source["baiduUserId"] != payload["expectedBaiduUserId"]:
        raise _error("InvalidPayload")
    from transfer_source_listing import compute_source_fingerprint
    raw = {key: value for key, value in source.items() if key != "observationFingerprint"}
    if compute_source_fingerprint(payload["sourceUri"], raw) != source["observationFingerprint"]:
        raise _error("InvalidPayload")


def _validate_target_header(value, binding=None):
    _exact_object(value, TARGET_HEADER_FIELDS, body=True)
    if value["scope"] != "AllItems" or not _canonical_uuid(value["browserId"]):
        raise _error("InvalidPayload")
    if not _bounded_text(value["targetId"]) or not _loopback_endpoint(value["devToolsEndpoint"]):
        raise _error("InvalidPayload")
    if not _bounded_text(value["baiduUserId"]) or not _bounded_text(value["generationToken"]):
        raise _error("InvalidPayload")
    _validate_target_directory_identity(value["directoryExactId"], value["canonicalPath"], value["ancestorExactIds"])
    if (type(value["totalCount"]) is not int or not 0 <= value["totalCount"] <= 1024 or
            value["reachedEnd"] is not True or value["isStable"] is not True or
            type(value["enumerationStepCount"]) is not int or not 1 <= value["enumerationStepCount"] <= 64 or
            not _nonce(value["parentObservationFingerprint"])):
        raise _error("InvalidPayload")
    if binding is not None and any(value[field] != binding[field] for field in
            ("browserId", "targetId", "devToolsEndpoint")):
        raise _error("InvalidPayload")
    if binding is not None and value["baiduUserId"] != binding["expectedBaiduUserId"]:
        raise _error("InvalidPayload")


def compute_target_inventory_fingerprint(value):
    """Versioned strict-UTF8, 4-byte big-endian length-prefixed SHA256 wire fingerprint."""
    fields = ["ManagedBrowserTargetInventory/v2", "AllItems", value["browserId"], value["targetId"],
              value["devToolsEndpoint"], value["baiduUserId"], value["generationToken"],
              value["directoryExactId"], value["canonicalPath"], str(len(value["ancestorExactIds"])),
              *value["ancestorExactIds"], str(value["totalCount"]), "true", "true",
              str(value["enumerationStepCount"])]
    for item in sorted(value["items"], key=lambda item: item["exactId"]):
        fields.extend((item["exactId"], item["kind"], item["serverName"]))
    digest = hashlib.sha256()
    for field in fields:
        try: encoded = field.encode("utf-8", errors="strict")
        except (AttributeError, UnicodeEncodeError): raise _error("InvalidPayload")
        digest.update(struct.pack(">I", len(encoded)))
        digest.update(encoded)
    return digest.hexdigest()


def validate_target_inventory_result(result, *, require_fingerprint=True):
    if type(result) is not dict or type(result.get("kind")) is not str:
        raise _error("InvalidPayload")
    if result["kind"] in TARGET_DENIAL_KINDS:
        _exact_object(result, ("kind",), body=True)
        return
    if result["kind"] != "Observed": raise _error("InvalidPayload")
    fields = TARGET_INVENTORY_FIELDS if require_fingerprint else tuple(
        field for field in TARGET_INVENTORY_FIELDS if field != "observationFingerprint")
    _exact_object(result, fields, body=True)
    if result["scope"] != "AllItems" or not _canonical_uuid(result["browserId"]): raise _error("InvalidPayload")
    if not _bounded_text(result["targetId"]) or not _loopback_endpoint(result["devToolsEndpoint"]): raise _error("InvalidPayload")
    if not _bounded_text(result["baiduUserId"]) or not _bounded_text(result["generationToken"]): raise _error("InvalidPayload")
    _validate_target_directory_identity(result["directoryExactId"], result["canonicalPath"], result["ancestorExactIds"])
    items = result["items"]
    if (type(items) is not list or len(items) > 1024 or type(result["totalCount"]) is not int or
            result["totalCount"] != len(items) or result["reachedEnd"] is not True or result["isStable"] is not True or
            type(result["enumerationStepCount"]) is not int or not 1 <= result["enumerationStepCount"] <= 64):
        raise _error("InvalidPayload")
    seen = set(result["ancestorExactIds"])
    for item in items:
        _exact_object(item, ("exactId", "kind", "serverName"), body=True)
        if (not _directory_exact_id(item["exactId"]) or item["kind"] not in ("File", "Folder") or
                not _target_server_name(item["serverName"]) or item["exactId"] in seen):
            raise _error("InvalidPayload")
        seen.add(item["exactId"])
    if require_fingerprint and (not _nonce(result["observationFingerprint"]) or
            compute_target_inventory_fingerprint(result) != result["observationFingerprint"]):
        raise _error("InvalidPayload")


def validate_target_baseline_request(action, payload):
    action_fields = {
        "OpenTransferTargetRoot": (),
        "EnterTransferTargetDirectory": ("expectedParent", "childExactId", "childServerName", "childCanonicalPath"),
        "ReadTransferTargetDirectory": ("expectedDirectoryExactId", "expectedCanonicalPath", "expectedAncestorExactIds"),
        "OpenTransferSourceAfterTargetRead": ("expectedTarget",),
    }[action]
    _scan_forbidden(payload)
    _exact_object(payload, TARGET_BINDING_FIELDS + action_fields, body=True)
    _validate_target_binding(payload)
    if action == "EnterTransferTargetDirectory":
        parent = payload["expectedParent"]
        _validate_target_header(parent, payload)
        child_id, child_name, child_path = payload["childExactId"], payload["childServerName"], payload["childCanonicalPath"]
        expected = ("/" if parent["canonicalPath"] == "/" else parent["canonicalPath"] + "/") + child_name
        if (not _directory_exact_id(child_id) or not _target_server_name(child_name) or
                not _target_canonical_path(child_path) or child_path != expected or child_id in parent["ancestorExactIds"]):
            raise _error("InvalidPayload")
    elif action == "ReadTransferTargetDirectory":
        values = [payload[field] for field in action_fields]
        if all(value is None for value in values): return
        if any(value is None for value in values): raise _error("InvalidPayload")
        _validate_target_directory_identity(*values)
    elif action == "OpenTransferSourceAfterTargetRead":
        _validate_target_header(payload["expectedTarget"], payload)


def validate_target_navigation_result(action, result):
    _exact_object(result, ("kind", "navigationState"), body=True)
    success = TARGET_NAVIGATION_SUCCESSES[action]
    kind, state = result["kind"], result["navigationState"]
    if kind == success:
        if state != "RouteOpened": raise _error("InvalidPayload")
    elif kind in TARGET_DENIAL_KINDS:
        if state not in ("KnownNotDispatched", "RouteOutcomeUnknown"): raise _error("InvalidPayload")
    else: raise _error("InvalidPayload")


def validate_read_transfer_directory_payload(payload):
    _scan_forbidden(payload)
    source_fields = ("browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri", "expectedBaiduUserId")
    _exact_object(payload, source_fields + ("expectedParentExactId", "expectedCanonicalPath"), body=True)
    validate_open_transfer_source_payload({field: payload[field] for field in source_fields})
    parent, path = payload["expectedParentExactId"], payload["expectedCanonicalPath"]
    if (parent is None) != (path is None) or (parent is not None and
            (not _directory_exact_id(parent) or not _canonical_directory_path(path))):
        raise _error("InvalidPayload")


def validate_read_transfer_directory_result(result, *, require_fingerprint=True):
    """Picker FOLDER completeness only, never a complete saved-file inventory."""
    if type(result) is not dict or type(result.get("kind")) is not str:
        raise _error("InvalidPayload")
    if result["kind"] in DIRECTORY_LISTING_DENIAL_KINDS:
        _exact_object(result, ("kind",), body=True)
        return
    if result["kind"] != "Observed":
        raise _error("InvalidPayload")
    _exact_object(result, DIRECTORY_LISTING_FIELDS + (("observationFingerprint",) if require_fingerprint else ()), body=True)
    if (not _bounded_text(result["baiduUserId"]) or not _bounded_text(result["generationToken"]) or
            not _directory_exact_id(result["parentExactId"]) or not _canonical_directory_path(result["canonicalPath"])):
        raise _error("InvalidPayload")
    ancestors = result["ancestorExactIds"]
    depth = 0 if result["canonicalPath"] == "/" else len(result["canonicalPath"][1:].split("/"))
    if (type(ancestors) is not list or not 1 <= len(ancestors) <= 33 or len(ancestors) != depth + 1 or
            not all(_directory_exact_id(item) for item in ancestors) or
            len(set(ancestors)) != len(ancestors) or ancestors[-1] != result["parentExactId"]):
        raise _error("InvalidPayload")
    items = result["items"]
    if (type(items) is not list or len(items) > 32 or type(result["totalCount"]) is not int or
            result["totalCount"] != len(items) or result["reachedEnd"] is not True or result["isStable"] is not True):
        raise _error("InvalidPayload")
    seen = set(ancestors)
    for item in items:
        _exact_object(item, ("exactId", "kind", "serverName"), body=True)
        if (not _directory_exact_id(item["exactId"]) or item["kind"] != "Folder" or
                not _source_server_name(item["serverName"]) or item["exactId"] in seen):
            raise _error("InvalidPayload")
        seen.add(item["exactId"])
    if require_fingerprint and not _nonce(result["observationFingerprint"]):
        raise _error("InvalidPayload")


def _append_length_prefixed(digest, value):
    try:
        encoded = value.encode("utf-8", errors="strict")
    except (AttributeError, UnicodeEncodeError):
        raise _error("InvalidPayload")
    digest.update(struct.pack(">I", len(encoded)))
    digest.update(encoded)


def compute_transfer_readiness_fingerprint(context, eligible_control_count=1):
    """Match ManagedBrowserTransferSubmissionReadiness byte-for-byte."""
    if type(context) is not dict or type(eligible_control_count) is not int:
        raise _error("InvalidPayload")
    timeout = context.get("timeoutMilliseconds")
    if type(timeout) is not int:
        raise _error("InvalidPayload")
    digest = hashlib.sha256()
    fields = (
        "ManagedBrowserTransferSubmissionReadiness/v1", context.get("browserId"),
        context.get("targetId"), context.get("devToolsEndpoint"), str(timeout), context.get("sourceUri"),
        context.get("baiduUserId"),
        context.get("sourceGenerationToken"), context.get("sourceObservationFingerprint"),
        context.get("sourceFingerprint"), context.get("sourceExactId"), context.get("sourceItemKind"),
        context.get("selectedTargetExactId"), context.get("selectedTargetCanonicalPath"),
        context.get("targetBaselineFingerprint"), context.get("duplicatePolicy"),
        str(eligible_control_count),
    )
    for field in fields:
        _append_length_prefixed(digest, field)
    return digest.hexdigest()


def compute_transfer_official_fingerprint(request_id, target_id, source_id, destination_id):
    """Match ManagedBrowserTransferSubmissionOutcomeResult byte-for-byte."""
    digest = hashlib.sha256()
    for field in ("ManagedBrowserTransferSubmissionOutcome/v1", request_id, target_id, source_id, destination_id):
        _append_length_prefixed(digest, field)
    return digest.hexdigest()


def _validate_transfer_submission_context(payload):
    _validate_binding_payload(payload)
    if (not is_canonical_source_uri(payload["sourceUri"]) or
            not _bounded_text(payload["baiduUserId"]) or
            not _bounded_text(payload["sourceGenerationToken"]) or
            not _nonce(payload["sourceObservationFingerprint"]) or
            not _nonce(payload["sourceFingerprint"]) or
            not _directory_exact_id(payload["sourceExactId"]) or
            payload["sourceItemKind"] not in ("File", "Folder") or
            not _directory_exact_id(payload["selectedTargetExactId"]) or
            not _target_canonical_path(payload["selectedTargetCanonicalPath"]) or
            not _nonce(payload["targetBaselineFingerprint"]) or
            payload["duplicatePolicy"] not in ("Skip", "Overwrite", "Rename")):
        raise _error("InvalidPayload")


def validate_transfer_submission_request(action, payload):
    if action not in TRANSFER_SUBMISSION_ACTIONS:
        raise _error("InvalidPayload")
    _scan_forbidden(payload)
    fields = TRANSFER_SUBMISSION_CONTEXT_FIELDS + (() if action == "ObserveTransferSubmissionReady"
                                                    else ("readinessFingerprint",))
    _exact_object(payload, fields, body=True)
    _validate_transfer_submission_context(payload)
    if action != "ObserveTransferSubmissionReady":
        fingerprint = payload["readinessFingerprint"]
        if (not _nonce(fingerprint) or
                compute_transfer_readiness_fingerprint(payload) != fingerprint):
            raise _error("InvalidPayload")


def validate_transfer_readiness_result(result):
    if type(result) is not dict or type(result.get("kind")) is not str:
        raise _error("InvalidPayload")
    if result["kind"] in TRANSFER_READINESS_DENIAL_KINDS:
        _exact_object(result, ("kind",), body=True)
        return
    if result["kind"] != "Ready":
        raise _error("InvalidPayload")
    fields = ("kind",) + TRANSFER_SUBMISSION_CONTEXT_FIELDS + ("eligibleControlCount", "readinessFingerprint")
    _exact_object(result, fields, body=True)
    context = {field: result[field] for field in TRANSFER_SUBMISSION_CONTEXT_FIELDS}
    _validate_transfer_submission_context(context)
    if (result["eligibleControlCount"] != 1 or type(result["eligibleControlCount"]) is not int or
            not _nonce(result["readinessFingerprint"]) or
            compute_transfer_readiness_fingerprint(context) != result["readinessFingerprint"]):
        raise _error("InvalidPayload")


_VALID_TRANSFER_SUBMIT_PAIRS = frozenset((
    ("ClickAttempted", "ClickDispatched"), ("ClickAttempted", "DispatchOutcomeUnknown"),
    ("ReadinessMismatch", "KnownNotDispatched"), ("PageNotValidated", "KnownNotDispatched"),
    ("AccountIdentityMismatch", "KnownNotDispatched"), ("LoginRequired", "KnownNotDispatched"),
    ("VerificationRequired", "KnownNotDispatched"), ("BrowserUnavailable", "KnownNotDispatched"),
    ("BrowserUnavailable", "DispatchOutcomeUnknown"), ("TimedOut", "KnownNotDispatched"),
    ("TimedOut", "DispatchOutcomeUnknown"), ("Cancelled", "KnownNotDispatched"),
    ("Cancelled", "DispatchOutcomeUnknown"),
))


def validate_transfer_submit_result(result):
    _exact_object(result, ("kind", "dispatchState"), body=True)
    if (type(result["kind"]) is not str or type(result["dispatchState"]) is not str or
            (result["kind"], result["dispatchState"]) not in _VALID_TRANSFER_SUBMIT_PAIRS):
        raise _error("InvalidPayload")


def validate_transfer_outcome_result(result):
    if type(result) is not dict or type(result.get("kind")) is not str:
        raise _error("InvalidPayload")
    if result["kind"] not in TRANSFER_OUTCOME_KINDS:
        raise _error("InvalidPayload")
    if result["kind"] != "AcceptedBound":
        _exact_object(result, ("kind",), body=True)
        return
    fields = ("kind", "officialRequestId", "exactTargetId", "sourceExactId", "destinationExactId",
              "officialObservationFingerprint")
    _exact_object(result, fields, body=True)
    if (not _bounded_text(result["officialRequestId"]) or not _directory_exact_id(result["exactTargetId"]) or
            not _directory_exact_id(result["sourceExactId"]) or
            not _directory_exact_id(result["destinationExactId"]) or
            not _nonce(result["officialObservationFingerprint"]) or
            compute_transfer_official_fingerprint(result["officialRequestId"], result["exactTargetId"],
                result["sourceExactId"], result["destinationExactId"]) != result["officialObservationFingerprint"]):
        raise _error("InvalidPayload")


def _fixed_share_code(value):
    return type(value) is str and len(value) == 4 and all(
        character.isascii() and character.isalnum() for character in value)


def _canonical_share_url(value):
    if type(value) is not str:
        return False
    try:
        parsed = urlsplit(value)
        port = parsed.port
    except (ValueError, TypeError):
        return False
    return (parsed.scheme == "https" and parsed.hostname == "pan.baidu.com" and
            port in (None, 443) and parsed.username is None and parsed.password is None and
            parsed.path.startswith("/s/") and len(parsed.path) > 3 and
            not parsed.query and not parsed.fragment)


def _validate_share_submission_context(payload):
    _validate_binding_payload(payload)
    exact_item_ids = _exact_item_ids(payload["exactItemIds"])
    if (not _bounded_text(payload["baiduUserId"]) or
            not _directory_exact_id(payload["targetDirectoryExactId"]) or
            not _target_canonical_path(payload["targetCanonicalPath"]) or
            not _nonce(payload["exactItemSetFingerprint"]) or
            _remote_item_set_fingerprint(exact_item_ids) != payload["exactItemSetFingerprint"] or
            payload["codeMode"] not in ("Random", "Fixed") or
            (payload["codeMode"] == "Random" and payload["fixedCode"] is not None) or
            (payload["codeMode"] == "Fixed" and not _fixed_share_code(payload["fixedCode"]))):
        raise _error("InvalidPayload")


def validate_share_submission_request(action, payload):
    if action not in SHARE_SUBMISSION_ACTIONS:
        raise _error("InvalidPayload")
    _scan_forbidden(payload)
    suffix = () if action == "ReadShareOfficialListing" else (
        "baselineShareIdsFingerprint", "baselineObservationFingerprint")
    _exact_object(payload, SHARE_SUBMISSION_CONTEXT_FIELDS + suffix, body=True)
    _validate_share_submission_context(payload)
    if action != "ReadShareOfficialListing" and (
            not _nonce(payload["baselineShareIdsFingerprint"]) or
            not _nonce(payload["baselineObservationFingerprint"])):
        raise _error("InvalidPayload")


def _remote_item_set_fingerprint(values):
    digest = hashlib.sha256()
    snapshot = sorted(values, key=lambda item: item.encode("utf-16-be"))
    if not snapshot or len(snapshot) != len(set(snapshot)):
        raise _error("InvalidPayload")
    for value in snapshot:
        if not _directory_exact_id(value):
            raise _error("InvalidPayload")
        _append_length_prefixed(digest, value)
    return digest.hexdigest()


def _exact_item_ids(value):
    if type(value) is not list or not value or len(value) > 1000:
        raise _error("InvalidPayload")
    snapshot = tuple(value)
    canonical = tuple(sorted(snapshot, key=lambda item: item.encode("utf-16-be")
                             if type(item) is str else b""))
    if (snapshot != canonical or len(snapshot) != len(set(snapshot)) or
            any(not _directory_exact_id(item) for item in snapshot)):
        raise _error("InvalidPayload")
    return snapshot


def compute_share_ids_fingerprint(values):
    snapshot = sorted(tuple(values))
    if len(snapshot) != len(set(snapshot)):
        raise _error("InvalidPayload")
    digest = hashlib.sha256()
    _append_length_prefixed(digest, "web-automation-baseline-v1")
    for value in snapshot:
        if not _bounded_text(value):
            raise _error("InvalidPayload")
        _append_length_prefixed(digest, value)
    return digest.hexdigest()


def _share_context_fingerprint_fields(value):
    exact_item_ids = _exact_item_ids(value["exactItemIds"])
    return (
        value["browserId"], value["targetId"], value["devToolsEndpoint"],
        str(value["timeoutMilliseconds"]), value["baiduUserId"],
        value["targetDirectoryExactId"], value["targetCanonicalPath"],
        str(len(exact_item_ids)), *exact_item_ids, value["exactItemSetFingerprint"],
        value["codeMode"], value["fixedCode"] or "",
    )


def compute_share_listing_fingerprint(value):
    digest = hashlib.sha256()
    _append_length_prefixed(digest, "ManagedBrowserShareOfficialListing/v2")
    for field in _share_context_fingerprint_fields(value):
        _append_length_prefixed(digest, field)
    entries = sorted(value["entries"], key=lambda item: item["shareId"])
    _append_length_prefixed(digest, str(len(entries)))
    for item in entries:
        exact_item_ids = _exact_item_ids(item["exactItemIds"])
        for field in (item["shareId"], item["shareUrl"], str(len(exact_item_ids)),
                      *exact_item_ids, "true" if item["isPermanent"] else "false",
                      "true" if item["isAccessUnlimited"] else "false", item["codeMode"],
                      item["effectiveCode"]):
            _append_length_prefixed(digest, field)
    return digest.hexdigest()


def compute_share_outcome_fingerprint(adapter_request_id, share_id, share_url, exact_item_ids,
                                      is_permanent, is_access_unlimited, code_mode, effective_code):
    digest = hashlib.sha256()
    exact_item_ids = _exact_item_ids(exact_item_ids)
    for field in ("ManagedBrowserShareSubmissionOutcome/v2", adapter_request_id, share_id, share_url,
                  str(len(exact_item_ids)), *exact_item_ids, "true" if is_permanent else "false",
                  "true" if is_access_unlimited else "false", code_mode, effective_code):
        _append_length_prefixed(digest, field)
    return digest.hexdigest()


def _validate_share_entry(item):
    _exact_object(item, ("shareId", "shareUrl", "exactItemIds", "isPermanent",
                         "isAccessUnlimited", "codeMode", "effectiveCode"), body=True)
    if (not _bounded_text(item["shareId"]) or not _canonical_share_url(item["shareUrl"]) or
            item["isPermanent"] is not True or
            item["isAccessUnlimited"] is not True or item["codeMode"] not in ("Random", "Fixed") or
            not _fixed_share_code(item["effectiveCode"])):
        raise _error("InvalidPayload")
    _exact_item_ids(item["exactItemIds"])


def validate_share_listing_result(result):
    if type(result) is not dict or type(result.get("kind")) is not str:
        raise _error("InvalidPayload")
    if result["kind"] in SHARE_LISTING_DENIAL_KINDS:
        _exact_object(result, ("kind",), body=True)
        return
    if result["kind"] != "Observed":
        raise _error("InvalidPayload")
    fields = (("kind",) + SHARE_SUBMISSION_CONTEXT_FIELDS +
              ("totalCount", "reachedEnd", "isStable", "entries",
               "shareIdsFingerprint", "observationFingerprint"))
    _exact_object(result, fields, body=True)
    _validate_share_submission_context(result)
    entries = result["entries"]
    if (type(entries) is not list or len(entries) > 1000 or
            type(result["totalCount"]) is not int or result["totalCount"] != len(entries) or
            result["reachedEnd"] is not True or result["isStable"] is not True):
        raise _error("InvalidPayload")
    for item in entries:
        _validate_share_entry(item)
    ids = [item["shareId"] for item in entries]
    if (len(ids) != len(set(ids)) or not _nonce(result["shareIdsFingerprint"]) or
            compute_share_ids_fingerprint(ids) != result["shareIdsFingerprint"] or
            not _nonce(result["observationFingerprint"]) or
            compute_share_listing_fingerprint(result) != result["observationFingerprint"]):
        raise _error("InvalidPayload")


_VALID_SHARE_SUBMIT_PAIRS = frozenset((
    ("ClickAttempted", "ClickDispatched"), ("ClickAttempted", "DispatchOutcomeUnknown"),
    ("BaselineMismatch", "KnownNotDispatched"), ("PageNotValidated", "KnownNotDispatched"),
    ("AccountIdentityMismatch", "KnownNotDispatched"), ("LoginRequired", "KnownNotDispatched"),
    ("VerificationRequired", "KnownNotDispatched"), ("BrowserUnavailable", "KnownNotDispatched"),
    ("BrowserUnavailable", "DispatchOutcomeUnknown"), ("TimedOut", "KnownNotDispatched"),
    ("TimedOut", "DispatchOutcomeUnknown"), ("Cancelled", "KnownNotDispatched"),
    ("Cancelled", "DispatchOutcomeUnknown"),
))


def validate_share_submit_result(result):
    _exact_object(result, ("kind", "dispatchState"), body=True)
    if (type(result["kind"]) is not str or type(result["dispatchState"]) is not str or
            (result["kind"], result["dispatchState"]) not in _VALID_SHARE_SUBMIT_PAIRS):
        raise _error("InvalidPayload")


def validate_share_outcome_result(result):
    if type(result) is not dict or type(result.get("kind")) is not str:
        raise _error("InvalidPayload")
    if result["kind"] not in SHARE_OUTCOME_KINDS:
        raise _error("InvalidPayload")
    if result["kind"] != "AcceptedBound":
        _exact_object(result, ("kind",), body=True)
        return
    fields = ("kind", "adapterRequestId", "shareId", "shareUrl", "exactItemIds", "isPermanent",
              "isAccessUnlimited", "codeMode", "effectiveCode", "officialObservationFingerprint")
    _exact_object(result, fields, body=True)
    _validate_share_entry({field: result[field] for field in fields[2:-1]})
    if (not _bounded_text(result["adapterRequestId"]) or
            not _nonce(result["officialObservationFingerprint"]) or
            compute_share_outcome_fingerprint(
                result["adapterRequestId"], result["shareId"], result["shareUrl"],
                result["exactItemIds"], result["isPermanent"], result["isAccessUnlimited"],
                result["codeMode"], result["effectiveCode"]) != result["officialObservationFingerprint"]):
        raise _error("InvalidPayload")


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
        _validate_binding_payload(payload)
    elif action in ("OpenTransferSource", "ReadTransferSource"):
        validate_open_transfer_source_payload(payload)
    elif action == "ReadTransferDirectory":
        validate_read_transfer_directory_payload(payload)
    elif action == "OpenTransferPicker":
        validate_open_transfer_picker_payload(payload)
    elif action in DIRECTORY_MUTATION_ACTIONS:
        validate_directory_mutation_payload(action, payload)
    elif action == "ObserveTransferPickerClosed":
        validate_observe_transfer_picker_closed_payload(payload)
    elif action in TARGET_NAVIGATION_SUCCESSES or action == "ReadTransferTargetDirectory":
        validate_target_baseline_request(action, payload)
    elif action in TRANSFER_SUBMISSION_ACTIONS:
        validate_transfer_submission_request(action, payload)
    elif action in SHARE_SUBMISSION_ACTIONS:
        validate_share_submission_request(action, payload)
    elif action == "SubmitExtractionCode":
        validate_submit_extraction_code_payload(payload)
    if action == "EnterTransferTargetDirectory":
        try:
            encoded_size = len(json.dumps(value, ensure_ascii=False, allow_nan=False,
                                          separators=(",", ":")).encode("utf-8")) + 4
        except (TypeError, ValueError, UnicodeEncodeError, OverflowError) as exc:
            raise _error("InvalidMessage") from exc
        if encoded_size > MAX_TARGET_ENTER_FRAME_BYTES:
            raise _error("FrameSizeInvalid")


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
    elif action == "OpenTransferSource":
        _exact_object(result, ("kind",), body=True)
        if result["kind"] not in SOURCE_RESULT_KINDS:
            raise _error("InvalidPayload")
    elif action == "ReadTransferSource":
        validate_read_transfer_source_result(result)
    elif action == "ReadTransferDirectory":
        validate_read_transfer_directory_result(result)
    elif action == "OpenTransferPicker":
        validate_open_transfer_picker_result(result)
    elif action in DIRECTORY_MUTATION_ACTIONS:
        validate_directory_mutation_result(action, result)
    elif action == "ObserveTransferPickerClosed":
        validate_observe_transfer_picker_closed_result(result)
    elif action in TARGET_NAVIGATION_SUCCESSES:
        validate_target_navigation_result(action, result)
    elif action == "ReadTransferTargetDirectory":
        validate_target_inventory_result(result)
    elif action == "ObserveTransferSubmissionReady":
        validate_transfer_readiness_result(result)
    elif action == "SubmitTransferOnce":
        validate_transfer_submit_result(result)
    elif action == "ObserveTransferSubmissionOutcome":
        validate_transfer_outcome_result(result)
    elif action == "ReadShareOfficialListing":
        validate_share_listing_result(result)
    elif action == "SubmitShareOnce":
        validate_share_submit_result(result)
    elif action == "ObserveShareSubmissionOutcome":
        validate_share_outcome_result(result)
    elif action == "SubmitExtractionCode":
        _exact_object(result, ("kind", "dispatchState"), body=True)
        if result["kind"] not in EXTRACTION_RESULT_KINDS or result["dispatchState"] not in EXTRACTION_DISPATCH_STATES:
            raise _error("InvalidPayload")
        if (result["kind"] == "SourceOpened" and result["dispatchState"] != "ClickDispatched") or \
                (result["kind"] == "OutcomeUnknown" and result["dispatchState"] != "ClickOutcomeUnknown"):
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
    validate_response_for(request, value)
    return value


def validate_response_for(request, response) -> None:
    validate_request(request)
    validate_response(response)
    for field in ("correlationId", "operationId", "accountId", "generation", "action"):
        if request[field] != response[field]:
            raise _error("ResponseIdentityMismatch")
    if request["action"] == "ReadTransferTargetDirectory" and response["result"]["kind"] == "Observed":
        payload, result = request["payload"], response["result"]
        if any(result[field] != payload[expected] for field, expected in (
                ("browserId", "browserId"), ("targetId", "targetId"),
                ("devToolsEndpoint", "devToolsEndpoint"), ("baiduUserId", "expectedBaiduUserId"))):
            raise _error("ResponseIdentityMismatch")
        if payload["expectedDirectoryExactId"] is None:
            if (result["canonicalPath"] != "/" or
                    result["ancestorExactIds"] != [result["directoryExactId"]]):
                raise _error("ResponseIdentityMismatch")
        elif any(result[field] != payload[expected] for field, expected in (
                ("directoryExactId", "expectedDirectoryExactId"),
                ("canonicalPath", "expectedCanonicalPath"),
                ("ancestorExactIds", "expectedAncestorExactIds"))):
            raise _error("ResponseIdentityMismatch")
    if request["action"] == "ObserveTransferSubmissionReady" and response["result"]["kind"] == "Ready":
        payload, result = request["payload"], response["result"]
        # Mirrors the C# immutable evidence comparison, including the operation timeout.
        for field in TRANSFER_SUBMISSION_CONTEXT_FIELDS:
            if payload[field] != result[field]:
                raise _error("ResponseIdentityMismatch")
    if request["action"] == "ObserveTransferSubmissionOutcome" and response["result"]["kind"] == "AcceptedBound":
        payload, result = request["payload"], response["result"]
        if (result["exactTargetId"] != payload["selectedTargetExactId"] or
                result["sourceExactId"] != payload["sourceExactId"]):
            raise _error("ResponseIdentityMismatch")
    if request["action"] == "ReadShareOfficialListing" and response["result"]["kind"] == "Observed":
        payload, result = request["payload"], response["result"]
        for field in SHARE_SUBMISSION_CONTEXT_FIELDS:
            if payload[field] != result[field]:
                raise _error("ResponseIdentityMismatch")
    if request["action"] == "ObserveShareSubmissionOutcome" and response["result"]["kind"] == "AcceptedBound":
        payload, result = request["payload"], response["result"]
        if (result["exactItemIds"] != payload["exactItemIds"] or
                result["isPermanent"] is not True or result["isAccessUnlimited"] is not True or
                result["codeMode"] != payload["codeMode"] or
                (payload["codeMode"] == "Fixed" and result["effectiveCode"] != payload["fixedCode"])):
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
    if (not response and value.get("action") == "EnterTransferTargetDirectory" and
            len(payload) + 4 > MAX_TARGET_ENTER_FRAME_BYTES):
        raise _error("FrameSizeInvalid")
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
