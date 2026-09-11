"""One fixed, read-only same-origin identity query; no generic browser capability.

The private script envelope never crosses the helper IPC boundary. JSON numbers
are deliberately parsed in Python, where Int64 identities retain exact digits.
The endpoint/response contract comes from this repository's legacy HTTP client
and fixtures, not from live verification of the current service.
"""

from __future__ import annotations

import json
import math
import re

MAX_RESPONSE_BYTES = 64 * 1024
MAX_INT64 = 9223372036854775807

# DrissionPage wraps these statements as a function, passes the sole numeric
# budget argument, and awaits the returned promise. Never interpolate request,
# URL, header, expected identity, or caller-supplied script values into this.
IDENTITY_QUERY_SCRIPT = r"""
return (async function (budgetMs) {
    'use strict';
    const endpoint = 'https://pan.baidu.com/api/gettemplatevariable?fields=%5B%22uk%22%2C%22username%22%5D';
    const maximumBytes = 65536;
    const unavailable = () => ({kind: 'BrowserUnavailable'});
    if (typeof budgetMs !== 'number' || !Number.isFinite(budgetMs)) return unavailable();
    if (budgetMs <= 0) return {kind: 'TimedOut'};
    const deadline = Date.now() + budgetMs;
    const timedOut = {};
    let originalDocument, originalHref, controller, reader, response, timer;
    let stopped = false;
    let succeeded = false;
    let cancelledReader = false;
    function guard() {
        if (stopped || Date.now() >= deadline) throw timedOut;
        if (document !== originalDocument || location.href !== originalHref ||
            window.top !== window || document.defaultView !== window ||
            !/^https:\/\/pan\.baidu\.com(?::443)?\/(?:disk\/main(?:\?from=homeFlow)?#\/index\?category=all)?$/.test(location.href)) {
            throw new Error();
        }
    }
    function cancelBody() {
        if (cancelledReader) return;
        const source = reader || (response && response.body);
        if (!source) return;
        cancelledReader = true;
        try {
            // Cancellation itself is not trusted to settle or to avoid throwing.
            Promise.resolve(source.cancel()).catch(() => {});
        } catch (_) {}
    }
    function abort() {
        try { if (controller) controller.abort(); } catch (_) {}
        cancelBody();
    }
    try {
        originalDocument = document;
        originalHref = location.href;
        guard(); // Reject passport, foreign origins, or a changed document before fetch.
        controller = new AbortController();
        const timeout = new Promise((_, reject) => {
            timer = setTimeout(() => { stopped = true; abort(); reject(timedOut); },
                               Math.max(0, deadline - Date.now()));
        });
        const query = (async () => {
            try {
                guard();
                response = await fetch(endpoint, {
                    method: 'GET', credentials: 'same-origin', mode: 'same-origin',
                    redirect: 'error', cache: 'no-store', signal: controller.signal
                });
                guard();
                if (!response || response.redirected !== false || response.url !== endpoint ||
                    !Number.isInteger(response.status) || response.status < 100 || response.status > 599) {
                    throw new Error();
                }
                // Error response content is neither needed nor returned.
                if (response.status < 200 || response.status >= 300) {
                    cancelBody();
                    guard();
                    return {kind: 'QueryResponse', status: response.status, body: ''};
                }
                const declared = response.headers.get('content-length');
                if (declared !== null && (!/^[0-9]+$/.test(declared) ||
                    !Number.isFinite(Number(declared)) || Number(declared) > maximumBytes)) {
                    throw new Error();
                }
                if (!response.body || typeof response.body.getReader !== 'function') throw new Error();
                reader = response.body.getReader();
                const decoder = new TextDecoder('utf-8', {fatal: true});
                let size = 0;
                let body = '';
                while (true) {
                    guard();
                    const part = await reader.read();
                    guard();
                    if (!part || typeof part.done !== 'boolean') throw new Error();
                    if (part.done) break;
                    if (!(part.value instanceof Uint8Array)) throw new Error();
                    size += part.value.byteLength;
                    if (size > maximumBytes) throw new Error();
                    body += decoder.decode(part.value, {stream: true});
                }
                body += decoder.decode();
                guard();
                return {kind: 'QueryResponse', status: response.status, body};
            } catch (error) {
                cancelBody(); // Also covers a fetch that resolves after the outer timeout.
                throw error;
            }
        })();
        const result = await Promise.race([query, timeout]);
        guard();
        succeeded = true;
        return result;
    } catch (error) {
        return error === timedOut ? {kind: 'TimedOut'} : unavailable();
    } finally {
        stopped = true;
        if (timer !== undefined) clearTimeout(timer);
        if (!succeeded) abort();
        if (succeeded && reader) {
            try { reader.releaseLock(); } catch (_) {}
        }
    }
})(arguments[0]);
"""


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("InvalidIdentityPayload")
        result[key] = value
    return result


