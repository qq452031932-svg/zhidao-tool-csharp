"""Read-only destination-picker FOLDER candidates; not a saved-file baseline or write authority.

Synthetic selectors only: no live acceptance, scrolling, navigation, storage access or mutation.
"""
from __future__ import annotations
import hashlib
import math
import struct
import time
import protocol
from transfer_source import OperationBudget, _Stopped

DIRECTORY_LISTING_SCRIPT = r"""
return (async function (expectedUid, budgetMs, expectedSourceUrl) {
    'use strict';
    const unknown = () => ({kind: 'PageNotValidated'});
    if (typeof budgetMs !== 'number' || !Number.isFinite(budgetMs)) return unknown();
    if (budgetMs <= 0) return {kind: 'TimedOut'};
    if (typeof expectedSourceUrl !== 'string' ||
        !/^https:\/\/pan\.baidu\.com\/s\/[A-Za-z0-9_-]{1,128}(?:\?pwd=[A-Za-z0-9]{4})?$/.test(expectedSourceUrl) ||
        /\s/.test(expectedSourceUrl)) return unknown();
    const deadline = Date.now() + budgetMs;
    const expired = {}, mismatch = {};
    const originalDocument = document, originalHref = location.href;
    const rootSelector = '[role="dialog"]';
    function guard() {
        if (Date.now() >= deadline) throw expired;
        if (document !== originalDocument || location.href !== originalHref || location.href !== expectedSourceUrl ||
            window.top !== window || document.defaultView !== window ||
            !/^https:\/\/pan\.baidu\.com\/s\/[A-Za-z0-9_-]{1,128}(?:\?pwd=[A-Za-z0-9]{4})?$/.test(location.href) ||
            /\s/.test(location.href)) throw new Error();
    }
    function nodes(root, selector, limit = 256) {
        guard();
        const result = root.querySelectorAll(selector);
        guard();
        if (!result || result.length > limit) throw new Error();
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
    function text(value, trim = true, maximum = 256) {
        if (typeof value !== 'string' || !value || value.length > maximum ||
            (trim && value !== value.trim()) || /[\x00-\x1f\x7f-\x9f]/.test(value)) throw new Error();
        // TextEncoder otherwise silently replaces unpaired UTF-16 surrogates.
        for (let i = 0; i < value.length; i++) {
            const code = value.charCodeAt(i);
            if (code >= 0xd800 && code <= 0xdbff) {
                const next = value.charCodeAt(++i);
                if (!(next >= 0xdc00 && next <= 0xdfff)) throw new Error();
            } else if (code >= 0xdc00 && code <= 0xdfff) throw new Error();
        }
        if (new TextEncoder().encode(value).length > maximum) throw new Error();
        return value;
    }
    function attributes(node, names) {
        guard();
        const values = names.map(name => node.getAttribute(name));
        guard();
        return values;
    }
    function alias(values, trim = true, identity = false, maximum = 256) {
        const present = values.filter(value => value !== null);
        if (!present.length) throw new Error();
        present.forEach(value => text(value, trim, maximum));
        if (present.some(value => value !== present[0])) {
            if (identity) throw mismatch;
            throw new Error();
        }
        return present[0];
    }
    function label(node) {
        guard();
        const aria = node.getAttribute('aria-label'), content = node.textContent;
        if ((aria !== null && (typeof aria !== 'string' || aria.length > 16384)) ||
            typeof content !== 'string' || content.length > 16384) throw new Error();
        return [aria, content];
    }
    function snapshot() {
        guard();
        const roots = nodes(document, rootSelector);
        const labels = roots.map(label);
        const trusted = roots.filter((node, index) => /保存到|选择保存位置|新建文件夹/.test(labels[index].join(' ')));
        if (trusted.length !== 1 || !visible(trusted[0])) throw new Error();
        const root = trusted[0];
        // A sibling login/verification/unknown modal can leave the old picker visibly mounted.
        // Its labels need not match the picker. Never publish the obscured picker as ready.
        if (roots.some(node => node !== root &&
            (visible(node) || root.contains(node) || node.contains(root)))) throw new Error();
        const uidNodes = nodes(document, '[data-uid],[data-uk]', 32);
        if (!uidNodes.length || uidNodes.some(node => !visible(node))) throw new Error();
        const uidAttrs = uidNodes.map(node => attributes(node, ['data-uid', 'data-uk']));
        const uids = uidAttrs.map(values => alias(values, true, true));
        if (uids.some(uid => uid !== expectedUid)) throw mismatch;
        const generationAttrs = attributes(root, ['data-generation', 'data-list-version']);
        const generationToken = alias(generationAttrs);
        const parentAttrs = attributes(root, ['data-parent-id', 'data-parent-fsid']);
        const parentExactId = alias(parentAttrs);
        if (!/^[A-Za-z0-9_-]{1,128}$/.test(parentExactId)) throw new Error();
        const pathAttrs = attributes(root, ['data-path', 'data-canonical-path']);
        const canonicalPath = alias(pathAttrs, true, false, 4096);
        if (!canonicalPath.startsWith('/') || canonicalPath.includes(String.fromCharCode(92))) throw new Error();
        const segments = canonicalPath === '/' ? [] : canonicalPath.slice(1).split('/');
        if (segments.length > 32 || segments.some(segment => !segment || segment === '.' || segment === '..')) throw new Error();
        segments.forEach(segment => text(segment, false));
        const ancestorText = attributes(root, ['data-ancestor-ids'])[0];
        text(ancestorText, true, 4608);
        const ancestorExactIds = JSON.parse(ancestorText);
        if (!Array.isArray(ancestorExactIds) || ancestorExactIds.length < 1 || ancestorExactIds.length > 33 ||
            ancestorExactIds.length !== segments.length + 1 ||
            ancestorExactIds.some(id => typeof id !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(id) || /\s/.test(id)) ||
            new Set(ancestorExactIds).size !== ancestorExactIds.length || ancestorExactIds[ancestorExactIds.length - 1] !== parentExactId) throw new Error();
        const totalNodes = nodes(root, '[data-total-count]', 2);
        const endNodes = nodes(root, '[data-list-end]', 2);
        if (totalNodes.length !== 1 || endNodes.length !== 1 ||
            !visible(totalNodes[0]) || !visible(endNodes[0])) throw new Error();
        const totalText = attributes(totalNodes[0], ['data-total-count'])[0];
        const endText = attributes(endNodes[0], ['data-list-end'])[0];
        if (typeof totalText !== 'string' || !/^(?:[0-9]|[12][0-9]|3[0-2])$/.test(totalText) || endText !== 'true') throw new Error();
        const rows = nodes(root, '[data-fsid],[data-file-id]', 32);
        if (rows.length !== Number(totalText) || rows.some(row => !visible(row))) throw new Error();
        if (rows.some(row => rows.some(other => other !== row && row.contains(other)))) throw new Error();
        const rowAttrs = rows.map(row => attributes(row, ['data-fsid', 'data-file-id', 'data-name', 'title', 'aria-label', 'data-kind']));
        const seen = new Set(ancestorExactIds);
        const items = rowAttrs.map(values => {
            const exactId = alias(values.slice(0, 2));
            if (!/^[A-Za-z0-9_-]{1,128}$/.test(exactId) || seen.has(exactId)) throw new Error();
            seen.add(exactId);
            const serverName = alias(values.slice(2, 5), false);
            if (values[5] !== 'folder') throw new Error();
            return {exactId, kind: 'Folder', serverName};
        });
        items.sort((a, b) => a.exactId < b.exactId ? -1 : a.exactId > b.exactId ? 1 : 0);
        const result = {kind: 'Observed', baiduUserId: expectedUid, generationToken, parentExactId, canonicalPath, ancestorExactIds,
                        totalCount: items.length, reachedEnd: true, isStable: true, items};
        guard();
        return {root, refs: [...roots, ...uidNodes, ...totalNodes, ...endNodes, ...rows],
                evidence: JSON.stringify({labels, uidAttrs, generationAttrs, parentAttrs, pathAttrs, ancestorText, totalText, endText, rowAttrs, result}), result};
    }
    try {
        guard();
        text(expectedUid);
        const first = snapshot();
        await Promise.resolve();
        const second = snapshot();
        guard();
        if (first.root !== second.root || first.evidence !== second.evidence ||
            first.refs.length !== second.refs.length || first.refs.some((node, index) => node !== second.refs[index])) return unknown();
        return second.result;
    } catch (error) {
        return error === expired ? {kind: 'TimedOut'} :
               error === mismatch ? {kind: 'AccountIdentityMismatch'} : unknown();
    }
})(arguments[0], arguments[1], arguments[2]);
"""


