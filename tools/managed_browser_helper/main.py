"""Minimal read-only managed Chromium helper.

stdout is protocol-only. stderr is restricted to the fixed event codes below.
"""

from __future__ import annotations

import io
import os
import sys
import time
from html.parser import HTMLParser
from urllib.parse import urlsplit

import protocol

PAN_ROOT = "https://pan.baidu.com/"
PASSPORT_ROOT = "https://passport.baidu.com/"
SAFE_PROTOCOL_EVENT = "HELPER_PROTOCOL_ERROR"
SAFE_RUNTIME_EVENT = "HELPER_RUNTIME_ERROR"
SAFE_SESSION_LIMIT_EVENT = "HELPER_SESSION_LIMIT_REACHED"


def classify_route(value: str) -> str:
    try:
        parsed = urlsplit(value)
        port = parsed.port
    except (ValueError, TypeError):
        return "Rejected"
    if (
        parsed.scheme != "https"
        or parsed.username is not None
        or parsed.password is not None
        or port not in (None, 443)
        or parsed.path != "/"
        or parsed.query
        or parsed.fragment
    ):
        return "Rejected"
    if parsed.hostname == "pan.baidu.com":
        return "PanRoot"
    if parsed.hostname == "passport.baidu.com":
        return "PassportRoot"
    return "Rejected"


class _IdentityFixtureParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.logged_out = False
        self.stable_uid = None

    def handle_starttag(self, _tag, attrs):
        values = dict(attrs)
        if values.get("data-baidu-login-state") == "logged-out":
            self.logged_out = True
        uid = values.get("data-baidu-stable-uid")
        if uid is not None and self.stable_uid is None:
            self.stable_uid = uid


def map_identity_fixture(html: str):
    """Map local test fixtures through the same fixed attributes used by the observer."""
    if not isinstance(html, str) or len(html.encode("utf-8")) > protocol.MAX_FRAME_BYTES:
        return {"kind": "BrowserUnavailable"}
    parser = _IdentityFixtureParser()
    try:
        parser.feed(html)
        parser.close()
    except Exception:
        return {"kind": "BrowserUnavailable"}
    if parser.logged_out:
        return {"kind": "LoginRequired"}
    if protocol._bounded_text(parser.stable_uid):
        return {"kind": "Observed", "stableBaiduUserId": parser.stable_uid}
    return {"kind": "BrowserUnavailable"}


def _fixed_dom_identity(tab, remaining_seconds):
    selector_timeout = max(0.001, min(0.2, remaining_seconds))
    logged_out = tab.ele("css:[data-baidu-login-state=logged-out]", timeout=selector_timeout)
    if logged_out:
        return {"kind": "LoginRequired"}
    identity = tab.ele("css:[data-baidu-stable-uid]", timeout=selector_timeout)
    if not identity:
        return {"kind": "BrowserUnavailable"}
    uid = identity.attr("data-baidu-stable-uid")
    if not protocol._bounded_text(uid):
        return {"kind": "BrowserUnavailable"}
    return {"kind": "Observed", "stableBaiduUserId": uid}


def observe_bound_target(
        payload,
        connector,
        *,
        monotonic=time.monotonic,
        sleeper=time.sleep,
        is_cancelled=lambda: False):
    """Observe one immutable binding with a cooperative soft deadline.

    A synchronous driver call cannot be preempted here. We check before and after every call,
    pass the remaining budget where DrissionPage accepts one, and discard any result returned
    after expiry. Task 9's owning C# client supplies the hard bound by killing the helper process
    and synthesizing the typed timeout/cancellation result.
    """
    deadline = monotonic() + payload["timeoutMilliseconds"] / 1000.0

    def budget():
        if is_cancelled():
            return {"kind": "Cancelled"}, 0.0
        remaining = deadline - monotonic()
        if remaining <= 0:
            return {"kind": "TimedOut"}, 0.0
        return None, remaining

    try:
        terminal, remaining = budget()
        if terminal:
            return terminal
        browser_id = connector.browser_id
        terminal, remaining = budget()
        if terminal:
            return terminal
        if browser_id != payload["browserId"]:
            return {"kind": "BrowserUnavailable"}

        target = connector.get_target(payload["targetId"], remaining)
        terminal, remaining = budget()
        if terminal:
            return terminal
    except Exception:
        return {"kind": "BrowserUnavailable"}
    if target is None:
        return {"kind": "BrowserUnavailable"}

    try:
        target_id = target.target_id
        terminal, remaining = budget()
        if terminal:
            return terminal
        if target_id != payload["targetId"]:
            return {"kind": "BrowserUnavailable"}

        route = classify_route(target.current_url(remaining))
        terminal, remaining = budget()
        if terminal:
            return terminal
        if route != "PanRoot":
            target.navigate(PAN_ROOT, remaining)
            terminal, remaining = budget()
            if terminal:
                return terminal

        while True:
            terminal, remaining = budget()
            if terminal:
                return terminal
            browser_id = connector.browser_id
            terminal, remaining = budget()
            if terminal:
                return terminal
            if browser_id != payload["browserId"]:
                return {"kind": "BrowserUnavailable"}
            current = connector.get_target(payload["targetId"], remaining)
            terminal, remaining = budget()
            if terminal:
                return terminal
            if current is None or current is not target:
                return {"kind": "BrowserUnavailable"}
            current_target_id = current.target_id
            terminal, remaining = budget()
            if terminal:
                return terminal
            if current_target_id != payload["targetId"]:
                return {"kind": "BrowserUnavailable"}
            route = classify_route(target.current_url(remaining))
            terminal, remaining = budget()
            if terminal:
                return terminal
            if route == "PanRoot":
                result = target.observe_identity(remaining)
                terminal, remaining = budget()
                if terminal:
                    return terminal
                if result.get("kind") != "LoginRequired":
                    return result
                sleeper(min(0.1, remaining))
                terminal, remaining = budget()
                if terminal:
                    return terminal
                continue
            if route != "PassportRoot":
                return {"kind": "BrowserUnavailable"}
            sleeper(min(0.1, remaining))
            terminal, remaining = budget()
            if terminal:
                return terminal
    except Exception:
        return {"kind": "BrowserUnavailable"}


