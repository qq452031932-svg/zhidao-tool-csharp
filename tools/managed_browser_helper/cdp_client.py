"""Minimal Chrome DevTools Protocol client for the managed browser helper.

Provides the four capabilities this helper actually needs: attach to an
already-running browser, enumerate its page targets, navigate one target, and
evaluate a script inside it. Nothing here starts, installs, downloads or
configures a browser; the caller owns the process and its profile directory.

The surface mirrors what the helper previously called on the third-party
driver, so call sites stay unchanged:

    browser = CdpBrowser(CdpOptions().set_address(address).existing_only(True))
    browser.id, browser.tab_ids, browser.get_tab(target_id)
    tab.tab_id, tab.url, tab.get(url, timeout=...)
    tab.run_js(script, *args, timeout=...), tab.run_cdp(method, **params)
"""

from __future__ import annotations

import json
from time import monotonic, sleep

import requests
from websocket import WebSocketTimeoutException, create_connection

DEFAULT_TIMEOUT_SECONDS = 30.0
_READY_STATE_POLL_SECONDS = 0.05


class CdpError(Exception):
    """The browser endpoint could not satisfy a request."""


class CdpOptions:
    """Fluent options object; only an address and attach-only mode are honoured."""

    def __init__(self):
        self._address = ""
        self._existing_only = False

    def set_address(self, address):
        self._address = str(address)
        return self

    def existing_only(self, value=True):
        self._existing_only = bool(value)
        return self

    @property
    def address(self):
        return self._address


class _Connection:
    """One websocket to one CDP target, matching replies to request ids."""

    def __init__(self, websocket_url, timeout):
        self._websocket = create_connection(websocket_url, timeout=timeout,
                                            suppress_origin=True)
        self._next_id = 0

    def send(self, method, params=None, timeout=DEFAULT_TIMEOUT_SECONDS):
        self._next_id += 1
        request_id = self._next_id
        message = {"id": request_id, "method": method}
        if params:
            message["params"] = params
        deadline = monotonic() + float(timeout)
        try:
            self._websocket.settimeout(float(timeout))
            self._websocket.send(json.dumps(message))
        except Exception as error:
            raise CdpError(method + ": send failed") from error
        while True:
            remaining = deadline - monotonic()
            if remaining <= 0:
                raise CdpError(method + ": timed out")
            try:
                self._websocket.settimeout(remaining)
                raw = self._websocket.recv()
            except WebSocketTimeoutException as error:
                raise CdpError(method + ": timed out") from error
            except Exception as error:
                raise CdpError(method + ": receive failed") from error
            try:
                reply = json.loads(raw)
            except (TypeError, ValueError):
                continue
            if type(reply) is not dict or reply.get("id") != request_id:
                continue  # protocol event, or a reply we are not waiting for
            if "error" in reply:
                raise CdpError(method + ": " + str(reply["error"]))
            result = reply.get("result")
            return result if type(result) is dict else {}

    def close(self):
        try:
            self._websocket.close()
        except Exception:
            pass