def compute_directory_fingerprint(source_uri, result):
    """Cross-language SHA256 over uint32 BE length-prefixed strict UTF-8 fields."""
    if not protocol.is_canonical_source_uri(source_uri):
        raise protocol.ProtocolError("InvalidPayload")
    protocol.validate_read_transfer_directory_result(result, require_fingerprint=False)
    if result["kind"] != "Observed":
        raise protocol.ProtocolError("InvalidPayload")
    fields = ["ManagedTransferDirectory-v1", source_uri, result["baiduUserId"], result["generationToken"],
              result["parentExactId"], result["canonicalPath"], str(len(result["ancestorExactIds"])),
              *result["ancestorExactIds"], str(len(result["items"]))]
    for item in sorted(result["items"], key=lambda item: item["exactId"]):
        fields.extend((item["exactId"], item["kind"], item["serverName"]))
    digest = hashlib.sha256()
    for field in fields:
        encoded = field.encode("utf-8", errors="strict")
        digest.update(struct.pack(">I", len(encoded))); digest.update(encoded)
    return digest.hexdigest()


def observe_directory_listing(tab, expected_uid, expected_source_url, remaining_seconds):
    if (type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or
            not protocol._bounded_text(expected_uid) or not protocol.is_canonical_source_uri(expected_source_url)):
        return {"kind": "BrowserUnavailable"}
    if remaining_seconds <= 0:
        return {"kind": "TimedOut"}
    try:
        value = tab.run_js(DIRECTORY_LISTING_SCRIPT, expected_uid, remaining_seconds * 1000.0,
                           expected_source_url, timeout=remaining_seconds)
    except Exception:
        return {"kind": "BrowserUnavailable"}
    try:
        protocol.validate_read_transfer_directory_result(value, require_fingerprint=False)
        return value
    except Exception:
        return {"kind": "PageNotValidated"}


