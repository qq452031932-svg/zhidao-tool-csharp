"""One fixed source-bound picker click, never save/create/share/navigation authority.

The DOM contract has synthetic tests only. Once mutation execution is dispatched,
transport, cancellation, deadline or binding loss is uncertain and cannot be retried.
"""

from __future__ import annotations

import math
import time

import protocol
from transfer_source import OperationBudget, _Stopped, _finite_result, STAGE_KINDS
from transfer_source_listing import SOURCE_OBSERVER_FUNCTION


OPEN_TRANSFER_PICKER_SCRIPT = r"""
return (async function (expectedUid, budgetMs, expectedSourceUrl, expectedSource) {
    'use strict';
    const unknown = () => ({kind: 'PageNotValidated'});
    const observer = (""" + SOURCE_OBSERVER_FUNCTION + r""")(expectedUid, budgetMs, expectedSourceUrl);
    if (observer.denial) return observer.denial;
    let clicked = false;
    const keys = (value, expected) => value !== null && typeof value === 'object' && !Array.isArray(value) &&
        Object.keys(value).length === expected.length && expected.every(key => Object.hasOwn(value, key));
    function expectedEvidence() {
        if (!keys(expectedSource, ['kind', 'baiduUserId', 'generationToken', 'totalCount', 'reachedEnd',
            'isStable', 'items', 'observationFingerprint']) || expectedSource.kind !== 'Observed') throw new Error();
        observer.text(expectedUid);
        observer.text(expectedSource.baiduUserId);
        observer.text(expectedSource.generationToken);
        if (expectedSource.baiduUserId !== expectedUid) throw observer.mismatch;
        if (typeof expectedSource.observationFingerprint !== 'string' ||
            !/^[a-f0-9]{64}$/.test(expectedSource.observationFingerprint) ||
            !Array.isArray(expectedSource.items) || expectedSource.items.length < 1 || expectedSource.items.length > 32 ||
            !Number.isInteger(expectedSource.totalCount) || expectedSource.totalCount !== expectedSource.items.length ||
            expectedSource.reachedEnd !== true || expectedSource.isStable !== true) throw new Error();
        const seen = new Set();
        const items = expectedSource.items.map(item => {
            if (!keys(item, ['exactId', 'kind', 'serverName']) || typeof item.exactId !== 'string' ||
                !/^[A-Za-z0-9_-]{1,128}$/.test(item.exactId) || seen.has(item.exactId) ||
                !['File', 'Folder'].includes(item.kind)) throw new Error();
            observer.text(item.serverName, false);
            seen.add(item.exactId);
            return {exactId: item.exactId, kind: item.kind, serverName: item.serverName};
        });
        items.sort((a, b) => a.exactId < b.exactId ? -1 : a.exactId > b.exactId ? 1 : 0);
        // SHA256 is validated against the original request URI by Python before attachment.
        // The live page may use that URI without its query; compare all hashed metadata here.
        return JSON.stringify({kind: 'Observed', baiduUserId: expectedUid,
            generationToken: expectedSource.generationToken, totalCount: items.length,
            reachedEnd: true, isStable: true, items});
    }
    function controls(root) {
        observer.guard();
        const dialogs = observer.nodes(document, '[role="dialog"],[role="alertdialog"]');
        if (dialogs.some(node => node !== root && observer.visible(node))) throw new Error();
        const all = observer.nodes(document, 'button,[role="button"]');
        const labelled = all.filter(node => {
            if (!observer.visible(node)) return false;
            const [aria, content] = observer.label(node);
            return ['选择保存位置', '选择保存目录'].includes(aria === null ? content : aria);
        });
        if (labelled.length !== 1 || !root.contains(labelled[0])) throw new Error();
        const node = labelled[0];
        const tag = String(node.tagName).toLowerCase();
        // Role alone must not authorize native link/input/summary default actions.
        // Non-native controls are restricted to inert generic containers with an explicit role.
        if (tag !== 'button' && (!['div', 'span'].includes(tag) || node.getAttribute('role') !== 'button')) throw new Error();
        if (all.some(other => other !== node && (other.contains(node) || node.contains(other)))) throw new Error();
        if (node.getAttribute('aria-labelledby') !== null ||
            (node.getAttribute('role') !== null && node.getAttribute('role') !== 'button')) throw new Error();
        // Native submit/reset buttons can navigate or reset a form even with an allowed label.
        if (tag === 'button' &&
            (node.form || ['submit', 'reset'].includes((node.getAttribute('type') || '').toLowerCase()))) throw new Error();
        let ancestor = node;
        for (let depth = 0; ancestor && depth < 32; depth++, ancestor = ancestor.parentElement) {
            observer.guard();
            if (['a', 'label', 'summary'].includes(String(ancestor.tagName).toLowerCase())) throw new Error();
            if (ancestor.disabled || ancestor.inert || ancestor.getAttribute('disabled') !== null ||
                ancestor.getAttribute('inert') !== null || ancestor.getAttribute('aria-disabled') === 'true' ||
                getComputedStyle(ancestor).pointerEvents === 'none') throw new Error();
        }
        if (ancestor || typeof node.click !== 'function') throw new Error();
        const attrs = ['aria-label', 'aria-labelledby', 'role', 'type', 'disabled', 'aria-disabled', 'inert']
            .map(name => node.getAttribute(name));
        observer.guard();
        return {node, click: node.click, dialogs,
            evidence: JSON.stringify([attrs, node.textContent, node.tagName])};
    }
    function sameControl(first, second) {
        return first.node === second.node && first.click === second.click && first.evidence === second.evidence &&
            first.dialogs.length === second.dialogs.length && first.dialogs.every((node, i) => node === second.dialogs[i]);
    }
    try {
        observer.guard();
        const expected = expectedEvidence();
        const first = observer.snapshot(), firstControl = controls(first.root);
        if (JSON.stringify(first.result) !== expected) return unknown();
        await Promise.resolve();
        const second = observer.snapshot(), secondControl = controls(second.root);
        if (!observer.same(first, second) || !sameControl(firstControl, secondControl) ||
            JSON.stringify(second.result) !== expected) return unknown();
        // Final source/control snapshot and click are synchronous: no await in this boundary.
        const final = observer.snapshot(), finalControl = controls(final.root);
        if (!observer.same(second, final) || !sameControl(secondControl, finalControl) ||
            JSON.stringify(final.result) !== expected) return unknown();
        observer.guard();
        clicked = true;
        finalControl.click.call(finalControl.node);
        observer.guard();
        return {kind: 'PickerOpened'};
    } catch (error) {
        return clicked ? {kind: 'OutcomeUnknown'} : observer.failure(error);
    }
})(arguments[0], arguments[1], arguments[2], arguments[3]);
"""


