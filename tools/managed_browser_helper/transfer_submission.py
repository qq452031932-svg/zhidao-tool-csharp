"""Fixed final-transfer readiness, one-click submission, and official outcome observation.

The three DOM programs accept only immutable protocol evidence. They contain no caller-supplied
selector, script, URL, label, or retry input and are exercised only against synthetic DOM in tests.
"""

from __future__ import annotations

import math
import time

import protocol
from transfer_source import OperationBudget, _Stopped


OBSERVE_TRANSFER_SUBMISSION_READY_SCRIPT = r"""
return (async function(expected,budgetMs){
  'use strict';
  const fail=kind=>({kind}),bytes=value=>new TextEncoder().encode(value).length;
  const exact=(value,max=256)=>typeof value==='string'&&value.length>0&&bytes(value)<=max&&
    !/[\u0000-\u001f\u007f-\u009f]/.test(value);
  const id=value=>exact(value,128)&&/^[A-Za-z0-9_-]+$/.test(value);
  const nonce=value=>typeof value==='string'&&/^[0-9a-f]{64}$/.test(value);
  const fields=['browserId','targetId','devToolsEndpoint','timeoutMilliseconds','sourceUri','baiduUserId',
    'sourceGenerationToken','sourceObservationFingerprint','sourceFingerprint','sourceExactId',
    'sourceItemKind','selectedTargetExactId','selectedTargetCanonicalPath','targetBaselineFingerprint','duplicatePolicy'];
  const exactObject=value=>value!==null&&typeof value==='object'&&!Array.isArray(value)&&
    Object.keys(value).length===fields.length&&fields.every(key=>Object.hasOwn(value,key));
  const visible=node=>node&&node.isConnected===true&&getComputedStyle(node).display!=='none'&&
    getComputedStyle(node).visibility!=='hidden'&&getComputedStyle(node).pointerEvents!=='none';
  const pageState=()=>{const states=Array.from(document.querySelectorAll('[data-page-state]'));
    if(!states.length)return null;if(states.length!==1)return 'PageNotValidated';
    const state=states[0].getAttribute('data-page-state');
    return state==='LoginRequired'||state==='VerificationRequired'?state:'PageNotValidated';};
  const canonicalSource=value=>typeof value==='string'&&
    /^https:\/\/pan\.baidu\.com\/s\/[A-Za-z0-9_-]{1,128}(?:\?pwd=[A-Za-z0-9]{4})?$/.test(value);
  const allowedRoute=()=>canonicalSource(expected.sourceUri)&&location.href===expected.sourceUri;
  const proof=()=>{
    const state=pageState();if(state)return {failure:state};
    if(!allowedRoute()||window.top!==window||document.defaultView!==window)return {failure:'PageNotValidated'};
    const roots=Array.from(document.querySelectorAll('[data-managed-transfer-submission]'));
    if(roots.length!==1)return {failure:'PageNotValidated'};const root=roots[0];
    if(!visible(root)||root.getAttribute('data-route')!=='transfer-picker')return {failure:'PageNotValidated'};
    const uid=root.getAttribute('data-uid');if(!exact(uid))return {failure:'PageNotValidated'};
    if(uid!==expected.baiduUserId)return {failure:'AccountIdentityMismatch'};
    const attributes=[['data-browser-id','browserId'],['data-browser-target-id','targetId'],
      ['data-source-generation','sourceGenerationToken'],['data-source-observation-fingerprint','sourceObservationFingerprint'],
      ['data-source-fingerprint','sourceFingerprint'],['data-selected-target-id','selectedTargetExactId'],
      ['data-selected-target-path','selectedTargetCanonicalPath'],['data-target-baseline-fingerprint','targetBaselineFingerprint'],
      ['data-duplicate-policy','duplicatePolicy']];
    if(attributes.some(([attribute,key])=>root.getAttribute(attribute)!==expected[key])||
      root.getAttribute('data-source-complete')!=='true'||root.getAttribute('data-source-count')!=='1')
      return {failure:'PageNotValidated'};
    const sources=Array.from(root.querySelectorAll('[data-managed-transfer-source-item]'));
    if(sources.length!==1||!visible(sources[0])||sources[0].getAttribute('data-source-id')!==expected.sourceExactId||
      sources[0].getAttribute('data-kind')!==expected.sourceItemKind)return {failure:'PageNotValidated'};
    const controls=Array.from(root.querySelectorAll('[data-managed-transfer-submit]')).filter(visible);
    if(controls.length!==1)return {failure:'PageNotValidated'};const control=controls[0];
    if(control.getAttribute('data-eligible')!=='true'||String(control.tagName).toLowerCase()!=='button'||
      control.form||!['', 'button'].includes((control.getAttribute('type')||'').toLowerCase())||
      control.disabled||control.inert||control.getAttribute('disabled')!==null||
      control.getAttribute('aria-disabled')==='true'||typeof control.click!=='function')return {failure:'PageNotValidated'};
    const evidence=JSON.stringify([location.href,attributes.map(([attribute])=>root.getAttribute(attribute)),
      uid,root.getAttribute('data-source-complete'),root.getAttribute('data-source-count'),
      sources[0].getAttribute('data-source-id'),sources[0].getAttribute('data-kind'),
      control.getAttribute('data-eligible'),control.getAttribute('type')]);
    return {root,source:sources[0],control,evidence};
  };
  try{
    if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs)||budgetMs<=0)return fail('TimedOut');
    if(!exactObject(expected)||!exact(expected.browserId)||!exact(expected.targetId)||
      !exact(expected.devToolsEndpoint)||!Number.isInteger(expected.timeoutMilliseconds)||!canonicalSource(expected.sourceUri)||
      !exact(expected.baiduUserId)||!exact(expected.sourceGenerationToken)||
      !nonce(expected.sourceObservationFingerprint)||!nonce(expected.sourceFingerprint)||
      !id(expected.sourceExactId)||!['File','Folder'].includes(expected.sourceItemKind)||
      !id(expected.selectedTargetExactId)||!exact(expected.selectedTargetCanonicalPath,4096)||
      !nonce(expected.targetBaselineFingerprint)||!['Skip','Overwrite','Rename'].includes(expected.duplicatePolicy))
      return fail('PageNotValidated');
    const first=proof();if(first.failure)return fail(first.failure);await Promise.resolve();
    const second=proof();if(second.failure)return fail(second.failure);
    if(first.root!==second.root||first.source!==second.source||first.control!==second.control||
      first.evidence!==second.evidence)return fail('PageNotValidated');
    return {kind:'Ready',...expected,eligibleControlCount:1};
  }catch(_){return fail('PageNotValidated');}
})(arguments[0],arguments[1]);
"""


