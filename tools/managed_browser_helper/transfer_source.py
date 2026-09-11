"""One bound source navigation and a conservative, read-only page hint.

The synthetic DOM heuristics below are candidates, not live-site acceptance,
identity evidence, inventory, completeness, or permission to save anything.
"""

from __future__ import annotations

import math
import re
import time

import protocol

PAGE_HINTS = ("SourceOpened", "ExtractionRequired", "LoginRequired", "VerificationRequired", "PageNotValidated")
STAGE_KINDS = PAGE_HINTS + ("BrowserUnavailable", "TimedOut", "Cancelled")

# Immutable built-in script: the only argument is a numeric time budget. It
# exports a finite kind, not page text, selectors, IDs, URLs or resource rows.
SOURCE_STAGE_SCRIPT = r"""
return (function (budgetMs) {
    'use strict';
    const unknown = () => ({kind: 'PageNotValidated'});
    if (typeof budgetMs !== 'number' || !Number.isFinite(budgetMs)) return unknown();
    if (budgetMs <= 0) return {kind: 'TimedOut'};
    const deadline = Date.now() + budgetMs;
    const expired = {};
    const originalDocument = document;
    const originalHref = location.href;
    function guard() {
        if (Date.now() >= deadline) throw expired;
        if (document !== originalDocument || location.href !== originalHref ||
            window.top !== window || document.defaultView !== window ||
            !/^https:\/\/pan\.baidu\.com\/s\/[A-Za-z0-9_-]{1,128}(?:\?pwd=[A-Za-z0-9]{4})?$/.test(location.href) ||
            /\s/.test(location.href)) throw new Error();
    }
    function nodes(root, selector) {
        guard();
        const result = root.querySelectorAll(selector);
        guard();
        if (!result || result.length > 256) throw new Error();
        return Array.from(result);
    }
    function visible(node) {
        guard();
        if (!node || node.nodeType !== 1 || !node.isConnected || !node.getClientRects().length) return false;
        let ancestor = node;
        for (let depth = 0; ancestor && depth < 32; depth++, ancestor = ancestor.parentElement) {
            guard();
            if (ancestor.hidden || ancestor.getAttribute('aria-hidden') === 'true') return false;
            const style = getComputedStyle(ancestor);
            if (style.display === 'none' || style.visibility === 'hidden' ||
                style.visibility === 'collapse' || Number(style.opacity) === 0) return false;
        }
        return !ancestor;
    }
    function label(node) {
        guard();
        const value = node.textContent;
        return typeof value === 'string' && value.length <= 256 ? value.trim() : '';
    }
    try {
        guard();
        const extraction = nodes(document, 'form input[name="pwd"], form input[name="accessCode"], form input[placeholder*="提取码"]')
            .some(input => {
                if (!visible(input)) return false;
                const form = input.closest('form');
                return form && visible(form) && nodes(form, 'button, input[type="submit"], [role="button"]')
                    .some(control => visible(control) && ['提取文件', '提取'].includes(label(control)));
            });
        const dialogs = nodes(document, '[role="dialog"], .login-dialog, .security-verification, .verify-dialog');
        const login = dialogs.some(node => visible(node) && ['登录百度网盘', '请登录百度网盘', '登录百度帐号'].includes(label(node)));
        const verification = dialogs.some(node => visible(node) && ['安全验证', '请完成验证', '请完成安全验证'].includes(label(node)));
        const resources = nodes(document, '.share-file-list, .share-list, #share-file-list')
            .some(surface => visible(surface) && nodes(surface, '.file-item, [role="row"], [data-fsid]').some(visible));
        const save = nodes(document, 'button, a[role="button"], [role="button"]')
            .some(control => visible(control) && ['保存到网盘', '保存到我的网盘'].includes(label(control)));
        const hints = [];
        if (extraction) hints.push('ExtractionRequired');
        if (login) hints.push('LoginRequired');
        if (verification) hints.push('VerificationRequired');
        if (resources && save) hints.push('SourceOpened');
        guard();
        return hints.length === 1 ? {kind: hints[0]} : unknown();
    } catch (error) {
        return error === expired ? {kind: 'TimedOut'} : unknown();
    }
})(arguments[0]);
"""

