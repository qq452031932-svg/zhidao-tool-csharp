"""Fixed, source-bound destination-picker actions; no generic DOM or navigation capability."""
from __future__ import annotations

import math
import time

import protocol
from transfer_source import OperationBudget, _Stopped
from transfer_directory_listing import DIRECTORY_LISTING_SCRIPT, compute_directory_fingerprint
from transfer_source_listing import SOURCE_OBSERVER_FUNCTION, compute_source_fingerprint


COMPETING_DIALOG_SELECTOR = '[role="dialog"],[role="alertdialog"],.login-dialog,.security-verification,.verify-dialog'


DIRECTORY_MUTATION_SCRIPT = r"""
return (async function (action, payload, budgetMs) {
 'use strict';
 const denial = () => ({kind:'PageNotValidated'});
 const success = {OpenTransferDirectoryRoot:'RootOpened',EnterTransferDirectory:'DirectoryEntered',
   BeginCreateTransferDirectory:'CreateDialogOpened',ConfirmCreateTransferDirectory:'DirectoryCreated',CancelTransferPicker:'PickerCancelled'};
 if (!Object.hasOwn(success, action) || typeof budgetMs !== 'number' || !Number.isFinite(budgetMs)) return denial();
 if (budgetMs <= 0) return {kind:'TimedOut'};
 if (!payload || typeof payload !== 'object' || typeof payload.expectedBaiduUserId !== 'string' ||
     typeof payload.sourceUri !== 'string' || !payload.expectedDirectory) return denial();
 const expectedUid=payload.expectedBaiduUserId, expectedDirectory=payload.expectedDirectory;
 const deadline=Date.now()+budgetMs, original=document, href=location.href;
 const readDirectory=async function(expectedUid, remainingMs, expectedSourceUrl) {
""" + DIRECTORY_LISTING_SCRIPT + r"""
 };
 const guard=()=>{if(Date.now()>=deadline)throw 'timeout'; if(document!==original||location.href!==href||
   window.top!==window||document.defaultView!==window)throw 'bad';};
 const remaining=()=>{guard();return deadline-Date.now();};
 const nodes=(root,selector,limit=256)=>{guard();const result=root.querySelectorAll(selector);guard();
   if(!result||result.length>limit)throw 'bad';return Array.from(result);};
 const visible=n=>{guard();if(!n||n.nodeType!==1||!n.isConnected||!n.getClientRects().length)return false;
   let p=n;for(let d=0;p&&d<32;d++,p=p.parentElement){guard();if(p.hidden||p.getAttribute('aria-hidden')==='true')return false;
     const style=getComputedStyle(p);if(style.display==='none'||style.visibility==='hidden'||style.visibility==='collapse'||Number(style.opacity)===0)return false;}return !p;};
 const safe=n=>{const tag=String(n.tagName).toLowerCase(); if(!visible(n)||(tag!=='button'&&!(tag==='div'&&n.getAttribute('role')==='button'))||n.form||['submit','reset'].includes((n.getAttribute('type')||'').toLowerCase()))throw 'bad'; let p=n,d=0; for(;p&&d<32;p=p.parentElement,d++){guard();if(['a','label','summary'].includes(String(p.tagName).toLowerCase())||p.disabled||p.inert||p.getAttribute('disabled')!==null||p.getAttribute('inert')!==null||p.getAttribute('aria-disabled')==='true'||getComputedStyle(p).pointerEvents==='none')throw 'bad';} if(!n.click||p||d>=32)throw 'bad'; return n;};
 const safeInput=n=>{if(!visible(n)||String(n.tagName).toLowerCase()!=='input'||
   (n.getAttribute('type')||'text').toLowerCase()!=='text'||n.form||n.disabled||n.readOnly||n.inert||
   n.getAttribute('disabled')!==null||n.getAttribute('readonly')!==null||n.getAttribute('inert')!==null||
   n.getAttribute('aria-disabled')==='true'||typeof n.dispatchEvent!=='function')throw 'bad';
   let p=n,d=0;for(;p&&d<32;p=p.parentElement,d++){guard();const tag=String(p.tagName).toLowerCase();
     if(['a','label','summary','form'].includes(tag)||p.disabled||p.inert||p.getAttribute('disabled')!==null||
       p.getAttribute('inert')!==null||p.getAttribute('aria-disabled')==='true'||getComputedStyle(p).pointerEvents==='none')throw 'bad';}
   if(p||d>=32)throw 'bad';return n;};
 const controls={OpenTransferDirectoryRoot:'[data-managed-directory-action="open-root"]',EnterTransferDirectory:'[data-managed-directory-action="enter-child"]',BeginCreateTransferDirectory:'[data-managed-directory-action="begin-create"]',ConfirmCreateTransferDirectory:'[data-managed-directory-action="confirm-create"]',CancelTransferPicker:'[data-managed-directory-action="cancel-picker"]'};
 const dialogSelector=""" + repr(COMPETING_DIALOG_SELECTOR) + r""";
 const keys=(value,expected)=>value!==null&&typeof value==='object'&&!Array.isArray(value)&&
   Object.keys(value).length===expected.length&&expected.every(key=>Object.hasOwn(value,key));
 const expectedEvidence=()=>{if(!keys(expectedDirectory,['kind','baiduUserId','generationToken','parentExactId','canonicalPath',
   'ancestorExactIds','totalCount','reachedEnd','isStable','items','observationFingerprint'])||expectedDirectory.kind!=='Observed'||
   expectedDirectory.baiduUserId!==expectedUid||!Array.isArray(expectedDirectory.ancestorExactIds)||!Array.isArray(expectedDirectory.items))throw 'bad';
   const items=expectedDirectory.items.map(item=>({exactId:item.exactId,kind:item.kind,serverName:item.serverName}));
   items.sort((a,b)=>a.exactId<b.exactId?-1:a.exactId>b.exactId?1:0);
   return JSON.stringify({kind:'Observed',baiduUserId:expectedUid,generationToken:expectedDirectory.generationToken,
     parentExactId:expectedDirectory.parentExactId,canonicalPath:expectedDirectory.canonicalPath,
     ancestorExactIds:[...expectedDirectory.ancestorExactIds],totalCount:expectedDirectory.totalCount,
     reachedEnd:expectedDirectory.reachedEnd,isStable:expectedDirectory.isStable,items});};
 let dispatched=false;
 try { guard();
   if(href!==payload.sourceUri&&href!==payload.sourceUri.split('?',1)[0])return denial();
   const expected=expectedEvidence();
   const firstDirectory=await readDirectory(expectedUid,remaining(),href);
   if(!firstDirectory||firstDirectory.kind!=='Observed')return firstDirectory&&['TimedOut','AccountIdentityMismatch'].includes(firstDirectory.kind)?firstDirectory:denial();
   if(JSON.stringify(firstDirectory)!==expected)return denial();
   const find=()=>{guard();const dialogs=nodes(document,dialogSelector).filter(visible);
     if(dialogs.length!==1)throw 'bad';const dialog=dialogs[0];
     if(action==='BeginCreateTransferDirectory'&&(nodes(dialog,'[data-managed-directory-input]').length!==0||
       dialog.getAttribute('data-create-dialog')!==null||dialog.getAttribute('data-create-name')!==null))throw 'bad';
     let createInput=null;
     if(action==='ConfirmCreateTransferDirectory'){
       if(dialog.getAttribute('data-create-dialog')!=='true'||dialog.getAttribute('data-create-name')!==payload.newSegment)throw 'bad';
       const inputs=nodes(dialog,'[data-managed-directory-input]').filter(visible);
       if(inputs.length!==1)throw 'bad';createInput=safeInput(inputs[0]);if(createInput.value!==payload.newSegment)throw 'bad';
     }
     const found=nodes(dialog,controls[action]).filter(visible); if(found.length!==1)throw 'bad'; const control=safe(found[0]);
     if(action==='OpenTransferDirectoryRoot'&&(control.getAttribute('data-exact-id')!==payload.rootBreadcrumbExactId||
       control.getAttribute('data-canonical-path')!=='/'))throw 'bad';
     if(action==='EnterTransferDirectory'){const child=(expectedDirectory.items||[]).find(item=>item.exactId===payload.childExactId); if(!child||control.getAttribute('data-exact-id')!==payload.childExactId||control.getAttribute('data-canonical-path')!==payload.childCanonicalPath||control.getAttribute('data-name')!==child.serverName)throw 'bad';}
     if((action==='BeginCreateTransferDirectory'||action==='ConfirmCreateTransferDirectory')&&control.getAttribute('data-new-segment')!==payload.newSegment)throw 'bad'; return {control,createInput};};
   const one=find(); await Promise.resolve();
   const finalDirectory=await readDirectory(expectedUid,remaining(),href);
   if(!finalDirectory||finalDirectory.kind!=='Observed'||JSON.stringify(finalDirectory)!==expected)return denial();
   const two=find();if(one.control!==two.control||one.createInput!==two.createInput)return denial();guard();dispatched=true;two.control.click.call(two.control);guard();
   if(action==='BeginCreateTransferDirectory'){
     await Promise.resolve();guard();const dialogs=nodes(document,dialogSelector).filter(visible);
     if(dialogs.length!==1||dialogs[0].getAttribute('data-create-dialog')!=='true')throw 'bad';
     const inputs=nodes(dialogs[0],'[data-managed-directory-input]').filter(visible);
     if(inputs.length!==1)throw 'bad';const input=safeInput(inputs[0]);
     input.value=payload.newSegment;if(input.value!==payload.newSegment)throw 'bad';
     input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));guard();
     if(input.value!==payload.newSegment||dialogs[0].getAttribute('data-create-name')!==payload.newSegment)throw 'bad';
     return {kind:success[action]};
   }
   if(action!=='OpenTransferDirectoryRoot'&&action!=='EnterTransferDirectory')return {kind:success[action]};
   const postDirectory=await readDirectory(expectedUid,remaining(),href);
   if(!postDirectory||postDirectory.kind!=='Observed')return {kind:'OutcomeUnknown'};
   const expectedParent=action==='OpenTransferDirectoryRoot'?payload.rootBreadcrumbExactId:payload.childExactId;
   const expectedPath=action==='OpenTransferDirectoryRoot'?'/':payload.childCanonicalPath;
   const expectedAncestors=action==='OpenTransferDirectoryRoot'?[payload.rootBreadcrumbExactId]:[...expectedDirectory.ancestorExactIds,payload.childExactId];
   if(postDirectory.baiduUserId!==expectedUid||postDirectory.parentExactId!==expectedParent||
      postDirectory.canonicalPath!==expectedPath||JSON.stringify(postDirectory.ancestorExactIds)!==JSON.stringify(expectedAncestors))return {kind:'OutcomeUnknown'};
   return {kind:success[action],postDirectory};
 } catch(e) { return e==='timeout'&&!dispatched?{kind:'TimedOut'}:dispatched?{kind:'OutcomeUnknown'}:{kind:'PageNotValidated'}; }
})(arguments[0],arguments[1],arguments[2]);
"""