# This entire proof and native activation boundary is synchronous and runs only in a fresh CDP
# isolated world. Do not insert await/timers or read/call the page world's ``control.click``.
SUBMIT_TRANSFER_ONCE_SCRIPT = r"""
return (function(expected,readinessFingerprint,computedReadinessFingerprint,budgetMs){
  'use strict';let clickAttempted=false;
  const result=(kind,dispatchState)=>({kind,dispatchState}),bytes=value=>new TextEncoder().encode(value).length;
  const exact=(value,max=256)=>typeof value==='string'&&value.length>0&&bytes(value)<=max&&
    !/[\u0000-\u001f\u007f-\u009f]/.test(value);
  const nonce=value=>typeof value==='string'&&/^[0-9a-f]{64}$/.test(value);
  const canonicalSource=value=>typeof value==='string'&&
    /^https:\/\/pan\.baidu\.com\/s\/[A-Za-z0-9_-]{1,128}(?:\?pwd=[A-Za-z0-9]{4})?$/.test(value);
  const stateFailure=()=>{const states=Array.from(document.querySelectorAll('[data-page-state]'));
    if(!states.length)return null;if(states.length!==1)return 'PageNotValidated';
    const state=states[0].getAttribute('data-page-state');
    return state==='LoginRequired'||state==='VerificationRequired'?state:'PageNotValidated';};
  const visible=node=>node&&node.isConnected===true&&getComputedStyle(node).display!=='none'&&
    getComputedStyle(node).visibility!=='hidden'&&getComputedStyle(node).pointerEvents!=='none';
  const nativeActivation=()=>{
    if(typeof HTMLElement!=='function'||typeof HTMLButtonElement!=='function')return null;
    const descriptor=Object.getOwnPropertyDescriptor(HTMLElement.prototype,'click');
    if(!descriptor||typeof descriptor.value!=='function'||descriptor.get!==undefined||descriptor.set!==undefined)
      return null;
    return {buttonType:HTMLButtonElement,click:descriptor.value,apply:Reflect.apply};
  };
  const proof=()=>{
    const state=stateFailure();if(state)return {failure:state};
    if(window.top!==window||document.defaultView!==window||
      !canonicalSource(expected.sourceUri)||location.href!==expected.sourceUri)
      return {failure:'PageNotValidated'};
    const roots=Array.from(document.querySelectorAll('[data-managed-transfer-submission]'));
    if(roots.length!==1)return {failure:'PageNotValidated'};const root=roots[0];
    if(!visible(root)||root.getAttribute('data-route')!=='transfer-picker')return {failure:'PageNotValidated'};
    const uid=root.getAttribute('data-uid');if(uid!==expected.baiduUserId)
      return {failure:exact(uid)?'AccountIdentityMismatch':'PageNotValidated'};
    const pairs=[['data-browser-id','browserId'],['data-browser-target-id','targetId'],
      ['data-source-generation','sourceGenerationToken'],['data-source-observation-fingerprint','sourceObservationFingerprint'],
      ['data-source-fingerprint','sourceFingerprint'],['data-selected-target-id','selectedTargetExactId'],
      ['data-selected-target-path','selectedTargetCanonicalPath'],['data-target-baseline-fingerprint','targetBaselineFingerprint'],
      ['data-duplicate-policy','duplicatePolicy']];
    if(pairs.some(([attribute,key])=>root.getAttribute(attribute)!==expected[key])||
      root.getAttribute('data-source-complete')!=='true'||root.getAttribute('data-source-count')!=='1')
      return {failure:'PageNotValidated'};
    const sources=Array.from(root.querySelectorAll('[data-managed-transfer-source-item]'));
    if(sources.length!==1||!visible(sources[0])||sources[0].getAttribute('data-source-id')!==expected.sourceExactId||
      sources[0].getAttribute('data-kind')!==expected.sourceItemKind)return {failure:'PageNotValidated'};
    const controls=Array.from(root.querySelectorAll('[data-managed-transfer-submit]')).filter(visible);
    if(controls.length!==1)return {failure:'PageNotValidated'};const control=controls[0];
    if(control.getAttribute('data-eligible')!=='true'||String(control.tagName).toLowerCase()!=='button'||
      control.form||!['','button'].includes((control.getAttribute('type')||'').toLowerCase())||
      control.disabled||control.inert||control.getAttribute('disabled')!==null||
      control.getAttribute('aria-disabled')==='true')return {failure:'PageNotValidated'};
    return {root,source:sources[0],control,
      evidence:JSON.stringify([location.href,pairs.map(([attribute])=>root.getAttribute(attribute)),uid,
        root.getAttribute('data-source-complete'),root.getAttribute('data-source-count'),
        sources[0].getAttribute('data-source-id'),sources[0].getAttribute('data-kind'),
        control.getAttribute('data-eligible'),control.getAttribute('type')])};
  };
  try{
    if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs)||budgetMs<=0)
      return result('TimedOut','KnownNotDispatched');
    if(!canonicalSource(expected.sourceUri)||!nonce(readinessFingerprint)||!nonce(computedReadinessFingerprint)||
      readinessFingerprint!==computedReadinessFingerprint)
      return result('ReadinessMismatch','KnownNotDispatched');
    const activation=nativeActivation();if(!activation)
      return result('PageNotValidated','KnownNotDispatched');
    const first=proof();if(first.failure)return result(first.failure,'KnownNotDispatched');
    const final=proof();if(final.failure)return result(final.failure,'KnownNotDispatched');
    if(!(first.control instanceof activation.buttonType)||!(final.control instanceof activation.buttonType))
      return result('PageNotValidated','KnownNotDispatched');
    if(first.root!==final.root||first.source!==final.source||first.control!==final.control||
      first.evidence!==final.evidence)
      return result('ReadinessMismatch','KnownNotDispatched');
    clickAttempted=true;activation.apply(activation.click,final.control,[]);
    const after=proof();
    if(after.failure||after.root!==final.root||after.source!==final.source||after.control!==final.control||
      after.evidence!==final.evidence||!(after.control instanceof activation.buttonType))
      return result('ClickAttempted','DispatchOutcomeUnknown');
    return result('ClickAttempted','ClickDispatched');
  }catch(_){return clickAttempted?result('ClickAttempted','DispatchOutcomeUnknown'):
    result('PageNotValidated','KnownNotDispatched');}
})(arguments[0],arguments[1],arguments[2],arguments[3]);
"""
OBSERVE_TRANSFER_SUBMISSION_OUTCOME_SCRIPT = r"""
return (function(expected,readinessFingerprint,computedReadinessFingerprint,budgetMs){
  'use strict';
  const fail=kind=>({kind}),bytes=value=>new TextEncoder().encode(value).length;
  const exact=(value,max=256)=>typeof value==='string'&&value.length>0&&bytes(value)<=max&&
    !/[\u0000-\u001f\u007f-\u009f]/.test(value);
  const id=value=>exact(value,128)&&/^[A-Za-z0-9_-]+$/.test(value);
  const nonce=value=>typeof value==='string'&&/^[0-9a-f]{64}$/.test(value);
  const canonicalSource=value=>typeof value==='string'&&
    /^https:\/\/pan\.baidu\.com\/s\/[A-Za-z0-9_-]{1,128}(?:\?pwd=[A-Za-z0-9]{4})?$/.test(value);
  const visible=node=>node&&node.isConnected===true&&getComputedStyle(node).display!=='none'&&
    getComputedStyle(node).visibility!=='hidden'&&getComputedStyle(node).pointerEvents!=='none';
  try{
    if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs)||budgetMs<=0)return fail('TimedOut');
    if(!canonicalSource(expected.sourceUri)||!nonce(readinessFingerprint)||!nonce(computedReadinessFingerprint)||
      readinessFingerprint!==computedReadinessFingerprint)return fail('PageNotValidated');
    const states=Array.from(document.querySelectorAll('[data-page-state]'));
    if(states.length){if(states.length!==1)return fail('PageNotValidated');
      const state=states[0].getAttribute('data-page-state');
      return fail(state==='LoginRequired'||state==='VerificationRequired'?state:'PageNotValidated');}
    if(window.top!==window||document.defaultView!==window||
      location.href!==expected.sourceUri)
      return fail('PageNotValidated');
    const roots=Array.from(document.querySelectorAll('[data-managed-transfer-submission]'));
    if(roots.length!==1||!visible(roots[0]))return fail('PageNotValidated');const root=roots[0];
    if(root.getAttribute('data-route')!=='transfer-picker')return fail('PageNotValidated');
    const uid=root.getAttribute('data-uid');if(uid!==expected.baiduUserId)
      return fail(exact(uid)?'AccountIdentityMismatch':'PageNotValidated');
    const pairs=[['data-browser-id','browserId'],['data-browser-target-id','targetId'],
      ['data-source-generation','sourceGenerationToken'],['data-source-observation-fingerprint','sourceObservationFingerprint'],
      ['data-source-fingerprint','sourceFingerprint'],['data-selected-target-id','selectedTargetExactId'],
      ['data-selected-target-path','selectedTargetCanonicalPath'],['data-target-baseline-fingerprint','targetBaselineFingerprint'],
      ['data-duplicate-policy','duplicatePolicy']];
    if(pairs.some(([attribute,key])=>root.getAttribute(attribute)!==expected[key]))return fail('PageNotValidated');
    const official=Array.from(root.querySelectorAll('[data-managed-transfer-official-result]')).filter(visible);
    if(official.length>1)return fail('PageNotValidated');
    if(official.length===1){const observed=official[0],status=observed.getAttribute('data-status');
      if(status==='conflict')return fail('Conflict');if(status==='rejected')return fail('ExplicitlyRejected');
      if(status!=='accepted')return fail('PageNotValidated');
      const officialRequestId=observed.getAttribute('data-request-id'),exactTargetId=observed.getAttribute('data-target-id'),
        sourceExactId=observed.getAttribute('data-source-id'),destinationExactId=observed.getAttribute('data-destination-id');
      if(!exact(officialRequestId)||!id(exactTargetId)||!id(sourceExactId)||!id(destinationExactId)||
        exactTargetId!==expected.selectedTargetExactId||sourceExactId!==expected.sourceExactId)
        return fail('PageNotValidated');
      return {kind:'AcceptedBound',officialRequestId,exactTargetId,sourceExactId,destinationExactId};}
    const acknowledgements=Array.from(root.querySelectorAll('[data-managed-transfer-acknowledgement]')).filter(visible);
    return fail(acknowledgements.length===1?'UnboundAcknowledgement':'PageNotValidated');
  }catch(_){return fail('PageNotValidated');}
})(arguments[0],arguments[1],arguments[2],arguments[3]);
"""