EXTRACTION_SUBMIT_SCRIPT = r"""
return (function (fixedCode, budgetMs) {
    'use strict';
    const denied = () => ({kind: 'PageNotValidated', dispatchState: 'KnownNotDispatched'});
    if (typeof fixedCode !== 'string' || !/^[A-Za-z0-9]{4}$/.test(fixedCode) ||
        typeof budgetMs !== 'number' || !Number.isFinite(budgetMs) || budgetMs <= 0) return denied();
    const deadline = Date.now() + budgetMs;
    try {
        if (window.top !== window || Date.now() >= deadline ||
            !/^https:\/\/pan\.baidu\.com\/s\/[A-Za-z0-9_-]{1,128}(?:\?pwd=[A-Za-z0-9]{4})?$/.test(location.href))
            return denied();
        const inputs = Array.from(document.querySelectorAll(
            'form input[name="pwd"], form input[name="accessCode"], form input[placeholder*="提取码"]'))
            .filter(node => node && node.isConnected && node.getClientRects().length);
        if (inputs.length !== 1) return denied();
        const form = inputs[0].closest('form');
        if (!form || !form.getClientRects().length) return denied();
        const controls = Array.from(form.querySelectorAll('button, input[type="submit"], [role="button"]'))
            .filter(node => node && node.isConnected && node.getClientRects().length &&
                ['提取文件', '提取'].includes((node.textContent || node.value || '').trim()));
        if (controls.length !== 1 || Date.now() >= deadline) return denied();
        const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
        setter.call(inputs[0], fixedCode);
        inputs[0].dispatchEvent(new Event('input', {bubbles: true}));
        inputs[0].dispatchEvent(new Event('change', {bubbles: true}));
        if (inputs[0].value !== fixedCode || Date.now() >= deadline) return denied();
        controls[0].click();
        return {kind: 'ClickAttempted', dispatchState: 'ClickDispatched'};
    } catch (_) { return denied(); }
})(arguments[0], arguments[1]);
"""


def _finite_result(value, kinds, fallback):
    if type(value) is dict and set(value) == {"kind"} and type(value["kind"]) is str and value["kind"] in kinds:
        return {"kind": value["kind"]}
    return {"kind": fallback}


def observe_source_stage(tab, remaining_seconds):
    if type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds):
        return {"kind": "BrowserUnavailable"}
    if remaining_seconds <= 0:
        return {"kind": "TimedOut"}
    try:
        value = tab.run_js(SOURCE_STAGE_SCRIPT, remaining_seconds * 900.0, timeout=remaining_seconds)
        return _finite_result(value, STAGE_KINDS, "PageNotValidated")
    except Exception:
        # The orchestrator checks its deadline/cancellation after this call.
        return {"kind": "BrowserUnavailable"}


def submit_extraction_code(tab, fixed_code, remaining_seconds):
    if type(fixed_code) is not str or re.fullmatch(r"[A-Za-z0-9]{4}", fixed_code) is None:
        return {"kind": "PageNotValidated", "dispatchState": "KnownNotDispatched"}
    if type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or remaining_seconds <= 0:
        return {"kind": "TimedOut", "dispatchState": "KnownNotDispatched"}
    try:
        value = tab.run_js(EXTRACTION_SUBMIT_SCRIPT, fixed_code, remaining_seconds * 800.0,
                           timeout=remaining_seconds)
        if type(value) is dict and set(value) == {"kind", "dispatchState"} and \
                value["kind"] in ("ClickAttempted", "PageNotValidated") and \
                value["dispatchState"] in ("KnownNotDispatched", "ClickDispatched"):
            return value
        return {"kind": "PageNotValidated", "dispatchState": "KnownNotDispatched"}
    except Exception:
        return {"kind": "OutcomeUnknown", "dispatchState": "ClickOutcomeUnknown"}


def submit_extraction_bound(payload, connector, *, classify_route=None, monotonic=time.monotonic,
                            is_cancelled=lambda: False, budget=None):
    dispatched = False
    try:
        protocol.validate_submit_extraction_code_payload(payload)
        budget = budget or OperationBudget(payload["timeoutMilliseconds"], monotonic, is_cancelled)
        call = budget.call
        if call(lambda _: connector.browser_id) != payload["browserId"]:
            raise _Stopped("BrowserUnavailable")
        target = call(lambda remaining: connector.get_target(payload["targetId"], remaining))
        if target is None or call(lambda _: target.target_id) != payload["targetId"]:
            raise _Stopped("BrowserUnavailable")
        expected_urls = (payload["sourceUri"], payload["sourceUri"].split("?", 1)[0])
        if call(target.current_url) not in expected_urls:
            return {"kind": "PageNotValidated", "dispatchState": "KnownNotDispatched"}
        dispatched = True
        submitted = call(lambda remaining: target.submit_extraction_code(payload["fixedCode"], remaining))
        if type(submitted) is not dict or set(submitted) != {"kind", "dispatchState"}:
            return {"kind": "OutcomeUnknown", "dispatchState": "ClickOutcomeUnknown"}
        if submitted.get("dispatchState") != "ClickDispatched":
            return submitted if submitted.get("dispatchState") == "KnownNotDispatched" else \
                {"kind": "OutcomeUnknown", "dispatchState": "ClickOutcomeUnknown"}
        stage = call(target.observe_source_stage)
        kind = stage.get("kind") if type(stage) is dict else None
        if kind == "SourceOpened":
            return {"kind": "SourceOpened", "dispatchState": "ClickDispatched"}
        if kind == "ExtractionRequired":
            return {"kind": "ExplicitlyRejected", "dispatchState": "ClickDispatched"}
        if kind in ("LoginRequired", "VerificationRequired", "PageNotValidated"):
            return {"kind": kind, "dispatchState": "ClickDispatched"}
        return {"kind": "OutcomeUnknown", "dispatchState": "ClickOutcomeUnknown"}
    except _Stopped as stopped:
        if dispatched:
            return {"kind": "OutcomeUnknown", "dispatchState": "ClickOutcomeUnknown"}
        return {"kind": stopped.kind, "dispatchState": "KnownNotDispatched"}
    except Exception:
        return {"kind": "OutcomeUnknown", "dispatchState": "ClickOutcomeUnknown"} if dispatched else \
            {"kind": "BrowserUnavailable", "dispatchState": "KnownNotDispatched"}


