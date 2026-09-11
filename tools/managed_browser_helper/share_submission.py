"""Fixed exact-item-set permanent-share listing, one-click submission, and official outcome reads.

No function accepts a selector, script, URL, label, retry count, or item collection from the host.
The only route is the already-open Baidu disk-main target bound by the immutable request context.
"""

from __future__ import annotations

import math

import protocol
from transfer_source import _Stopped


READ_SHARE_OFFICIAL_LISTING_SCRIPT = r"""
return (async function(expected,budgetMs){
  'use strict';
  const fail=kind=>({kind}),bytes=value=>new TextEncoder().encode(value).length;
  const exact=(value,max=256)=>typeof value==='string'&&value.length>0&&bytes(value)<=max&&
    !/[\u0000-\u001f\u007f-\u009f]/.test(value);
  const id=value=>exact(value,128)&&/^[A-Za-z0-9_-]+$/.test(value);
  const nonce=value=>typeof value==='string'&&/^[0-9a-f]{64}$/.test(value);
  const code=value=>typeof value==='string'&&/^[A-Za-z0-9]{4}$/.test(value);
  const fields=['browserId','targetId','devToolsEndpoint','timeoutMilliseconds','baiduUserId',
    'targetDirectoryExactId','targetCanonicalPath','exactItemIds','exactItemSetFingerprint','codeMode','fixedCode'];
  const exactObject=value=>value!==null&&typeof value==='object'&&!Array.isArray(value)&&
    Object.keys(value).length===fields.length&&fields.every(key=>Object.hasOwn(value,key));
  const visible=node=>node&&node.isConnected===true&&getComputedStyle(node).display!=='none'&&
    getComputedStyle(node).visibility!=='hidden'&&getComputedStyle(node).pointerEvents!=='none';
  const ids=value=>Array.isArray(value)&&value.length>0&&value.length<=1000&&value.every(id)&&
    new Set(value).size===value.length&&value.every((item,index)=>index===0||value[index-1]<item);
  const attributeIds=value=>{try{const parsed=JSON.parse(value);return ids(parsed)?parsed:null;}catch(_){return null;}};
  const route=()=>typeof location.href==='string'&&location.href.startsWith('https://pan.baidu.com/disk/main');
  const stateFailure=()=>{const states=Array.from(document.querySelectorAll('[data-page-state]'));
    if(!states.length)return null;if(states.length!==1)return 'PageNotValidated';
    const value=states[0].getAttribute('data-page-state');
    return value==='LoginRequired'||value==='VerificationRequired'?value:'PageNotValidated';};
  const read=()=>{
    const state=stateFailure();if(state)return {failure:state};
    if(!route()||window.top!==window||document.defaultView!==window)return {failure:'PageNotValidated'};
    const roots=Array.from(document.querySelectorAll('[data-managed-share-page]'));
    if(roots.length!==1)return {failure:'PageNotValidated'};const root=roots[0];
    if(!visible(root)||root.getAttribute('data-route')!=='disk-main')return {failure:'PageNotValidated'};
    const uid=root.getAttribute('data-uid');if(uid!==expected.baiduUserId)
      return {failure:exact(uid)?'AccountIdentityMismatch':'PageNotValidated'};
    const pairs=[['data-browser-id','browserId'],['data-browser-target-id','targetId'],
      ['data-directory-id','targetDirectoryExactId'],['data-directory-path','targetCanonicalPath']];
    if(pairs.some(([attribute,key])=>root.getAttribute(attribute)!==expected[key])||
      root.getAttribute('data-list-complete')!=='true'||root.getAttribute('data-list-stable')!=='true')
      return {failure:'PageNotValidated'};
    const items=Array.from(root.querySelectorAll('[data-managed-share-item]'));
    const itemIds=items.map(item=>item.getAttribute('data-exact-id'));
    if(items.length!==expected.exactItemIds.length||items.some(item=>!visible(item))||
      JSON.stringify(itemIds)!==JSON.stringify(expected.exactItemIds)||
      root.getAttribute('data-item-set-fingerprint')!==expected.exactItemSetFingerprint)
      return {failure:'PageNotValidated'};
    const entries=[];
    for(const node of Array.from(root.querySelectorAll('[data-managed-share-official-entry]'))){
      if(!visible(node))return {failure:'PageNotValidated'};
      const entry={shareId:node.getAttribute('data-share-id'),shareUrl:node.getAttribute('data-share-url'),
        exactItemIds:attributeIds(node.getAttribute('data-exact-item-ids')),isPermanent:node.getAttribute('data-permanent')==='true',
        isAccessUnlimited:node.getAttribute('data-unlimited')==='true',codeMode:node.getAttribute('data-code-mode'),
        effectiveCode:node.getAttribute('data-effective-code')};
      if(!exact(entry.shareId)||typeof entry.shareUrl!=='string'||!ids(entry.exactItemIds)||
        !entry.isPermanent||!entry.isAccessUnlimited||!['Random','Fixed'].includes(entry.codeMode)||
        !code(entry.effectiveCode))return {failure:'PageNotValidated'};
      entries.push(entry);
    }
    entries.sort((left,right)=>left.shareId<right.shareId?-1:left.shareId>right.shareId?1:0);
    if(new Set(entries.map(value=>value.shareId)).size!==entries.length||entries.length>1000)
      return {failure:'PageNotValidated'};
    return {root,items,entries,evidence:JSON.stringify([location.href,pairs.map(([attribute])=>root.getAttribute(attribute)),
      uid,root.getAttribute('data-list-complete'),root.getAttribute('data-list-stable'),itemIds,entries])};
  };
  try{
    if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs)||budgetMs<=0||!exactObject(expected)||
      !exact(expected.browserId)||!exact(expected.targetId)||!exact(expected.devToolsEndpoint)||
      !Number.isInteger(expected.timeoutMilliseconds)||!exact(expected.baiduUserId)||
      !id(expected.targetDirectoryExactId)||!exact(expected.targetCanonicalPath,4096)||
      !ids(expected.exactItemIds)||!nonce(expected.exactItemSetFingerprint)||
      !['Random','Fixed'].includes(expected.codeMode)||
      (expected.codeMode==='Random'?expected.fixedCode!==null:!code(expected.fixedCode)))return fail('PageNotValidated');
    const first=read();if(first.failure)return fail(first.failure);await Promise.resolve();
    const second=read();if(second.failure)return fail(second.failure);
    if(first.root!==second.root||first.items.length!==second.items.length||
      first.items.some((item,index)=>item!==second.items[index])||first.evidence!==second.evidence)
      return fail('PageNotValidated');
    return {kind:'Observed',...expected,totalCount:second.entries.length,reachedEnd:true,isStable:true,
      entries:second.entries};
  }catch(_){return fail('PageNotValidated');}
})(arguments[0],arguments[1]);
"""