def _validate_raw_ready(value):
    if type(value) is not dict or value.get("kind") != "Ready":
        protocol.validate_transfer_readiness_result(value)
        return value
    expected = ("kind",) + protocol.TRANSFER_SUBMISSION_CONTEXT_FIELDS + ("eligibleControlCount",)
    protocol._exact_object(value, expected, body=True)
    context = {field: value[field] for field in protocol.TRANSFER_SUBMISSION_CONTEXT_FIELDS}
    protocol._validate_transfer_submission_context(context)
    if value["eligibleControlCount"] != 1 or type(value["eligibleControlCount"]) is not int:
        raise protocol.ProtocolError("InvalidPayload")
    result = {**value, "readinessFingerprint": protocol.compute_transfer_readiness_fingerprint(context)}
    protocol.validate_transfer_readiness_result(result)
    return result


def observe_readiness(tab, payload, remaining_seconds):
    try:
        protocol.validate_transfer_submission_request("ObserveTransferSubmissionReady", payload)
        if (type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or
                remaining_seconds <= 0):
            return {"kind": "TimedOut"}
        value = tab.run_js(OBSERVE_TRANSFER_SUBMISSION_READY_SCRIPT, payload,
                           remaining_seconds * 1000.0, timeout=remaining_seconds)
        return _validate_raw_ready(value)
    except protocol.ProtocolError:
        return {"kind": "PageNotValidated"}
    except Exception:
        return {"kind": "BrowserUnavailable"}