def _reject_constant(_value):
    raise ValueError("InvalidIdentityPayload")


def _finite_float(value):
    number = float(value)
    if not math.isfinite(number):
        raise ValueError("InvalidIdentityPayload")
    return number


def _canonical_uid(value):
    if type(value) is int:
        number = value
    elif (type(value) is str and len(value) <= 256 and
          re.fullmatch(r" *[0-9]+ *", value) is not None):
        number = int(value.strip(" "))
    else:
        return None
    return str(number) if 0 < number <= MAX_INT64 else None


def _check_depth(payload):
    pending = [(payload, 0)]
    while pending:
        value, depth = pending.pop()
        if depth > 32:
            raise ValueError("InvalidIdentityPayload")
        if type(value) is dict:
            pending.extend((child, depth + 1) for child in value.values())
        elif type(value) is list:
            pending.extend((child, depth + 1) for child in value)


def parse_query_envelope(value):
    """Reduce a bounded private result to the existing, data-minimal IPC types."""
    unavailable = {"kind": "BrowserUnavailable"}
    if type(value) is not dict or type(value.get("kind")) is not str:
        return unavailable
    if value["kind"] in ("TimedOut", "BrowserUnavailable"):
        return {"kind": value["kind"]} if set(value) == {"kind"} else unavailable
    if value["kind"] != "QueryResponse" or set(value) != {"kind", "status", "body"}:
        return unavailable
    status, body = value["status"], value["body"]
    if type(status) is not int or not 100 <= status <= 599 or type(body) is not str:
        return unavailable
    try:
        if len(body) > MAX_RESPONSE_BYTES or len(body.encode("utf-8")) > MAX_RESPONSE_BYTES:
            return unavailable
        if status == 401:
            return {"kind": "LoginRequired"}
        if not 200 <= status <= 299:
            return unavailable
        payload = json.loads(body, object_pairs_hook=_unique_object,
                             parse_constant=_reject_constant, parse_float=_finite_float)
        _check_depth(payload)
        if type(payload) is not dict:
            return unavailable
        errno = payload.get("errno")
        if (type(errno) is int and errno == -6) or (type(errno) is str and errno == "-6"):
            return {"kind": "LoginRequired"}
        if not ((type(errno) is int and errno == 0) or (type(errno) is str and errno == "0")):
            return unavailable
        result = payload.get("result")
        uid = _canonical_uid(result.get("uk")) if type(result) is dict else None
        if uid is not None:
            return {"kind": "Observed", "stableBaiduUserId": uid}
    except (ValueError, TypeError, RecursionError, OverflowError):
        # Do not log exceptions: decoder/parser/driver messages may contain data.
        pass
    return unavailable


def observe_identity(tab, remaining_seconds):
    if (type(remaining_seconds) not in (int, float) or
            not math.isfinite(remaining_seconds)):
        return {"kind": "BrowserUnavailable"}
    if remaining_seconds <= 0:
        return {"kind": "TimedOut"}
    try:
        # Leave a small proportional margin for Drission result marshaling; the
        # owning host also enforces a hard process deadline/cancellation boundary.
        result = tab.run_js(IDENTITY_QUERY_SCRIPT, remaining_seconds * 900.0,
                            timeout=remaining_seconds)
        return parse_query_envelope(result)
    except TimeoutError:
        return {"kind": "TimedOut"}
    except Exception:
        return {"kind": "BrowserUnavailable"}
