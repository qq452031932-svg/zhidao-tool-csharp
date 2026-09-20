"""Managed Chromium helper with fixed, narrowly scoped actions.

stdout is protocol-only. stderr is restricted to the fixed event codes below.
"""

from __future__ import annotations

import sys

# The host applies a best-effort no-write ACL for this helper's process lifetime. It blocks
# ordinary Python/import writes, but is not a security boundary against the same Windows owner,
# who can deliberately regain WRITE_DAC. A protected installation identity can strengthen this
# later. Set this before protocol/third-party imports so normal imports never attempt pyc writes.
sys.dont_write_bytecode = True

import io
import os
import time
from urllib.parse import urlsplit

import identity_query
import protocol
import transfer_source
import transfer_source_listing
import transfer_directory_listing
import transfer_picker
import transfer_directory_mutation
import transfer_target_baseline
import transfer_submission
import share_submission

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
    ):
        return "Rejected"
    if parsed.path == "/" and not parsed.query and not parsed.fragment:
        if parsed.hostname == "pan.baidu.com":
            return "PanRoot"
        if parsed.hostname == "passport.baidu.com":
            return "PassportRoot"
    # Exact route observed in the user's logged-in screenshot (2026-08-28).
    # This recognizes a page location only, not a logged-in account or stable UID.
    if (parsed.hostname == "pan.baidu.com" and parsed.path == "/disk/main"
            and parsed.query in ("", "from=homeFlow")
            and parsed.fragment == "/index?category=all"):
        return "PanMain"
    return "Rejected"