class _Stopped(Exception):
    def __init__(self, kind):
        self.kind = kind


class OperationBudget:
    """Shared cooperative budget, including attachment; the C# owner hard-bounds stalls."""
    def __init__(self, timeout_milliseconds, monotonic, is_cancelled):
        self.monotonic = monotonic
        self.is_cancelled = is_cancelled
        self.deadline = monotonic() + timeout_milliseconds / 1000.0

    def remaining(self):
        if self.is_cancelled():
            raise _Stopped("Cancelled")
        remaining = self.deadline - self.monotonic()
        if remaining <= 0:
            raise _Stopped("TimedOut")
        return remaining

    def call(self, operation):
        remaining = self.remaining()
        try:
            result = operation(remaining)
        except Exception:
            self.remaining()  # Deadline/cancellation wins even an exception.
            raise
        self.remaining()
        return result


def open_bound_source(payload, connector, *, classify_route, monotonic=time.monotonic,
                      is_cancelled=lambda: False, budget=None):
    """Open once, only after an exact own-drive identity and immutable binding check."""
    try:
        protocol.validate_open_transfer_source_payload(payload)
        budget = budget or OperationBudget(payload["timeoutMilliseconds"], monotonic, is_cancelled)
        call = budget.call
        browser_id = call(lambda _: connector.browser_id)
        if browser_id != payload["browserId"]:
            raise _Stopped("BrowserUnavailable")
        target = call(lambda remaining: connector.get_target(payload["targetId"], remaining))
        if target is None:
            raise _Stopped("BrowserUnavailable")

        def binding():
            if call(lambda _: connector.browser_id) != payload["browserId"]:
                raise _Stopped("BrowserUnavailable")
            current = call(lambda remaining: connector.get_target(payload["targetId"], remaining))
            if current is not target:
                raise _Stopped("BrowserUnavailable")
            if call(lambda _: target.target_id) != payload["targetId"]:
                raise _Stopped("BrowserUnavailable")
            # Detect a browser replacement during target lookup/ID reads too.
            if call(lambda _: connector.browser_id) != payload["browserId"]:
                raise _Stopped("BrowserUnavailable")

        def bound_call(operation):
            binding()
            result = call(operation)
            binding()
            return result

        initial_url = bound_call(target.current_url)
        route = classify_route(initial_url)
        if route == "PassportRoot":
            return {"kind": "LoginRequired"}
        if route not in ("PanRoot", "PanMain"):
            return {"kind": "PageNotValidated"}

        identity = bound_call(target.observe_identity)
        if bound_call(target.current_url) != initial_url:
            return {"kind": "BrowserUnavailable"}
        if type(identity) is not dict or type(identity.get("kind")) is not str:
            return {"kind": "BrowserUnavailable"}
        if identity["kind"] in ("LoginRequired", "BrowserUnavailable", "TimedOut", "Cancelled"):
            return _finite_result(identity, STAGE_KINDS, "BrowserUnavailable")
        if (identity["kind"] != "Observed" or set(identity) != {"kind", "stableBaiduUserId"} or
                not protocol._bounded_text(identity["stableBaiduUserId"])):
            return {"kind": "BrowserUnavailable"}
        if identity["stableBaiduUserId"] != payload["expectedBaiduUserId"]:
            return {"kind": "AccountIdentityMismatch"}

        source = payload["sourceUri"]
        bound_call(lambda remaining: target.navigate(source, remaining))
        opened_url = bound_call(target.current_url)
        if classify_route(opened_url) == "PassportRoot":
            return {"kind": "LoginRequired"}
        if opened_url not in (source, source.split("?", 1)[0]):
            return {"kind": "PageNotValidated"}

        stage = bound_call(target.observe_source_stage)
        if bound_call(target.current_url) != opened_url:
            return {"kind": "PageNotValidated"}
        return _finite_result(stage, STAGE_KINDS, "PageNotValidated")
    except _Stopped as stopped:
        return {"kind": stopped.kind}
    except Exception:
        return {"kind": "BrowserUnavailable"}


def open_with_connector(payload, connector_factory, *, classify_route,
                        monotonic=time.monotonic, is_cancelled=lambda: False):
    """Include existing-only attachment in the same deadline as the page operation."""
    try:
        protocol.validate_open_transfer_source_payload(payload)
        budget = OperationBudget(payload["timeoutMilliseconds"], monotonic, is_cancelled)
        connector = budget.call(lambda _: connector_factory(budget))
        return open_bound_source(payload, connector, classify_route=classify_route, budget=budget)
    except _Stopped as stopped:
        return {"kind": stopped.kind}
    except Exception:
        return {"kind": "BrowserUnavailable"}