PICKER_CLOSED_SCRIPT = r"""
return (async function(payload, budgetMs) {
 'use strict';
 const unknown=()=>({kind:'PageNotValidated'});
 if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs))return unknown(); if(budgetMs<=0)return {kind:'TimedOut'};
 const expectedUid=payload&&payload.expectedBaiduUserId,expectedSource=payload&&payload.expectedSource,sourceUri=payload&&payload.sourceUri;
 if(typeof expectedUid!=='string'||!expectedSource||typeof sourceUri!=='string')return unknown();
 const href=location.href;
 if(href!==sourceUri&&href!==sourceUri.split('?',1)[0])return unknown();
 const observer=(""" + SOURCE_OBSERVER_FUNCTION + r""")(expectedUid,budgetMs,href);
 if(observer.denial)return observer.denial;
 const dialogSelector=""" + repr(COMPETING_DIALOG_SELECTOR) + r""";
 const keys=(value,expected)=>value!==null&&typeof value==='object'&&!Array.isArray(value)&&
   Object.keys(value).length===expected.length&&expected.every(key=>Object.hasOwn(value,key));
 function expectedEvidence(){
   if(!keys(expectedSource,['kind','baiduUserId','generationToken','totalCount','reachedEnd','isStable','items','observationFingerprint'])||
      expectedSource.kind!=='Observed'||expectedSource.baiduUserId!==expectedUid||!Array.isArray(expectedSource.items))throw new Error();
   const items=expectedSource.items.map(item=>({exactId:item.exactId,kind:item.kind,serverName:item.serverName}));
   items.sort((a,b)=>a.exactId<b.exactId?-1:a.exactId>b.exactId?1:0);
   return JSON.stringify({kind:'Observed',baiduUserId:expectedUid,generationToken:expectedSource.generationToken,
     totalCount:expectedSource.totalCount,reachedEnd:expectedSource.reachedEnd,isStable:expectedSource.isStable,items});
 }
 function absent(){observer.guard();
   if(observer.nodes(document,dialogSelector).filter(observer.visible).length!==0||
      observer.nodes(document,'[data-managed-directory-action],[data-managed-directory-input],[data-create-dialog]').length!==0)throw new Error();
 }
 try {observer.guard();observer.text(expectedUid);const expected=expectedEvidence();
   const first=observer.snapshot();absent();await Promise.resolve();
   const second=observer.snapshot();absent();
   if(!observer.same(first,second)||JSON.stringify(first.result)!==expected||JSON.stringify(second.result)!==expected)return unknown();
   const final=observer.snapshot();absent();observer.guard();
   if(!observer.same(second,final)||JSON.stringify(final.result)!==expected)return unknown();
   return {kind:'PickerClosed',postSource:final.result};
 } catch(error) {return observer.failure(error);}
})(arguments[0],arguments[1]);
"""