# Synchronous by design. This is the sole create-share click and runs only in a fresh isolated world.
SUBMIT_SHARE_ONCE_SCRIPT = r"""
return (function(expected,baselineIds,baselineObservation,currentIds,currentObservation,budgetMs){
  'use strict';let clickAttempted=false;
  const result=(kind,dispatchState)=>({kind,dispatchState}),bytes=value=>new TextEncoder().encode(value).length;
  const exact=(value,max=256)=>typeof value==='string'&&value.length>0&&bytes(value)<=max&&
    !/[\u0000-\u001f\u007f-\u009f]/.test(value);
  const id=value=>exact(value,128)&&/^[A-Za-z0-9_-]+$/.test(value);
  const nonce=value=>typeof value==='string'&&/^[0-9a-f]{64}$/.test(value);
  const code=value=>typeof value==='string'&&/^[A-Za-z0-9]{4}$/.test(value);
  const visible=node=>node&&node.isConnected===true&&getComputedStyle(node).display!=='none'&&
    getComputedStyle(node).visibility!=='hidden'&&getComputedStyle(node).pointerEvents!=='none';
  const ids=value=>Array.isArray(value)&&value.length>0&&value.length<=1000&&value.every(id)&&
    new Set(value).size===value.length&&value.every((item,index)=>index===0||value[index-1]<item);
  const nativeActivation=()=>{if(typeof HTMLElement!=='function'||typeof HTMLButtonElement!=='function')return null;
    const descriptor=Object.getOwnPropertyDescriptor(HTMLElement.prototype,'click');
    if(!descriptor||typeof descriptor.value!=='function'||descriptor.get!==undefined||descriptor.set!==undefined)return null;
    return {buttonType:HTMLButtonElement,click:descriptor.value,apply:Reflect.apply};};
  const nativeCheckbox=()=>{if(typeof HTMLInputElement!=='function')return null;
    const descriptor=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'checked');
    if(!descriptor||typeof descriptor.get!=='function'||typeof descriptor.set!=='function'||
      descriptor.value!==undefined)return null;
    return {inputType:HTMLInputElement,get:descriptor.get,set:descriptor.set,apply:Reflect.apply};};
  const proof=()=>{
    if(typeof location.href!=='string'||!location.href.startsWith('https://pan.baidu.com/disk/main')||
      window.top!==window||document.defaultView!==window)return {failure:'PageNotValidated'};
    const roots=Array.from(document.querySelectorAll('[data-managed-share-page]'));
    if(roots.length!==1)return {failure:'PageNotValidated'};const root=roots[0];
    const uid=root.getAttribute('data-uid');if(uid!==expected.baiduUserId)
      return {failure:exact(uid)?'AccountIdentityMismatch':'PageNotValidated'};
    if(!visible(root)||root.getAttribute('data-route')!=='disk-main'||
      root.getAttribute('data-browser-id')!==expected.browserId||
      root.getAttribute('data-browser-target-id')!==expected.targetId||
      root.getAttribute('data-directory-id')!==expected.targetDirectoryExactId||
      root.getAttribute('data-directory-path')!==expected.targetCanonicalPath||
      root.getAttribute('data-list-complete')!=='true'||root.getAttribute('data-list-stable')!=='true')
      return {failure:'PageNotValidated'};
    const items=Array.from(root.querySelectorAll('[data-managed-share-item]'));
    const controls=Array.from(root.querySelectorAll('[data-managed-share-submit]')).filter(visible);
    if(items.length!==expected.exactItemIds.length||items.some(item=>!visible(item))||
      JSON.stringify(items.map(item=>item.getAttribute('data-exact-id')))!==JSON.stringify(expected.exactItemIds)||
      root.getAttribute('data-item-set-fingerprint')!==expected.exactItemSetFingerprint||controls.length!==1)
      return {failure:'PageNotValidated'};
    const control=controls[0];
    if(control.getAttribute('data-eligible')!=='true'||String(control.tagName).toLowerCase()!=='button'||
      control.disabled||control.inert||control.getAttribute('disabled')!==null||
      control.getAttribute('aria-disabled')==='true')return {failure:'PageNotValidated'};
    const labels=Array.from(root.querySelectorAll('label')).filter(visible).filter(label=>
      typeof label.textContent==='string'&&label.textContent.trim()==='分享链接自动填充提取码');
    if(labels.length!==1)return {failure:'PageNotValidated'};
    const checkboxes=Array.from(labels[0].querySelectorAll('input[type="checkbox"]')).filter(visible);
    if(checkboxes.length!==1)return {failure:'PageNotValidated'};const checkbox=checkboxes[0];
    if(String(checkbox.tagName).toLowerCase()!=='input'||checkbox.getAttribute('type')!=='checkbox'||
      checkbox.disabled||checkbox.inert||checkbox.getAttribute('disabled')!==null||
      checkbox.getAttribute('aria-disabled')==='true'||typeof checkbox.dispatchEvent!=='function')
      return {failure:'PageNotValidated'};
    return {root,items,control,checkbox};
  };
  try{
    if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs)||budgetMs<=0||!nonce(baselineIds)||
      !nonce(baselineObservation)||!nonce(currentIds)||!nonce(currentObservation)||
      baselineIds!==currentIds||baselineObservation!==currentObservation)return result('BaselineMismatch','KnownNotDispatched');
    if(!ids(expected.exactItemIds)||!id(expected.targetDirectoryExactId)||
      !['Random','Fixed'].includes(expected.codeMode)||
      (expected.codeMode==='Random'?expected.fixedCode!==null:!code(expected.fixedCode)))
      return result('PageNotValidated','KnownNotDispatched');
    const first=proof();if(first.failure)return result(first.failure,'KnownNotDispatched');
    const native=nativeActivation(),checkboxNative=nativeCheckbox();
    if(!native||!checkboxNative||!(first.control instanceof native.buttonType)||
      !(first.checkbox instanceof checkboxNative.inputType)||typeof Event!=='function')
      return result('PageNotValidated','KnownNotDispatched');
    first.items.forEach(item=>item.setAttribute('data-selected','true'));
    first.root.setAttribute('data-share-permanent','true');first.root.setAttribute('data-share-unlimited','true');
    first.root.setAttribute('data-share-code-mode',expected.codeMode);
    first.root.setAttribute('data-share-fixed-code',expected.fixedCode===null?'':expected.fixedCode);
    const initiallyChecked=checkboxNative.apply(checkboxNative.get,first.checkbox,[]);
    if(typeof initiallyChecked!=='boolean')return result('PageNotValidated','KnownNotDispatched');
    if(!initiallyChecked){
      checkboxNative.apply(checkboxNative.set,first.checkbox,[true]);
      if(checkboxNative.apply(checkboxNative.get,first.checkbox,[])!==true)
        return result('PageNotValidated','KnownNotDispatched');
      first.checkbox.dispatchEvent(new Event('input',{bubbles:true}));
      first.checkbox.dispatchEvent(new Event('change',{bubbles:true}));
    }
    const second=proof();if(second.failure||second.root!==first.root||second.items.length!==first.items.length||
      second.items.some((item,index)=>item!==first.items[index])||second.control!==first.control||
      second.checkbox!==first.checkbox||checkboxNative.apply(checkboxNative.get,second.checkbox,[])!==true)
      return result('PageNotValidated','KnownNotDispatched');
    clickAttempted=true;native.apply(native.click,first.control,[]);
    if(!first.root.isConnected||first.items.some(item=>!item.isConnected||item.getAttribute('data-selected')!=='true')||
      !first.control.isConnected||first.root.getAttribute('data-share-permanent')!=='true'||
      first.root.getAttribute('data-share-unlimited')!=='true'||
      first.root.getAttribute('data-share-code-mode')!==expected.codeMode||
      first.root.getAttribute('data-share-fixed-code')!==(expected.fixedCode===null?'':expected.fixedCode))
      return result('ClickAttempted','DispatchOutcomeUnknown');
    return result('ClickAttempted','ClickDispatched');
  }catch(_){return result(clickAttempted?'ClickAttempted':'PageNotValidated',
    clickAttempted?'DispatchOutcomeUnknown':'KnownNotDispatched');}
})(arguments[0],arguments[1],arguments[2],arguments[3],arguments[4],arguments[5]);
"""