class CdpTab:
    """One page target, exposing only what the helper calls on it."""

    def __init__(self, tab_id, websocket_url, timeout=DEFAULT_TIMEOUT_SECONDS):
        self._tab_id = tab_id
        self._connection = _Connection(websocket_url, timeout)
        self._connection.send("Page.enable", timeout=timeout)
        self._connection.send("Runtime.enable", timeout=timeout)

    @property
    def tab_id(self):
        return self._tab_id

    @property
    def url(self):
        result = self._connection.send(
            "Runtime.evaluate",
            {"expression": "location.href", "returnByValue": True})
        value = result.get("result")
        if type(value) is dict and value.get("type") == "string":
            return value.get("value", "")
        return ""

    def get(self, url, timeout=None, retry=0, interval=0):
        """Navigate, then wait for the document to reach a complete ready state."""
        budget = DEFAULT_TIMEOUT_SECONDS if timeout is None else float(timeout)
        attempts = max(0, int(retry)) + 1
        last_error = None
        for attempt in range(attempts):
            if attempt and interval:
                sleep(float(interval))
            deadline = monotonic() + budget
            try:
                self._connection.send("Page.navigate", {"url": url}, timeout=budget)
                self._wait_document_complete(deadline)
                return True
            except CdpError as error:
                last_error = error
        raise last_error if last_error is not None else CdpError("navigation failed")

    def _wait_document_complete(self, deadline):
        while True:
            remaining = deadline - monotonic()
            if remaining <= 0:
                raise CdpError("navigation did not complete in time")
            result = self._connection.send(
                "Runtime.evaluate",
                {"expression": "document.readyState", "returnByValue": True},
                timeout=remaining)
            value = result.get("result")
            if type(value) is dict and value.get("value") == "complete":
                return
            sleep(_READY_STATE_POLL_SECONDS)

    def run_js(self, script, *args, timeout=None):
        """Run a function body in the page and await a promise it may return.

        Mirrors the previous driver: the script is wrapped as ``function(){...}``
        and reads its inputs through ``arguments[n]``.
        """
        budget = DEFAULT_TIMEOUT_SECONDS if timeout is None else float(timeout)
        deadline = monotonic() + budget
        document = self._connection.send(
            "Runtime.evaluate",
            {"expression": "document", "returnByValue": False},
            timeout=budget)
        result = document.get("result")
        object_id = result.get("objectId") if type(result) is dict else None
        if not object_id:
            raise CdpError("page document is unavailable")
        remaining = deadline - monotonic()
        if remaining <= 0:
            raise CdpError("run_js: timed out")
        response = self._connection.send("Runtime.callFunctionOn", {
            "functionDeclaration": "function(){" + script + "}",
            "objectId": object_id,
            "arguments": [{"value": argument} for argument in args],
            "returnByValue": True,
            "awaitPromise": True,
            "userGesture": True,
        }, timeout=remaining)
        if "exceptionDetails" in response:
            raise CdpError("script raised: " + str(response["exceptionDetails"]))
        value = response.get("result")
        return value.get("value") if type(value) is dict else None

    def run_cdp(self, method, **params):
        """Send one raw protocol command; keyword arguments become its params."""
        timeout = params.pop("_timeout", DEFAULT_TIMEOUT_SECONDS)
        params.pop("_ignore", None)
        return self._connection.send(method, params, timeout=timeout)

    def close(self):
        self._connection.close()


class CdpBrowser:
    """A browser that is already running; this attaches, it never launches."""

    def __init__(self, options, timeout=DEFAULT_TIMEOUT_SECONDS):
        self._address = options.address if isinstance(options, CdpOptions) else str(options)
        if not self._address:
            raise CdpError("no browser address was supplied")
        self._timeout = float(timeout)
        self._tabs = {}
        version = self._endpoint_json("/json/version")
        websocket_url = version.get("webSocketDebuggerUrl", "") if type(version) is dict else ""
        if not websocket_url:
            raise CdpError("browser endpoint did not advertise a debugger url")
        self.id = websocket_url.split("/")[-1]

    def _endpoint_json(self, path):
        try:
            response = requests.get("http://" + self._address + path, timeout=self._timeout)
            response.raise_for_status()
            return response.json()
        except Exception as error:
            raise CdpError("browser endpoint " + path + " is unreachable") from error

    def _page_targets(self):
        targets = self._endpoint_json("/json/list")
        if type(targets) is not list:
            return []
        return [target for target in targets
                if type(target) is dict and target.get("type") == "page" and target.get("id")]

    @property
    def tab_ids(self):
        return tuple(target["id"] for target in self._page_targets())

    def get_tab(self, tab_id):
        tab = self._tabs.get(tab_id)
        if tab is not None:
            return tab
        for target in self._page_targets():
            if target.get("id") != tab_id:
                continue
            websocket_url = target.get("webSocketDebuggerUrl", "")
            if not websocket_url:
                raise CdpError("target did not advertise a debugger url")
            tab = CdpTab(tab_id, websocket_url, self._timeout)
            self._tabs[tab_id] = tab
            return tab
        raise CdpError("target not found")

    def close(self):
        for tab in self._tabs.values():
            tab.close()
        self._tabs.clear()