def open_picker(tab, expected_uid, expected_source_url, expected_source, remaining_seconds):
    """Private fixed-script adapter; the bound operation verifies the request fingerprint."""
    try:
        if (type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or
                not protocol._bounded_text(expected_uid) or not protocol.is_canonical_source_uri(expected_source_url)):
            return {"kind": "PageNotValidated"}
        protocol.validate_read_transfer_source_result(expected_source)
        if expected_source["kind"] != "Observed" or expected_source["baiduUserId"] != expected_uid:
            return {"kind": "PageNotValidated"}
        if remaining_seconds <= 0:
            return {"kind": "TimedOut"}
    except Exception:
        return {"kind": "PageNotValidated"}
    try:
        value = tab.run_js(OPEN_TRANSFER_PICKER_SCRIPT, expected_uid, remaining_seconds * 1000.0,
                           expected_source_url, expected_source, timeout=remaining_seconds)
        protocol.validate_open_transfer_picker_result(value)
        return value
    except Exception:
        return {"kind": "OutcomeUnknown"}


def open_bound_picker(payload, connector, *, classify_route, monotonic=time.monotonic,
                      is_cancelled=lambda: False, budget=None):
    """Keep exact object/browser/target binding around every call, and never retry dispatch."""
    dispatched = False
    try:
        protocol.validate_open_transfer_picker_payload(payload)
    except Exception:
        return {"kind": "PageNotValidated"}
    try:
        budget = budget or OperationBudget(payload["timeoutMilliseconds"], monotonic, is_cancelled)
        call = budget.call
        if call(lambda _: connector.browser_id) != payload["browserId"]:
            raise _Stopped("BrowserUnavailable")
        target = call(lambda remaining: connector.get_target(payload["targetId"], remaining))
        if target is None:
            raise _Stopped("BrowserUnavailable")

        def binding():
            if call(lambda _: connector.browser_id) != payload["browserId"]:
                raise _Stopped("BrowserUnavailable")
            if call(lambda remaining: connector.get_target(payload["targetId"], remaining)) is not target:
                raise _Stopped("BrowserUnavailable")
            if call(lambda _: target.target_id) != payload["targetId"]:
                raise _Stopped("BrowserUnavailable")
            if call(lambda _: connector.browser_id) != payload["browserId"]:
                raise _Stopped("BrowserUnavailable")

        def bound_call(operation):
            binding()
            result = call(operation)
            binding()
            return result

        initial_url = bound_call(target.current_url)
        if classify_route(initial_url) == "PassportRoot":
            return {"kind": "LoginRequired"}
        if initial_url not in (payload["sourceUri"], payload["sourceUri"].split("?", 1)[0]):
            return {"kind": "PageNotValidated"}
        stage = bound_call(target.observe_source_stage)
        if bound_call(target.current_url) != initial_url:
            return {"kind": "PageNotValidated"}
        stage = _finite_result(stage, STAGE_KINDS, "PageNotValidated")
        if stage["kind"] != "SourceOpened":
            return {"kind": "PageNotValidated"} if stage["kind"] == "ExtractionRequired" else stage

        def dispatch(remaining):
            nonlocal dispatched
            dispatched = True
            return target.open_transfer_picker(payload["expectedBaiduUserId"], initial_url,
                                               payload["expectedSource"], remaining)

        value = bound_call(dispatch)
        if bound_call(target.current_url) != initial_url:
            return {"kind": "OutcomeUnknown"}
        protocol.validate_open_transfer_picker_result(value)
        budget.remaining()
        return value
    except _Stopped as stopped:
        return {"kind": "OutcomeUnknown" if dispatched else stopped.kind}
    except Exception:
        return {"kind": "OutcomeUnknown" if dispatched else "BrowserUnavailable"}


def open_with_connector(payload, connector_factory, *, classify_route,
                        monotonic=time.monotonic, is_cancelled=lambda: False):
    """Existing-only attachment shares the one deadline and happens after source validation."""
    try:
        protocol.validate_open_transfer_picker_payload(payload)
    except Exception:
        return {"kind": "PageNotValidated"}
    try:
        budget = OperationBudget(payload["timeoutMilliseconds"], monotonic, is_cancelled)
        connector = budget.call(lambda _: connector_factory(budget))
        return open_bound_picker(payload, connector, classify_route=classify_route, budget=budget)
    except _Stopped as stopped:
        return {"kind": stopped.kind}
    except Exception:
        return {"kind": "BrowserUnavailable"}