def read_bound_directory(payload, connector, *, classify_route, monotonic=time.monotonic,
                         is_cancelled=lambda: False, budget=None):
    """One exact already-open picker target. No source-stage detector, mutation or retry."""
    try:
        protocol.validate_read_transfer_directory_payload(payload)
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
            binding(); result = call(operation); binding(); return result

        initial_url = bound_call(target.current_url)
        if classify_route(initial_url) == "PassportRoot":
            return {"kind": "LoginRequired"}
        source = payload["sourceUri"]
        if initial_url not in (source, source.split("?", 1)[0]):
            return {"kind": "PageNotValidated"}
        # Exact source URL is also an input to the fixed script, protecting against
        # another share briefly replacing the target between outer observations.
        if bound_call(target.current_url) != initial_url:
            return {"kind": "PageNotValidated"}
        value = bound_call(lambda remaining: target.observe_directory_listing(
            payload["expectedBaiduUserId"], initial_url, remaining))
        if bound_call(target.current_url) != initial_url:
            return {"kind": "PageNotValidated"}
        try:
            protocol.validate_read_transfer_directory_result(value, require_fingerprint=False)
        except Exception:
            return {"kind": "PageNotValidated"}
        if value["kind"] != "Observed":
            return {"kind": value["kind"]}
        if value["baiduUserId"] != payload["expectedBaiduUserId"]:
            return {"kind": "AccountIdentityMismatch"}
        if payload["expectedParentExactId"] is not None and (
                value["parentExactId"] != payload["expectedParentExactId"] or
                value["canonicalPath"] != payload["expectedCanonicalPath"]):
            return {"kind": "PageNotValidated"}
        result = {**value, "ancestorExactIds": list(value["ancestorExactIds"]),
                  "items": sorted((dict(item) for item in value["items"]), key=lambda item: item["exactId"]),
                  "observationFingerprint": compute_directory_fingerprint(source, value)}
        budget.remaining()
        return result
    except _Stopped as stopped:
        return {"kind": stopped.kind}
    except Exception:
        return {"kind": "BrowserUnavailable"}


def read_with_connector(payload, connector_factory, *, classify_route,
                        monotonic=time.monotonic, is_cancelled=lambda: False):
    try:
        protocol.validate_read_transfer_directory_payload(payload)
        budget = OperationBudget(payload["timeoutMilliseconds"], monotonic, is_cancelled)
        connector = budget.call(lambda _: connector_factory(budget))
        return read_bound_directory(payload, connector, classify_route=classify_route, budget=budget)
    except _Stopped as stopped:
        return {"kind": stopped.kind}
    except Exception:
        return {"kind": "BrowserUnavailable"}