OBSERVE_SHARE_SUBMISSION_OUTCOME_SCRIPT = r"""
return (function(expected,baselineIds,baselineObservation,budgetMs){
  'use strict';
  const fail=kind=>({kind}),bytes=value=>new TextEncoder().encode(value).length;
  const exact=(value,max=256)=>typeof value==='string'&&value.length>0&&bytes(value)<=max&&
    !/[\u0000-\u001f\u007f-\u009f]/.test(value);
  const id=value=>exact(value,128)&&/^[A-Za-z0-9_-]+$/.test(value);
  const ids=value=>Array.isArray(value)&&value.length>0&&value.length<=1000&&value.every(id)&&
    new Set(value).size===value.length&&value.every((item,index)=>index===0||value[index-1]<item);
  const attributeIds=value=>{try{const parsed=JSON.parse(value);return ids(parsed)?parsed:null;}catch(_){return null;}};
  const code=value=>typeof value==='string'&&/^[A-Za-z0-9]{4}$/.test(value);
  try{
    if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs)||budgetMs<=0||
      !/^[0-9a-f]{64}$/.test(baselineIds)||!/^[0-9a-f]{64}$/.test(baselineObservation)||
      typeof location.href!=='string'||!location.href.startsWith('https://pan.baidu.com/disk/main')||
      window.top!==window||document.defaultView!==window)return fail('PageNotValidated');
    const roots=Array.from(document.querySelectorAll('[data-managed-share-page]'));
    if(roots.length!==1)return fail('PageNotValidated');const root=roots[0];
    const uid=root.getAttribute('data-uid');if(uid!==expected.baiduUserId)
      return fail(exact(uid)?'AccountIdentityMismatch':'PageNotValidated');
    if(root.getAttribute('data-route')!=='disk-main'||root.getAttribute('data-browser-id')!==expected.browserId||
      root.getAttribute('data-browser-target-id')!==expected.targetId||
      root.getAttribute('data-directory-id')!==expected.targetDirectoryExactId||
      root.getAttribute('data-directory-path')!==expected.targetCanonicalPath)return fail('PageNotValidated');
    const results=Array.from(root.querySelectorAll('[data-managed-share-official-result]'));
    if(results.length!==1)return fail('PageNotValidated');const node=results[0],status=node.getAttribute('data-status');
    if(status==='rejected')return fail('ExplicitlyRejected');if(status==='conflict')return fail('Conflict');
    if(status!=='accepted')return fail('UnboundAcknowledgement');
    const value={kind:'AcceptedBound',adapterRequestId:node.getAttribute('data-adapter-request-id'),
      shareId:node.getAttribute('data-share-id'),shareUrl:node.getAttribute('data-share-url'),
      exactItemIds:attributeIds(node.getAttribute('data-exact-item-ids')),isPermanent:node.getAttribute('data-permanent')==='true',
      isAccessUnlimited:node.getAttribute('data-unlimited')==='true',codeMode:node.getAttribute('data-code-mode'),
      effectiveCode:node.getAttribute('data-effective-code')};
    if(!exact(value.adapterRequestId)||!exact(value.shareId)||typeof value.shareUrl!=='string'||
      JSON.stringify(value.exactItemIds)!==JSON.stringify(expected.exactItemIds)||!value.isPermanent||!value.isAccessUnlimited||
      value.codeMode!==expected.codeMode||!code(value.effectiveCode)||
      (expected.codeMode==='Fixed'&&value.effectiveCode!==expected.fixedCode))return fail('PageNotValidated');
    return value;
  }catch(_){return fail('PageNotValidated');}
})(arguments[0],arguments[1],arguments[2],arguments[3]);
"""