def mutate_directory(tab, action, payload, remaining_seconds):
    try:
        protocol.validate_directory_mutation_payload(action, payload)
        if type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or remaining_seconds <= 0:
            return {'kind': 'TimedOut'}
        value = tab.run_js(DIRECTORY_MUTATION_SCRIPT, action, payload, remaining_seconds * 1000, timeout=remaining_seconds)
        expected_success = protocol.DIRECTORY_MUTATION_SUCCESS_KINDS[action]
        if type(value) is dict and value.get('kind') == expected_success and action in (
                'OpenTransferDirectoryRoot', 'EnterTransferDirectory'):
            if set(value) != {'kind', 'postDirectory'}:
                return {'kind': 'OutcomeUnknown'}
            post = value['postDirectory']
            protocol.validate_read_transfer_directory_result(post, require_fingerprint=False)
            if post['kind'] != 'Observed' or post['baiduUserId'] != payload['expectedBaiduUserId']:
                return {'kind': 'OutcomeUnknown'}
            expected_parent = (payload['rootBreadcrumbExactId'] if action == 'OpenTransferDirectoryRoot'
                               else payload['childExactId'])
            expected_path = '/' if action == 'OpenTransferDirectoryRoot' else payload['childCanonicalPath']
            expected_ancestors = ([payload['rootBreadcrumbExactId']] if action == 'OpenTransferDirectoryRoot'
                                  else [*payload['expectedDirectory']['ancestorExactIds'], payload['childExactId']])
            if (post['parentExactId'] != expected_parent or post['canonicalPath'] != expected_path or
                    post['ancestorExactIds'] != expected_ancestors):
                return {'kind': 'OutcomeUnknown'}
            proven = {**post, 'ancestorExactIds': list(post['ancestorExactIds']),
                      'items': sorted((dict(item) for item in post['items']), key=lambda item: item['exactId'])}
            proven['observationFingerprint'] = compute_directory_fingerprint(payload['sourceUri'], proven)
            protocol.validate_read_transfer_directory_result(proven)
            result = {'kind': expected_success}
            protocol.validate_directory_mutation_result(action, result)
            return result
        protocol.validate_directory_mutation_result(action, value)
        return value
    except Exception:
        return {'kind': 'OutcomeUnknown'}