def submit_once(tab, payload, remaining_seconds):
    """Invoke one fixed synchronous proof/click program; any thrown call is post-boundary unknown."""
    try:
        protocol.validate_transfer_submission_request("SubmitTransferOnce", payload)
    except Exception:
        return {"kind": "ReadinessMismatch", "dispatchState": "KnownNotDispatched"}
    if (type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or
            remaining_seconds <= 0):
        return {"kind": "TimedOut", "dispatchState": "KnownNotDispatched"}
    context = {field: payload[field] for field in protocol.TRANSFER_SUBMISSION_CONTEXT_FIELDS}
    computed = protocol.compute_transfer_readiness_fingerprint(context)
    isolated_runner = getattr(tab, "run_transfer_submit_isolated", None)
    if not callable(isolated_runner):
        return {"kind": "PageNotValidated", "dispatchState": "KnownNotDispatched"}
    try:
        value = isolated_runner(
            context, payload["readinessFingerprint"], computed,
            remaining_seconds * 1000.0, timeout=remaining_seconds)
        protocol.validate_transfer_submit_result(value)
        return value
    except Exception:
        return {"kind": "BrowserUnavailable", "dispatchState": "DispatchOutcomeUnknown"}


def observe_outcome(tab, payload, remaining_seconds):
    """Read the fixed official result contract without navigation, click, dismissal, or inference."""
    try:
        protocol.validate_transfer_submission_request("ObserveTransferSubmissionOutcome", payload)
        if (type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or
                remaining_seconds <= 0):
            return {"kind": "TimedOut"}
        context = {field: payload[field] for field in protocol.TRANSFER_SUBMISSION_CONTEXT_FIELDS}
        computed = protocol.compute_transfer_readiness_fingerprint(context)
        value = tab.run_js(OBSERVE_TRANSFER_SUBMISSION_OUTCOME_SCRIPT, context,
                           payload["readinessFingerprint"], computed, remaining_seconds * 1000.0,
                           timeout=remaining_seconds)
        if type(value) is dict and value.get("kind") == "AcceptedBound":
            expected = ("kind", "officialRequestId", "exactTargetId", "sourceExactId", "destinationExactId")
            protocol._exact_object(value, expected, body=True)
            value = {**value, "officialObservationFingerprint": protocol.compute_transfer_official_fingerprint(
                value["officialRequestId"], value["exactTargetId"], value["sourceExactId"],
                value["destinationExactId"])}
        protocol.validate_transfer_outcome_result(value)
        return value
    except protocol.ProtocolError:
        return {"kind": "PageNotValidated"}
    except Exception:
        return {"kind": "BrowserUnavailable"}