def _context(payload):
    return {field: payload[field] for field in protocol.SHARE_SUBMISSION_CONTEXT_FIELDS}


def read_official_listing(tab, payload, remaining_seconds):
    """Read the complete stable official share list without navigation or mutation."""
    try:
        protocol.validate_share_submission_request("ReadShareOfficialListing", payload)
        if (type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or
                remaining_seconds <= 0):
            return {"kind": "TimedOut"}
        value = tab.run_js(READ_SHARE_OFFICIAL_LISTING_SCRIPT, payload,
                           remaining_seconds * 1000.0, timeout=remaining_seconds)
        if type(value) is dict and value.get("kind") == "Observed":
            expected = (("kind",) + protocol.SHARE_SUBMISSION_CONTEXT_FIELDS +
                        ("totalCount", "reachedEnd", "isStable", "entries"))
            protocol._exact_object(value, expected, body=True)
            value = {**value,
                     "shareIdsFingerprint": protocol.compute_share_ids_fingerprint(
                         item["shareId"] for item in value["entries"])}
            value = {**value, "observationFingerprint":
                     protocol.compute_share_listing_fingerprint(value)}
        protocol.validate_share_listing_result(value)
        return value
    except protocol.ProtocolError:
        return {"kind": "PageNotValidated"}
    except Exception:
        return {"kind": "BrowserUnavailable"}