def observe_bound_target(
        payload,
        connector,
        *,
        monotonic=time.monotonic,
        sleeper=time.sleep,
        is_cancelled=lambda: False):
    """Observe one immutable binding with a cooperative soft deadline.

    A synchronous driver call cannot be preempted here. We check before and after every call,
    pass the remaining budget where the driver accepts one, and discard any result returned
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
        if route not in ("PanRoot", "PanMain", "PassportRoot"):
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
            observed_url = target.current_url(remaining)
            route = classify_route(observed_url)
            terminal, remaining = budget()
            if terminal:
                return terminal
            if route in ("PanRoot", "PanMain"):
                result = target.observe_identity(remaining)
                terminal, remaining = budget()
                if terminal:
                    return terminal
                # Identity is accepted only for the same live browser, target,
                # and location after the asynchronous read has completed.
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
                current_url = current.current_url(remaining)
                terminal, remaining = budget()
                if terminal:
                    return terminal
                if current_url != observed_url or classify_route(current_url) not in ("PanRoot", "PanMain"):
                    return {"kind": "BrowserUnavailable"}
                if result.get("kind") != "LoginRequired":
                    return result
                sleeper(min(2.0, remaining))
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


class _CdpTarget:
    def __init__(self, tab):
        self._tab = tab

    @property
    def target_id(self):
        return self._tab.tab_id

    def current_url(self, _remaining_seconds):
        return self._tab.url

    def navigate(self, value, remaining_seconds):
        self._tab.get(value, timeout=remaining_seconds, retry=0, interval=0)

    def observe_identity(self, remaining_seconds):
        return identity_query.observe_identity(self._tab, remaining_seconds)

    def observe_source_stage(self, remaining_seconds):
        return transfer_source.observe_source_stage(self._tab, remaining_seconds)

    def observe_source_listing(self, expected_uid, expected_source_url, remaining_seconds):
        return transfer_source_listing.observe_source_listing(
            self._tab, expected_uid, expected_source_url, remaining_seconds)

    def submit_extraction_code(self, fixed_code, remaining_seconds):
        return transfer_source.submit_extraction_code(self._tab, fixed_code, remaining_seconds)

    def observe_directory_listing(self, expected_uid, expected_source_url, remaining_seconds):
        return transfer_directory_listing.observe_directory_listing(
            self._tab, expected_uid, expected_source_url, remaining_seconds)

    def open_transfer_picker(self, expected_uid, expected_source_url, expected_source, remaining_seconds):
        return transfer_picker.open_picker(
            self._tab, expected_uid, expected_source_url, expected_source, remaining_seconds)

    def mutate_transfer_directory(self, action, payload, remaining_seconds):
        return transfer_directory_mutation.mutate_directory(self._tab, action, payload, remaining_seconds)

    def observe_transfer_picker_closed(self, payload, remaining_seconds):
        return transfer_directory_mutation.observe_picker_closed(self._tab, payload, remaining_seconds)

    def open_target_root(self, expected_uid, remaining_seconds):
        self._tab.get("https://pan.baidu.com/disk/main#/index?category=all",
                      timeout=remaining_seconds, retry=0, interval=0)
        value = self._tab.run_js(transfer_target_baseline.TARGET_ROOT_PROOF_SCRIPT,
                                 expected_uid, remaining_seconds * 1000.0,
                                 timeout=remaining_seconds)
        if type(value) is dict and set(value) == {"kind"} \
                and value["kind"] in ({"RootOpened"} | set(protocol.TARGET_DENIAL_KINDS)):
            return value
        return {"kind": "PageNotValidated"}

    def read_target_inventory(self, payload, expected_id, expected_path, expected_ancestors,
                              remaining_seconds):
        value = self._tab.run_js(
            transfer_target_baseline.TARGET_INVENTORY_PASS_ONE_SCRIPT,
            payload["expectedBaiduUserId"], remaining_seconds * 1000.0,
            payload["browserId"], payload["targetId"], payload["devToolsEndpoint"],
            expected_id, expected_path, expected_ancestors, timeout=remaining_seconds)
        try:
            protocol.validate_target_inventory_result(value, require_fingerprint=False)
            return value
        except Exception:
            return {"kind": "PageNotValidated"}

    def enter_target_child(self, payload, observed_parent, remaining_seconds):
        value = self._tab.run_js(
            transfer_target_baseline.TARGET_ENTER_CHILD_SCRIPT,
            payload["expectedBaiduUserId"], remaining_seconds * 1000.0,
            payload["browserId"], payload["targetId"], payload["devToolsEndpoint"],
            observed_parent, payload["childExactId"], payload["childServerName"],
            payload["childCanonicalPath"],
            payload["expectedParent"]["parentObservationFingerprint"], timeout=remaining_seconds)
        allowed = {
            ("DirectoryEntered", True), ("OutcomeUnknown", True),
            ("PageNotValidated", False), ("LoginRequired", False),
            ("VerificationRequired", False), ("AccountIdentityMismatch", False),
            ("TimedOut", False), ("Cancelled", False), ("BrowserUnavailable", False),
        }
        if type(value) is dict and set(value) == {"kind", "navigationAttempted"} \
                and type(value["navigationAttempted"]) is bool \
                and (value["kind"], value["navigationAttempted"]) in allowed:
            return value
        return {"kind": "PageNotValidated", "navigationAttempted": False}

    def restore_source(self, payload, remaining_seconds):
        self._tab.get(payload["sourceUri"], timeout=remaining_seconds, retry=0, interval=0)
        value = self._tab.run_js(
            transfer_target_baseline.SOURCE_RESTORE_PROOF_SCRIPT,
            payload["expectedBaiduUserId"], remaining_seconds * 1000.0,
            payload["sourceUri"], payload["expectedSource"], timeout=remaining_seconds)
        if type(value) is dict and set(value) == {"kind"}:
            return value
        return {"kind": "PageNotValidated"}

    def observe_transfer_submission_ready(self, payload, remaining_seconds):
        return transfer_submission.observe_readiness(self._tab, payload, remaining_seconds)

    def submit_transfer_once(self, payload, remaining_seconds):
        return transfer_submission.submit_once(self, payload, remaining_seconds)

    def run_transfer_submit_isolated(self, context, readiness_fingerprint,
                                     computed_readiness_fingerprint, budget_milliseconds,
                                     *, timeout):
        """Run the sole fixed submit program synchronously in a fresh isolated world.

        Isolation is established before the possibly mutating Runtime call. A failure before that
        boundary is therefore known not dispatched; a transport failure during callFunctionOn is
        allowed to propagate so ``submit_once`` conservatively reports an unknown outcome.
        """
        try:
            if self.target_id != context["targetId"]:
                raise ValueError("target binding mismatch")
            frame_tree = self._tab.run_cdp("Page.getFrameTree")
            frame = frame_tree["frameTree"]["frame"]
            frame_id = frame["id"]
            if (type(frame_id) is not str or not frame_id
                    or frame.get("url") != context["sourceUri"]):
                raise ValueError("invalid top frame")
            isolated = self._tab.run_cdp(
                "Page.createIsolatedWorld", frameId=frame_id, grantUniveralAccess=False)
            execution_context_id = isolated["executionContextId"]
            if type(execution_context_id) is not int or execution_context_id <= 0:
                raise ValueError("invalid isolated execution context")
        except Exception:
            return {"kind": "PageNotValidated", "dispatchState": "KnownNotDispatched"}

        function_declaration = "function(){" + transfer_submission.SUBMIT_TRANSFER_ONCE_SCRIPT + "\n}"
        response = self._tab.run_cdp(
            "Runtime.callFunctionOn",
            functionDeclaration=function_declaration,
            executionContextId=execution_context_id,
            arguments=[{"value": context}, {"value": readiness_fingerprint},
                       {"value": computed_readiness_fingerprint}, {"value": budget_milliseconds}],
            returnByValue=True,
            awaitPromise=False,
            userGesture=True,
        )
        if type(response) is not dict or "exceptionDetails" in response:
            raise RuntimeError("isolated submission failed")
        result = response.get("result")
        if (type(result) is not dict or not {"type", "value"} <= set(result)
                or result["type"] != "object"):
            raise RuntimeError("invalid isolated submission result")
        return result["value"]

    def observe_transfer_submission_outcome(self, payload, remaining_seconds):
        return transfer_submission.observe_outcome(self._tab, payload, remaining_seconds)

    def read_share_official_listing(self, payload, remaining_seconds):
        return share_submission.read_official_listing(self._tab, payload, remaining_seconds)

    def submit_share_once(self, payload, remaining_seconds):
        return share_submission.submit_once(self, payload, remaining_seconds)

    def run_share_submit_isolated(self, context, baseline_ids, baseline_observation,
                                  current_ids, current_observation, budget_milliseconds,
                                  *, timeout):
        """Run the sole fixed create-share click synchronously in a fresh isolated world."""
        try:
            if self.target_id != context["targetId"]:
                raise ValueError("target binding mismatch")
            frame_tree = self._tab.run_cdp("Page.getFrameTree")
            frame = frame_tree["frameTree"]["frame"]
            frame_id, frame_url = frame["id"], frame["url"]
            if (type(frame_id) is not str or not frame_id or type(frame_url) is not str or
                    not frame_url.startswith("https://pan.baidu.com/disk/main")):
                raise ValueError("invalid top frame")
            isolated = self._tab.run_cdp(
                "Page.createIsolatedWorld", frameId=frame_id, grantUniveralAccess=False)
            execution_context_id = isolated["executionContextId"]
            if type(execution_context_id) is not int or execution_context_id <= 0:
                raise ValueError("invalid isolated execution context")
        except Exception:
            return {"kind": "PageNotValidated", "dispatchState": "KnownNotDispatched"}

        response = self._tab.run_cdp(
            "Runtime.callFunctionOn",
            functionDeclaration="function(){" + share_submission.SUBMIT_SHARE_ONCE_SCRIPT + "\n}",
            executionContextId=execution_context_id,
            arguments=[{"value": context}, {"value": baseline_ids}, {"value": baseline_observation},
                       {"value": current_ids}, {"value": current_observation},
                       {"value": budget_milliseconds}],
            returnByValue=True,
            awaitPromise=False,
            userGesture=True,
        )
        if type(response) is not dict or "exceptionDetails" in response:
            raise RuntimeError("isolated share submission failed")
        result = response.get("result")
        if (type(result) is not dict or not {"type", "value"} <= set(result) or
                result["type"] != "object"):
            raise RuntimeError("invalid isolated share submission result")
        return result["value"]

    def observe_share_submission_outcome(self, payload, remaining_seconds):
        return share_submission.observe_outcome(self._tab, payload, remaining_seconds)


class _CdpConnector:
    def __init__(self, browser):
        self._browser = browser
        self._targets = {}

    @property
    def browser_id(self):
        try:
            import uuid
            return str(uuid.UUID(str(self._browser.id)))
        except (ValueError, TypeError, AttributeError):
            return ""

    def get_target(self, target_id, _remaining_seconds):
        if target_id not in self._browser.tab_ids:
            return None
        target = self._targets.get(target_id)
        if target is None:
            target = _CdpTarget(self._browser.get_tab(target_id))
            self._targets[target_id] = target
        return target


def observe_account_identity(payload, is_cancelled=lambda: False):
    """Attach to one pre-verified endpoint and one exact target; perform no mutation."""
    try:
        from cdp_client import CdpBrowser, CdpOptions

        endpoint = urlsplit(payload["devToolsEndpoint"])
        address = f"{endpoint.hostname}:{endpoint.port}"
        options = CdpOptions().set_address(address).existing_only(True)
        browser = CdpBrowser(options)
        return observe_bound_target(
            payload,
            _CdpConnector(browser),
            is_cancelled=is_cancelled,
        )
    except Exception:
        return {"kind": "BrowserUnavailable"}


def open_transfer_source(payload, is_cancelled=lambda: False):
    """Attach existing-only, then open a validated source in the one exact target."""
    def attach(budget):
        def load_driver(_remaining):
            from cdp_client import CdpBrowser, CdpOptions
            return CdpBrowser, CdpOptions
        chromium, chromium_options = budget.call(load_driver)
        endpoint = urlsplit(payload["devToolsEndpoint"])
        address = f"{endpoint.hostname}:{endpoint.port}"
        options = budget.call(lambda _: chromium_options())
        options = budget.call(lambda _: options.set_address(address))
        options = budget.call(lambda _: options.existing_only(True))
        browser = budget.call(lambda _: chromium(options))
        return _CdpConnector(browser)
    return transfer_source.open_with_connector(
        payload, attach, classify_route=classify_route, is_cancelled=is_cancelled)


def _write_event(error_writer, event_code):
    error_writer.write(event_code + "\n")
    flush = getattr(error_writer, "flush", None)
    if flush is not None:
        flush()


def read_transfer_source(payload, is_cancelled=lambda: False):
    """Attach existing-only and read a bounded candidate listing; never navigate."""
    def attach(budget):
        def load_driver(_remaining):
            from cdp_client import CdpBrowser, CdpOptions
            return CdpBrowser, CdpOptions
        chromium, chromium_options = budget.call(load_driver)
        endpoint = urlsplit(payload["devToolsEndpoint"])
        address = f"{endpoint.hostname}:{endpoint.port}"
        options = budget.call(lambda _: chromium_options())
        options = budget.call(lambda _: options.set_address(address))
        options = budget.call(lambda _: options.existing_only(True))
        browser = budget.call(lambda _: chromium(options))
        return _CdpConnector(browser)
    return transfer_source_listing.read_with_connector(
        payload, attach, classify_route=classify_route, is_cancelled=is_cancelled)


def read_transfer_directory(payload, is_cancelled=lambda: False):
    """Read folder candidates in an already-open picker; never open or navigate it."""
    def attach(budget):
        def load_driver(_remaining):
            from cdp_client import CdpBrowser, CdpOptions
            return CdpBrowser, CdpOptions
        chromium, chromium_options = budget.call(load_driver)
        endpoint = urlsplit(payload["devToolsEndpoint"])
        address = f"{endpoint.hostname}:{endpoint.port}"
        options = budget.call(lambda _: chromium_options())
        options = budget.call(lambda _: options.set_address(address))
        options = budget.call(lambda _: options.existing_only(True))
        browser = budget.call(lambda _: chromium(options))
        return _CdpConnector(browser)
    return transfer_directory_listing.read_with_connector(
        payload, attach, classify_route=classify_route, is_cancelled=is_cancelled)


def open_transfer_picker(payload, is_cancelled=lambda: False):
    """Attach existing-only and dispatch one source-validated picker click; never retry."""
    def attach(budget):
        def load_driver(_remaining):
            from cdp_client import CdpBrowser, CdpOptions
            return CdpBrowser, CdpOptions
        chromium, chromium_options = budget.call(load_driver)
        endpoint = urlsplit(payload["devToolsEndpoint"])
        address = f"{endpoint.hostname}:{endpoint.port}"
        options = budget.call(lambda _: chromium_options())
        options = budget.call(lambda _: options.set_address(address))
        options = budget.call(lambda _: options.existing_only(True))
        browser = budget.call(lambda _: chromium(options))
        return _CdpConnector(browser)
    return transfer_picker.open_with_connector(
        payload, attach, classify_route=classify_route, is_cancelled=is_cancelled)


def _attach_existing(payload, budget):
    def load_driver(_remaining):
        from cdp_client import CdpBrowser, CdpOptions
        return CdpBrowser, CdpOptions
    chromium, chromium_options = budget.call(load_driver)
    endpoint = urlsplit(payload["devToolsEndpoint"]); address = f"{endpoint.hostname}:{endpoint.port}"
    options = budget.call(lambda _: chromium_options()); options = budget.call(lambda _: options.set_address(address))
    options = budget.call(lambda _: options.existing_only(True)); return _CdpConnector(budget.call(lambda _: chromium(options)))


def mutate_transfer_directory(action, payload, is_cancelled=lambda: False):
    try:
        protocol.validate_directory_mutation_payload(action, payload)
        budget = transfer_source.OperationBudget(payload["timeoutMilliseconds"], time.monotonic, is_cancelled)
        return transfer_directory_mutation.dispatch_bound(action, payload, _attach_existing(payload, budget), budget=budget)
    except transfer_source._Stopped as stopped:
        return {"kind": stopped.kind}
    except Exception:
        return {"kind": "BrowserUnavailable"}


def observe_transfer_picker_closed(payload, is_cancelled=lambda: False):
    try:
        protocol.validate_observe_transfer_picker_closed_payload(payload)
        budget = transfer_source.OperationBudget(payload["timeoutMilliseconds"], time.monotonic, is_cancelled)
        connector = _attach_existing(payload, budget); call = budget.call
        if call(lambda _: connector.browser_id) != payload["browserId"]: return {"kind":"BrowserUnavailable"}
        target = call(lambda remaining: connector.get_target(payload["targetId"], remaining))
        if target is None: return {"kind":"BrowserUnavailable"}
        def binding():
            if call(lambda _: connector.browser_id) != payload["browserId"]:
                raise transfer_source._Stopped("BrowserUnavailable")
            if call(lambda remaining: connector.get_target(payload["targetId"], remaining)) is not target:
                raise transfer_source._Stopped("BrowserUnavailable")
            if call(lambda _: target.target_id) != payload["targetId"]:
                raise transfer_source._Stopped("BrowserUnavailable")
            if call(lambda _: connector.browser_id) != payload["browserId"]:
                raise transfer_source._Stopped("BrowserUnavailable")
        def bound(operation):
            binding(); value = call(operation); binding(); return value
        return bound(lambda remaining: target.observe_transfer_picker_closed(payload, remaining))
    except transfer_source._Stopped as stopped: return {"kind": stopped.kind}
    except Exception: return {"kind":"BrowserUnavailable"}


def handle_transfer_target_baseline(action, payload, is_cancelled=lambda: False):
    """Attach existing-only and execute one of the four audited target-baseline actions."""
    try:
        protocol.validate_target_baseline_request(action, payload)
        budget = transfer_source.OperationBudget(payload["timeoutMilliseconds"], time.monotonic, is_cancelled)
        connector = _attach_existing(payload, budget)
        return transfer_target_baseline.dispatch_bound(action, payload, connector, budget=budget)
    except transfer_source._Stopped as stopped:
        if action == "ReadTransferTargetDirectory":
            return {"kind": stopped.kind}
        return {"kind": stopped.kind, "navigationState": "KnownNotDispatched"}
    except Exception:
        if action == "ReadTransferTargetDirectory":
            return {"kind": "BrowserUnavailable"}
        return {"kind": "BrowserUnavailable", "navigationState": "KnownNotDispatched"}


def handle_transfer_submission(action, payload, is_cancelled=lambda: False):
    """Attach existing-only and execute exactly one fixed final-submission action."""
    try:
        protocol.validate_transfer_submission_request(action, payload)
        budget = transfer_source.OperationBudget(payload["timeoutMilliseconds"], time.monotonic, is_cancelled)
        connector = _attach_existing(payload, budget)
        return transfer_submission.dispatch_bound(action, payload, connector, budget=budget)
    except transfer_source._Stopped as stopped:
        if action == "SubmitTransferOnce":
            return {"kind": stopped.kind, "dispatchState": "KnownNotDispatched"}
        return {"kind": stopped.kind}
    except Exception:
        if action == "SubmitTransferOnce":
            return {"kind": "BrowserUnavailable", "dispatchState": "KnownNotDispatched"}
        return {"kind": "BrowserUnavailable"}


def handle_share_submission(action, payload, is_cancelled=lambda: False):
    """Attach existing-only and execute one fixed single-item share action."""
    try:
        protocol.validate_share_submission_request(action, payload)
        budget = transfer_source.OperationBudget(payload["timeoutMilliseconds"], time.monotonic, is_cancelled)
        connector = _attach_existing(payload, budget)
        return share_submission.dispatch_bound(action, payload, connector, budget=budget)
    except transfer_source._Stopped as stopped:
        if action == "SubmitShareOnce":
            return {"kind": stopped.kind, "dispatchState": "KnownNotDispatched"}
        return {"kind": stopped.kind}
    except Exception:
        if action == "SubmitShareOnce":
            return {"kind": "BrowserUnavailable", "dispatchState": "KnownNotDispatched"}
        return {"kind": "BrowserUnavailable"}


def submit_extraction_code(payload, is_cancelled=lambda: False):
    try:
        protocol.validate_submit_extraction_code_payload(payload)
        budget = transfer_source.OperationBudget(payload["timeoutMilliseconds"], time.monotonic, is_cancelled)
        connector = _attach_existing(payload, budget)
        return transfer_source.submit_extraction_bound(payload, connector, budget=budget)
    except transfer_source._Stopped as stopped:
        return {"kind": stopped.kind, "dispatchState": "KnownNotDispatched"}
    except Exception:
        return {"kind": "BrowserUnavailable", "dispatchState": "KnownNotDispatched"}


def run(
        reader,
        writer,
        error_writer,
        *,
        helper_pid=None,
        parent_pid=None,
        observer=None,
        source_opener=None,
        source_reader=None,
        directory_reader=None,
        picker_opener=None,
        directory_mutator=None,
        picker_closed_observer=None,
        target_baseline_handler=None,
        transfer_submission_handler=None,
        share_submission_handler=None,
        extraction_handler=None,
        max_requests=protocol.MAX_REQUESTS_PER_SESSION):
    helper_pid = os.getpid() if helper_pid is None else helper_pid
    parent_pid = os.getppid() if parent_pid is None else parent_pid
    observer = observe_account_identity if observer is None else observer
    source_opener = open_transfer_source if source_opener is None else source_opener
    source_reader = read_transfer_source if source_reader is None else source_reader
    directory_reader = read_transfer_directory if directory_reader is None else directory_reader
    picker_opener = open_transfer_picker if picker_opener is None else picker_opener
    directory_mutator = mutate_transfer_directory if directory_mutator is None else directory_mutator
    picker_closed_observer = observe_transfer_picker_closed if picker_closed_observer is None else picker_closed_observer
    target_baseline_handler = handle_transfer_target_baseline if target_baseline_handler is None else target_baseline_handler
    transfer_submission_handler = handle_transfer_submission if transfer_submission_handler is None else transfer_submission_handler
    share_submission_handler = handle_share_submission if share_submission_handler is None else share_submission_handler
    extraction_handler = submit_extraction_code if extraction_handler is None else extraction_handler
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
            elif action == "OpenTransferSource":
                result = source_opener(message["payload"])
            elif action == "ReadTransferSource":
                result = source_reader(message["payload"])
            elif action == "ReadTransferDirectory":
                result = directory_reader(message["payload"])
            elif action == "OpenTransferPicker":
                result = picker_opener(message["payload"])
            elif action in protocol.DIRECTORY_MUTATION_ACTIONS:
                result = directory_mutator(action, message["payload"])
            elif action == "ObserveTransferPickerClosed":
                result = picker_closed_observer(message["payload"])
            elif action in protocol.TARGET_BASELINE_ACTIONS:
                result = target_baseline_handler(action, message["payload"])
            elif action in protocol.TRANSFER_SUBMISSION_ACTIONS:
                result = transfer_submission_handler(action, message["payload"])
            elif action in protocol.SHARE_SUBMISSION_ACTIONS:
                result = share_submission_handler(action, message["payload"])
            elif action == "SubmitExtractionCode":
                result = extraction_handler(message["payload"])
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