def dispatch_bound(action, payload, connector, *, budget):
    """Execute exactly one bound action with conservative post-click uncertainty."""
    dispatched = False
    try:
        protocol.validate_transfer_submission_request(action, payload)
    except Exception:
        if action == "SubmitTransferOnce":
            return {"kind": "ReadinessMismatch", "dispatchState": "KnownNotDispatched"}
        return {"kind": "PageNotValidated"}
    try:
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

        binding()
        if action == "ObserveTransferSubmissionReady":
            value = call(lambda remaining: target.observe_transfer_submission_ready(payload, remaining))
            binding(); protocol.validate_transfer_readiness_result(value)
        elif action == "SubmitTransferOnce":
            dispatched = True
            value = call(lambda remaining: target.submit_transfer_once(payload, remaining))
            protocol.validate_transfer_submit_result(value)
            if value["dispatchState"] == "KnownNotDispatched":
                dispatched = False
            binding()
        else:
            value = call(lambda remaining: target.observe_transfer_submission_outcome(payload, remaining))
            binding(); protocol.validate_transfer_outcome_result(value)
        budget.remaining()
        return value
    except _Stopped as stopped:
        if action == "SubmitTransferOnce":
            return {"kind": stopped.kind, "dispatchState":
                    "DispatchOutcomeUnknown" if dispatched else "KnownNotDispatched"}
        return {"kind": stopped.kind}
    except Exception:
        if action == "SubmitTransferOnce":
            return {"kind": "BrowserUnavailable", "dispatchState":
                    "DispatchOutcomeUnknown" if dispatched else "KnownNotDispatched"}
        return {"kind": "BrowserUnavailable"}