def submit_once(tab, payload, remaining_seconds):
    """Re-read the baseline and cross the fixed isolated-world create boundary at most once."""
    try:
        protocol.validate_share_submission_request("SubmitShareOnce", payload)
    except Exception:
        return {"kind": "BaselineMismatch", "dispatchState": "KnownNotDispatched"}
    if (type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or
            remaining_seconds <= 0):
        return {"kind": "TimedOut", "dispatchState": "KnownNotDispatched"}
    reader = getattr(tab, "read_share_official_listing", None)
    current = (reader(_context(payload), remaining_seconds) if callable(reader)
               else read_official_listing(tab, _context(payload), remaining_seconds))
    if current.get("kind") != "Observed":
        kind = current.get("kind", "PageNotValidated")
        return {"kind": kind, "dispatchState": "KnownNotDispatched"}
    runner = getattr(tab, "run_share_submit_isolated", None)
    if not callable(runner):
        return {"kind": "PageNotValidated", "dispatchState": "KnownNotDispatched"}
    try:
        value = runner(
            _context(payload), payload["baselineShareIdsFingerprint"],
            payload["baselineObservationFingerprint"], current["shareIdsFingerprint"],
            current["observationFingerprint"], remaining_seconds * 1000.0,
            timeout=remaining_seconds)
        protocol.validate_share_submit_result(value)
        return value
    except Exception:
        return {"kind": "BrowserUnavailable", "dispatchState": "DispatchOutcomeUnknown"}