def observe_picker_closed(tab, payload, remaining_seconds):
    try:
        protocol.validate_observe_transfer_picker_closed_payload(payload)
        if type(remaining_seconds) not in (int, float) or not math.isfinite(remaining_seconds) or remaining_seconds <= 0:
            return {'kind': 'TimedOut'}
        value = tab.run_js(PICKER_CLOSED_SCRIPT, payload, remaining_seconds * 1000, timeout=remaining_seconds)
        if type(value) is dict and value.get('kind') == 'PickerClosed':
            if set(value) != {'kind', 'postSource'}:
                return {'kind': 'PageNotValidated'}
            post = value['postSource']
            protocol.validate_read_transfer_source_result(post, require_fingerprint=False)
            if post['kind'] != 'Observed' or post['baiduUserId'] != payload['expectedBaiduUserId']:
                return {'kind': 'PageNotValidated'}
            proven = {**post, 'items': sorted((dict(item) for item in post['items']), key=lambda item: item['exactId'])}
            proven['observationFingerprint'] = compute_source_fingerprint(payload['sourceUri'], proven)
            expected = {**payload['expectedSource'],
                        'items': sorted((dict(item) for item in payload['expectedSource']['items']),
                                        key=lambda item: item['exactId'])}
            if proven != expected:
                return {'kind': 'PageNotValidated'}
            result = {'kind': 'PickerClosed'}
            protocol.validate_observe_transfer_picker_closed_result(result)
            return result
        protocol.validate_observe_transfer_picker_closed_result(value)
        return value
    except Exception:
        return {'kind': 'PageNotValidated'}