class _DrissionTarget:
    def __init__(self, tab):
        self._tab = tab
        self.target_id = tab.tab_id

    def current_url(self, _remaining_seconds):
        return self._tab.url

    def navigate(self, value, remaining_seconds):
        self._tab.get(value, timeout=remaining_seconds)

    def observe_identity(self, remaining_seconds):
        return _fixed_dom_identity(self._tab, remaining_seconds)


class _DrissionConnector:
    def __init__(self, browser):
        self._browser = browser
        try:
            import uuid
            self.browser_id = str(uuid.UUID(str(browser.id)))
        except (ValueError, TypeError, AttributeError):
            self.browser_id = ""
        self._targets = {}

    def get_target(self, target_id, _remaining_seconds):
        if target_id not in self._browser.tab_ids:
            return None
        target = self._targets.get(target_id)
        if target is None:
            target = _DrissionTarget(self._browser.get_tab(target_id))
            self._targets[target_id] = target
        return target


def observe_account_identity(payload, is_cancelled=lambda: False):
    """Attach to one pre-verified endpoint and one exact target; perform no mutation."""
    try:
        from DrissionPage import Chromium, ChromiumOptions

        endpoint = urlsplit(payload["devToolsEndpoint"])
        address = f"{endpoint.hostname}:{endpoint.port}"
        options = ChromiumOptions().set_address(address).existing_only(True)
        browser = Chromium(options)
        return observe_bound_target(
            payload,
            _DrissionConnector(browser),
            is_cancelled=is_cancelled,
        )
    except Exception:
        return {"kind": "BrowserUnavailable"}


def _write_event(error_writer, event_code):
    error_writer.write(event_code + "\n")
    flush = getattr(error_writer, "flush", None)
    if flush is not None:
        flush()


def run(
        reader,
        writer,
        error_writer,
        *,
        helper_pid=None,
        parent_pid=None,
        observer=None,
        max_requests=protocol.MAX_REQUESTS_PER_SESSION):
    helper_pid = os.getpid() if helper_pid is None else helper_pid
    parent_pid = os.getppid() if parent_pid is None else parent_pid
    observer = observe_account_identity if observer is None else observer
    if not 1 <= max_requests <= protocol.MAX_REQUESTS_PER_SESSION:
        raise ValueError("max_requests is outside the fixed helper session bound")
    handshaken = False
    seen_correlation_ids = set()
    request_count = 0
    try:
        while True:
            message = protocol.read_message(reader)
            if message is None:
                return 0
            request_count += 1
            correlation_id = message["correlationId"]
            if correlation_id in seen_correlation_ids:
                raise protocol.ProtocolError("DuplicateCorrelationId")
            seen_correlation_ids.add(correlation_id)

            action = message["action"]
            if not handshaken:
                if action != "Handshake":
                    raise protocol.ProtocolError("HandshakeRequired")
                payload = message["payload"]
                if payload["parentProcessId"] != parent_pid:
                    raise protocol.ProtocolError("ParentProcessMismatch")
                result = {
                    "kind": "HandshakeAccepted",
                    "nonce": payload["nonce"],
                    "parentProcessId": payload["parentProcessId"],
                    "helperProcessId": helper_pid,
                    "capabilities": list(protocol.REQUIRED_CAPABILITIES),
                }
                handshaken = True
            elif action == "Handshake":
                raise protocol.ProtocolError("HandshakeAlreadyCompleted")
            elif action == "Health":
                result = {"kind": "Healthy"}
            elif action == "Shutdown":
                result = {"kind": "ShuttingDown"}
                protocol.write_message(writer, protocol.response_for(message, result), response=True)
                return 0
            elif action == "ObserveAccountIdentity":
                result = observer(message["payload"])
            else:
                raise protocol.ProtocolError("UnknownAction")

            protocol.write_message(writer, protocol.response_for(message, result), response=True)
            if request_count >= max_requests:
                _write_event(error_writer, SAFE_SESSION_LIMIT_EVENT)
                return 0
    except protocol.ProtocolError:
        _write_event(error_writer, SAFE_PROTOCOL_EVENT)
        return 2
    except Exception:
        _write_event(error_writer, SAFE_RUNTIME_EVENT)
        return 3


def _binary_stream(stream):
    return getattr(stream, "buffer", stream)


if __name__ == "__main__":
    raise SystemExit(run(_binary_stream(sys.stdin), _binary_stream(sys.stdout), sys.stderr))