def observe_outcome(tab, payload, remaining_seconds):
    """Read only the exact official result binding; never click, navigate, dismiss, or retry."""
    try:
        protocol.validate_share_submission_request("ObserveShareSubmissionOutcome", payload)
        if (type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or
                remaining_seconds <= 0):
            return {"kind": "TimedOut"}
        value = tab.run_js(
            OBSERVE_SHARE_SUBMISSION_OUTCOME_SCRIPT, _context(payload),
            payload["baselineShareIdsFingerprint"], payload["baselineObservationFingerprint"],
            remaining_seconds * 1000.0, timeout=remaining_seconds)
        if type(value) is dict and value.get("kind") == "AcceptedBound":
            expected = ("kind", "adapterRequestId", "shareId", "shareUrl", "exactItemIds",
                        "isPermanent", "isAccessUnlimited", "codeMode", "effectiveCode")
            protocol._exact_object(value, expected, body=True)
            value = {**value, "officialObservationFingerprint":
                     protocol.compute_share_outcome_fingerprint(
                         value["adapterRequestId"], value["shareId"], value["shareUrl"],
                         value["exactItemIds"], value["isPermanent"], value["isAccessUnlimited"],
                         value["codeMode"], value["effectiveCode"])}
        protocol.validate_share_outcome_result(value)
        return value
    except protocol.ProtocolError:
        return {"kind": "PageNotValidated"}
    except Exception:
        return {"kind": "BrowserUnavailable"}


def dispatch_bound(action, payload, connector, *, budget):
    """Execute one bound fixed share action and conservatively classify possible dispatch."""
    dispatched = False
    try:
        protocol.validate_share_submission_request(action, payload)
    except Exception:
        if action == "SubmitShareOnce":
            return {"kind": "BaselineMismatch", "dispatchState": "KnownNotDispatched"}
        return {"kind": "PageNotValidated"}
    try:
        call = budget.call
        if call(lambda _: connector.browser_id) != payload["browserId"]:
            raise _Stopped("BrowserUnavailable")
        target = call(lambda remaining: connector.get_target(payload["targetId"], remaining))
        if target is None:
            raise _Stopped("BrowserUnavailable")

        def binding():
            if (call(lambda _: connector.browser_id) != payload["browserId"] or
                    call(lambda remaining: connector.get_target(payload["targetId"], remaining)) is not target or
                    call(lambda _: target.target_id) != payload["targetId"]):
                raise _Stopped("BrowserUnavailable")

        binding()
        if action == "ReadShareOfficialListing":
            value = call(lambda remaining: target.read_share_official_listing(payload, remaining))
            binding(); protocol.validate_share_listing_result(value)
        elif action == "SubmitShareOnce":
            dispatched = True
            value = call(lambda remaining: target.submit_share_once(payload, remaining))
            protocol.validate_share_submit_result(value)
            if value["dispatchState"] == "KnownNotDispatched":
                dispatched = False
            binding()
        else:
            value = call(lambda remaining: target.observe_share_submission_outcome(payload, remaining))
            binding(); protocol.validate_share_outcome_result(value)
        budget.remaining()
        return value
    except _Stopped as stopped:
        if action == "SubmitShareOnce":
            return {"kind": stopped.kind, "dispatchState":
                    "DispatchOutcomeUnknown" if dispatched else "KnownNotDispatched"}
        return {"kind": stopped.kind}
    except Exception:
        if action == "SubmitShareOnce":
            return {"kind": "BrowserUnavailable", "dispatchState":
                    "DispatchOutcomeUnknown" if dispatched else "KnownNotDispatched"}
        return {"kind": "BrowserUnavailable"}