def dispatch_bound(action, payload, connector, *, monotonic=time.monotonic, is_cancelled=lambda: False, budget=None):
    """One exact attachment; any failure after a possible click is opaque uncertainty."""
    dispatched = False
    try:
        protocol.validate_directory_mutation_payload(action, payload)
        budget = budget or OperationBudget(payload['timeoutMilliseconds'], monotonic, is_cancelled)
        call = budget.call
        if call(lambda _: connector.browser_id) != payload['browserId']: raise _Stopped('BrowserUnavailable')
        target = call(lambda remaining: connector.get_target(payload['targetId'], remaining))
        if target is None: raise _Stopped('BrowserUnavailable')
        def binding():
            if call(lambda _: connector.browser_id) != payload['browserId']:
                raise _Stopped('BrowserUnavailable')
            if call(lambda remaining: connector.get_target(payload['targetId'], remaining)) is not target:
                raise _Stopped('BrowserUnavailable')
            if call(lambda _: target.target_id) != payload['targetId']:
                raise _Stopped('BrowserUnavailable')
            if call(lambda _: connector.browser_id) != payload['browserId']:
                raise _Stopped('BrowserUnavailable')
        def bound(operation):
            binding()
            value = call(operation)
            binding()
            return value
        if bound(target.current_url) not in (payload['sourceUri'], payload['sourceUri'].split('?',1)[0]): return {'kind':'PageNotValidated'}
        def dispatch(remaining):
            nonlocal dispatched
            dispatched = True
            return target.mutate_transfer_directory(action, payload, remaining)
        value = bound(dispatch)
        protocol.validate_directory_mutation_result(action,value); budget.remaining(); return value
    except _Stopped as stopped: return {'kind':'OutcomeUnknown' if dispatched else stopped.kind}
    except Exception: return {'kind':'OutcomeUnknown' if dispatched else 'BrowserUnavailable'}
