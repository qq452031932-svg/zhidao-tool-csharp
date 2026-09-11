"""Fixed transfer-target baseline actions.

DOM/navigation implementations are intentionally added one tested behavior at a time.
This module never accepts caller-supplied scripts, selectors, or target URLs.
"""

from __future__ import annotations

import copy
import hmac

import protocol
import transfer_source


TARGET_ROOT_PROOF_SCRIPT = r"""
return (function(expectedUid, budgetMs){
  'use strict';
  const fail=kind=>({kind}), exact=(value,max=256)=>typeof value==='string'&&value.length>0&&
    new TextEncoder().encode(value).length<=max&&!/[\u0000-\u001f\u007f-\u009f]/.test(value);
  try {
    if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs)||budgetMs<=0)return fail('TimedOut');
    if(!exact(expectedUid)||window.top!==window||document.defaultView!==window||
      location.href!=='https://pan.baidu.com/disk/main#/index?category=all')return fail('PageNotValidated');
    const states=Array.from(document.querySelectorAll('[data-page-state]'));
    if(states.length){
      if(states.length!==1)return fail('PageNotValidated');
      const state=states[0].getAttribute('data-page-state');
      return fail(state==='LoginRequired'||state==='VerificationRequired'?state:'PageNotValidated');
    }
    const roots=Array.from(document.querySelectorAll('[data-managed-target-root]'));
    if(roots.length!==1)return fail('PageNotValidated');
    const root=roots[0];
    if(root.getAttribute('data-route')!=='root'||root.getAttribute('data-directory-id')===null||
       root.getAttribute('data-path')!=='/'||root.getAttribute('data-ancestor-ids')!==
         JSON.stringify([root.getAttribute('data-directory-id')]))return fail('PageNotValidated');
    const uid=root.getAttribute('data-uid');
    if(!exact(uid))return fail('PageNotValidated');
    return fail(uid===expectedUid?'RootOpened':'AccountIdentityMismatch');
  }catch(_){return fail('PageNotValidated');}
})(arguments[0],arguments[1]);
"""


TARGET_PARENT_READ_SCRIPT = r"""
return (async function(expectedUid,budgetMs,browserId,targetId,endpoint,expectedDirectoryId,expectedPath,expectedAncestors){
  'use strict';
  const fail=kind=>({kind}), bytes=value=>new TextEncoder().encode(value).length;
  const exact=(value,max=256)=>typeof value==='string'&&value.length>0&&bytes(value)<=max&&
    !/[\u0000-\u001f\u007f-\u009f]/.test(value);
  const id=value=>exact(value,128)&&/^[A-Za-z0-9_-]+$/.test(value);
  const name=value=>exact(value,256)&&!/[\\/]/.test(value);
  const path=value=>{if(!exact(value,4096)||value[0]!=='/'||value.includes('\\'))return null;
    if(value==='/')return [];const parts=value.slice(1).split('/');return parts.length<=64&&parts.every(p=>name(p)&&p!=='.'&&p!=='..')?parts:null;};
  const pageState=()=>{const states=Array.from(document.querySelectorAll('[data-page-state]'));
    if(!states.length)return null;if(states.length!==1)return 'PageNotValidated';
    const state=states[0].getAttribute('data-page-state');
    return state==='LoginRequired'||state==='VerificationRequired'?state:'PageNotValidated';};
  const readHeader=()=>{
    const state=pageState();if(state)return {failure:state};
    const roots=Array.from(document.querySelectorAll('[data-managed-target-root]'));
    if(roots.length!==1)return {failure:'PageNotValidated'};const root=roots[0],route=root.getAttribute('data-route');
    if(!['root','directory'].includes(route))return {failure:'PageNotValidated'};
    const uid=root.getAttribute('data-uid');if(!exact(uid))return {failure:'PageNotValidated'};
    if(uid!==expectedUid)return {failure:'AccountIdentityMismatch'};
    const directoryExactId=root.getAttribute('data-directory-id'),canonicalPath=root.getAttribute('data-path');
    const parts=path(canonicalPath);if(!id(directoryExactId)||parts===null)return {failure:'PageNotValidated'};
    let ancestors;try{ancestors=JSON.parse(root.getAttribute('data-ancestor-ids'));}catch(_){return {failure:'PageNotValidated'};}
    if(!Array.isArray(ancestors)||ancestors.length!==parts.length+1||ancestors.length>65||
      !ancestors.every(id)||new Set(ancestors).size!==ancestors.length||ancestors.at(-1)!==directoryExactId)
      return {failure:'PageNotValidated'};
    const allUndefined=expectedDirectoryId===undefined&&expectedPath===undefined&&expectedAncestors===undefined;
    const allNull=expectedDirectoryId===null&&expectedPath===null&&expectedAncestors===null;
    const proofOmitted=allUndefined||allNull;
    if(!proofOmitted&&(expectedDirectoryId==null||expectedPath==null||expectedAncestors==null))
      return {failure:'PageNotValidated'};
    if(proofOmitted){if(route!=='root'||canonicalPath!=='/'||ancestors.length!==1||ancestors[0]!==directoryExactId)
      return {failure:'PageNotValidated'};}
    else if(route!=='directory'||directoryExactId!==expectedDirectoryId||canonicalPath!==expectedPath||
      JSON.stringify(ancestors)!==JSON.stringify(expectedAncestors))return {failure:'PageNotValidated'};
    const generationToken=root.getAttribute('data-generation'),totalText=root.getAttribute('data-total-count');
    const legacySteps=root.getAttribute('data-steps');
    if(!exact(generationToken)||!/^(0|[1-9][0-9]*)$/.test(totalText||'')||
      (legacySteps!==null&&(!/^[1-9][0-9]*$/.test(legacySteps)||Number(legacySteps)>64)))return {failure:'PageNotValidated'};
    return {root,uid,directoryExactId,canonicalPath,ancestors,generationToken,totalCount:Number(totalText)};
  };
  try {
    if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs)||budgetMs<=0)return fail('TimedOut');
    if(!exact(expectedUid)||!exact(browserId)||!exact(targetId)||!exact(endpoint)||
      location.href!=='https://pan.baidu.com/disk/main#/index?category=all'||window.top!==window||document.defaultView!==window)
      return fail('PageNotValidated');
    const initial=readHeader();if(initial.failure)return fail(initial.failure);
    const resets=Array.from(initial.root.querySelectorAll('[data-managed-target-reset]'));
    if(resets.length!==1||typeof resets[0].click!=='function')return fail('PageNotValidated');
    resets[0].click();await Promise.resolve();
    const itemsById=new Map();let baseline=null;
    for(let step=0;step<64;step++){
      const current=readHeader();if(current.failure)return fail(current.failure);
      const pageIndex=current.root.getAttribute('data-page-index');
      if(pageIndex!==String(step))return fail('PageNotValidated');
      const header=[current.uid,current.directoryExactId,current.canonicalPath,JSON.stringify(current.ancestors),
        current.generationToken,current.totalCount];
      if(baseline===null)baseline=header;else if(JSON.stringify(header)!==JSON.stringify(baseline))return fail('PageNotValidated');
      const endText=current.root.getAttribute('data-end');if(endText!=='true'&&endText!=='false')return fail('PageNotValidated');
      const rows=Array.from(current.root.querySelectorAll('[data-target-item-id]'));
      if(rows.length>64)return fail('PageNotValidated');let added=0;
      for(const row of rows){const exactId=row.getAttribute('data-target-item-id'),kind=row.getAttribute('data-kind'),serverName=row.getAttribute('data-name');
        if(!id(exactId)||!['File','Folder'].includes(kind)||!name(serverName)||current.ancestors.includes(exactId))
          return fail('PageNotValidated');
        const item={exactId,kind,serverName};
        if(itemsById.has(exactId))return fail('PageNotValidated');
        if(itemsById.size>=1024)return fail('PageNotValidated');
        itemsById.set(exactId,item);added++;
      }
      if(endText==='true'){
        if(current.totalCount!==itemsById.size)return fail('PageNotValidated');
        const items=Array.from(itemsById.values()).sort((a,b)=>a.exactId<b.exactId?-1:a.exactId>b.exactId?1:0);
        return {kind:'Observed',scope:'AllItems',browserId,targetId,devToolsEndpoint:endpoint,baiduUserId:current.uid,
          generationToken:current.generationToken,directoryExactId:current.directoryExactId,canonicalPath:current.canonicalPath,
          ancestorExactIds:current.ancestors,totalCount:current.totalCount,reachedEnd:true,isStable:true,
          enumerationStepCount:step+1,items};
      }
      if(added===0)return fail('PageNotValidated');
      const next=Array.from(current.root.querySelectorAll('[data-managed-target-next]'));
      if(next.length!==1||typeof next[0].click!=='function')return fail('PageNotValidated');
      next[0].click();await Promise.resolve();
    }
    return fail('PageNotValidated');
  }catch(_){return fail('PageNotValidated');}
})(arguments[0],arguments[1],arguments[2],arguments[3],arguments[4],arguments[5],arguments[6],arguments[7]);
"""


TARGET_ENTER_CHILD_SCRIPT = r"""
return (async function(expectedUid,budgetMs,browserId,targetId,endpoint,expectedParent,childId,childName,childPath,expectedFingerprint){
  'use strict';let navigationAttempted=false;
  const fail=kind=>({kind,navigationAttempted}),bytes=value=>new TextEncoder().encode(value).length;
  const exact=(value,max=256)=>typeof value==='string'&&value.length>0&&bytes(value)<=max&&!/[\u0000-\u001f\u007f-\u009f]/.test(value);
  const id=value=>exact(value,128)&&/^[A-Za-z0-9_-]+$/.test(value),name=value=>exact(value,256)&&!/[\\/]/.test(value);
  const pageState=()=>{const states=Array.from(document.querySelectorAll('[data-page-state]'));
    if(!states.length)return null;if(states.length!==1)return 'PageNotValidated';
    const state=states[0].getAttribute('data-page-state');
    return state==='LoginRequired'||state==='VerificationRequired'?state:'PageNotValidated';};
  const readHeader=(step,requireFingerprint=false)=>{
    const state=pageState();if(state)return {failure:state};
    const roots=Array.from(document.querySelectorAll('[data-managed-target-root]'));
    if(roots.length!==1)return {failure:'PageNotValidated'};const root=roots[0];
    const expectedRoute=expectedParent.canonicalPath==='/'?'root':'directory';
    if(root.getAttribute('data-route')!==expectedRoute||root.getAttribute('data-uid')!==expectedUid||
      root.getAttribute('data-directory-id')!==expectedParent.directoryExactId||
      root.getAttribute('data-path')!==expectedParent.canonicalPath||
      root.getAttribute('data-ancestor-ids')!==JSON.stringify(expectedParent.ancestorExactIds)||
      root.getAttribute('data-generation')!==expectedParent.generationToken||
      root.getAttribute('data-total-count')!==String(expectedParent.totalCount))return {failure:'PageNotValidated'};
    if(requireFingerprint&&root.getAttribute('data-observation-fingerprint')!==expectedFingerprint)
      return {failure:'PageNotValidated'};
    if(step!==null&&root.getAttribute('data-page-index')!==String(step))return {failure:'PageNotValidated'};
    const end=root.getAttribute('data-end');if(end!=='true'&&end!=='false')return {failure:'PageNotValidated'};
    return {root,end};
  };
  const resetToHead=async()=>{const current=readHeader(null);if(current.failure)return current;
    const controls=Array.from(current.root.querySelectorAll('[data-managed-target-reset]'));
    if(controls.length!==1||typeof controls[0].click!=='function')return {failure:'PageNotValidated'};
    controls[0].click();await Promise.resolve();return {};};
  try {
    if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs)||budgetMs<=0)return fail('TimedOut');
    if(!expectedParent||expectedParent.kind!=='Observed'||expectedParent.scope!=='AllItems'||
      expectedParent.browserId!==browserId||expectedParent.targetId!==targetId||expectedParent.devToolsEndpoint!==endpoint||
      expectedParent.baiduUserId!==expectedUid||!Array.isArray(expectedParent.ancestorExactIds)||
      !Array.isArray(expectedParent.items)||!Number.isInteger(expectedParent.totalCount)||
      typeof expectedFingerprint!=='string'||!/^[0-9a-f]{64}$/.test(expectedFingerprint)||
      !id(childId)||!name(childName)||typeof childPath!=='string')return fail('PageNotValidated');
    const expectedPath=(expectedParent.canonicalPath==='/'?'/':expectedParent.canonicalPath+'/')+childName;
    if(childPath!==expectedPath||expectedParent.ancestorExactIds.includes(childId)||
      location.href!=='https://pan.baidu.com/disk/main#/index?category=all')return fail('PageNotValidated');
    const declared=expectedParent.items.filter(item=>item&&item.exactId===childId);
    if(declared.length!==1||declared[0].kind!=='Folder'||declared[0].serverName!==childName)return fail('PageNotValidated');
    let reset=await resetToHead();if(reset.failure)return fail(reset.failure);
    const itemsById=new Map();let childOccurrences=0,completed=false,steps=0;
    for(let step=0;step<64;step++){
      const current=readHeader(step);if(current.failure)return fail(current.failure);steps=step+1;
      const rows=Array.from(current.root.querySelectorAll('[data-target-item-id]'));
      if(rows.length>64)return fail('PageNotValidated');let added=0;
      for(const row of rows){const exactId=row.getAttribute('data-target-item-id'),kind=row.getAttribute('data-kind'),serverName=row.getAttribute('data-name');
        if(!id(exactId)||!['File','Folder'].includes(kind)||!name(serverName)||expectedParent.ancestorExactIds.includes(exactId))
          return fail('PageNotValidated');
        const item={exactId,kind,serverName};
        if(itemsById.has(exactId))return fail('PageNotValidated');
        if(exactId===childId){childOccurrences++;if(kind!=='Folder'||serverName!==childName)return fail('PageNotValidated');}
        if(itemsById.size>=1024)return fail('PageNotValidated');itemsById.set(exactId,item);added++;
      }
      if(current.end==='true'){
        if(itemsById.size!==expectedParent.totalCount||childOccurrences!==1)return fail('PageNotValidated');
        completed=true;break;
      }
      if(added===0)return fail('PageNotValidated');
      const next=Array.from(current.root.querySelectorAll('[data-managed-target-next]'));
      if(next.length!==1||typeof next[0].click!=='function')return fail('PageNotValidated');
      next[0].click();await Promise.resolve();
    }
    if(!completed)return fail('PageNotValidated');
    const items=Array.from(itemsById.values()).sort((a,b)=>a.exactId<b.exactId?-1:a.exactId>b.exactId?1:0);
    const actual={kind:'Observed',scope:'AllItems',browserId,targetId,devToolsEndpoint:endpoint,baiduUserId:expectedUid,
      generationToken:expectedParent.generationToken,directoryExactId:expectedParent.directoryExactId,
      canonicalPath:expectedParent.canonicalPath,ancestorExactIds:expectedParent.ancestorExactIds,
      totalCount:expectedParent.totalCount,reachedEnd:true,isStable:true,enumerationStepCount:steps,items};
    if(JSON.stringify(actual)!==JSON.stringify(expectedParent))return fail('PageNotValidated');
    reset=await resetToHead();if(reset.failure)return fail(reset.failure);
    const finalItemsById=new Map();let finalChild=null,finalChildOccurrences=0,finalCompleted=false,finalSteps=0;
    for(let step=0;step<64;step++){
      const current=readHeader(step);if(current.failure)return fail(current.failure);finalSteps=step+1;
      const rows=Array.from(current.root.querySelectorAll('[data-target-item-id]'));
      if(rows.length>64)return fail('PageNotValidated');let added=0;
      for(const row of rows){const exactId=row.getAttribute('data-target-item-id'),kind=row.getAttribute('data-kind'),serverName=row.getAttribute('data-name');
        if(!id(exactId)||!['File','Folder'].includes(kind)||!name(serverName)||expectedParent.ancestorExactIds.includes(exactId))
          return fail('PageNotValidated');
        const item={exactId,kind,serverName};
        if(finalItemsById.has(exactId))return fail('PageNotValidated');
        if(finalItemsById.size>=1024)return fail('PageNotValidated');
        finalItemsById.set(exactId,item);added++;
        if(exactId===childId){finalChildOccurrences++;finalChild=row;
          if(kind!=='Folder'||serverName!==childName)return fail('PageNotValidated');}
      }
      if(current.end==='true'){
        if(finalItemsById.size!==expectedParent.totalCount||finalChildOccurrences!==1)return fail('PageNotValidated');
        finalCompleted=true;break;
      }
      if(added===0)return fail('PageNotValidated');
      const next=Array.from(current.root.querySelectorAll('[data-managed-target-next]'));
      if(next.length!==1||typeof next[0].click!=='function')return fail('PageNotValidated');
      next[0].click();await Promise.resolve();
    }
    if(!finalCompleted||finalSteps!==expectedParent.enumerationStepCount)return fail('PageNotValidated');
    const finalItems=Array.from(finalItemsById.values()).sort((a,b)=>a.exactId<b.exactId?-1:a.exactId>b.exactId?1:0);
    const finalActual={kind:'Observed',scope:'AllItems',browserId,targetId,devToolsEndpoint:endpoint,baiduUserId:expectedUid,
      generationToken:expectedParent.generationToken,directoryExactId:expectedParent.directoryExactId,
      canonicalPath:expectedParent.canonicalPath,ancestorExactIds:expectedParent.ancestorExactIds,
      totalCount:expectedParent.totalCount,reachedEnd:true,isStable:true,enumerationStepCount:finalSteps,items:finalItems};
    if(JSON.stringify(finalActual)!==JSON.stringify(expectedParent))return fail('PageNotValidated');
    reset=await resetToHead();if(reset.failure)return fail(reset.failure);
    const locateSeen=new Set();
    for(let step=0;step<64;step++){
      const current=readHeader(step,true);if(current.failure)return fail(current.failure);
      const rows=Array.from(current.root.querySelectorAll('[data-target-item-id]'));
      if(rows.length>64)return fail('PageNotValidated');let added=0;
      for(const row of rows){const exactId=row.getAttribute('data-target-item-id'),kind=row.getAttribute('data-kind'),serverName=row.getAttribute('data-name');
        if(!id(exactId)||!['File','Folder'].includes(kind)||!name(serverName)||expectedParent.ancestorExactIds.includes(exactId)||
          locateSeen.has(exactId)||locateSeen.size>=1024)return fail('PageNotValidated');
        locateSeen.add(exactId);added++;
      }
      const matches=rows.filter(row=>row.getAttribute('data-target-item-id')===childId);
      if(matches.length){if(matches.length!==1||matches[0].isConnected!==true||
          matches[0].getAttribute('data-kind')!=='Folder'||matches[0].getAttribute('data-name')!==childName||
          typeof matches[0].click!=='function')return fail('PageNotValidated');
        const proof=readHeader(step,true);if(proof.failure||proof.root!==current.root)return fail('PageNotValidated');
        const finalMatches=Array.from(proof.root.querySelectorAll('[data-target-item-id]'))
          .filter(row=>row.getAttribute('data-target-item-id')===childId);
        if(finalMatches.length!==1||finalMatches[0]!==matches[0]||matches[0].isConnected!==true||
          matches[0].getAttribute('data-kind')!=='Folder'||matches[0].getAttribute('data-name')!==childName)
          return fail('PageNotValidated');
        navigationAttempted=true;matches[0].click();return fail('DirectoryEntered');}
      if(current.end==='true'||added===0)return fail('PageNotValidated');
      const next=Array.from(current.root.querySelectorAll('[data-managed-target-next]'));
      if(next.length!==1||typeof next[0].click!=='function')return fail('PageNotValidated');
      next[0].click();await Promise.resolve();
    }
    return fail('PageNotValidated');
  }catch(_){return fail(navigationAttempted?'OutcomeUnknown':'PageNotValidated');}
})(arguments[0],arguments[1],arguments[2],arguments[3],arguments[4],arguments[5],arguments[6],arguments[7],arguments[8],arguments[9]);
"""

# The post-entry action deliberately uses the same audited all-item reader. Keeping a distinct
# fixed symbol prevents dispatch from substituting a caller-selected script.
TARGET_POST_ENTRY_READ_SCRIPT = TARGET_PARENT_READ_SCRIPT
TARGET_INVENTORY_PASS_ONE_SCRIPT = TARGET_POST_ENTRY_READ_SCRIPT


SOURCE_RESTORE_PROOF_SCRIPT = r"""
return (function(expectedUid,budgetMs,sourceUri,expectedSource){
  'use strict';
  const fail=kind=>({kind}),bytes=value=>new TextEncoder().encode(value).length;
  const exact=(value,max=256)=>typeof value==='string'&&value.length>0&&bytes(value)<=max&&!/[\u0000-\u001f\u007f-\u009f]/.test(value);
  const id=value=>exact(value,128)&&/^[A-Za-z0-9_-]+$/.test(value),name=value=>exact(value,256)&&!/[\\/]/.test(value);
  try {
    if(typeof budgetMs!=='number'||!Number.isFinite(budgetMs)||budgetMs<=0)return fail('TimedOut');
    if(!exact(expectedUid)||typeof sourceUri!=='string'||
      !/^https:\/\/pan\.baidu\.com\/s\/[A-Za-z0-9_-]{1,128}(?:\?pwd=[A-Za-z0-9]{4})?$/.test(sourceUri)||
      !expectedSource||expectedSource.kind!=='Observed'||window.top!==window||document.defaultView!==window)return fail('PageNotValidated');
    if(location.href!==sourceUri&&location.href!==sourceUri.split('?',1)[0])return fail('PageNotValidated');
    const states=Array.from(document.querySelectorAll('[data-page-state]'));
    if(states.length){if(states.length!==1)return fail('PageNotValidated');const state=states[0].getAttribute('data-page-state');
      return fail(state==='LoginRequired'||state==='VerificationRequired'?state:'PageNotValidated');}
    const roots=Array.from(document.querySelectorAll('[data-managed-source-root]'));
    if(roots.length!==1)return fail('PageNotValidated');const root=roots[0],uid=root.getAttribute('data-uid');
    if(!exact(uid))return fail('PageNotValidated');if(uid!==expectedUid)return fail('AccountIdentityMismatch');
    const generationToken=root.getAttribute('data-generation'),totalText=root.getAttribute('data-total-count');
    if(!exact(generationToken)||!/^(0|[1-9][0-9]*)$/.test(totalText||'')||root.getAttribute('data-end')!=='true')return fail('PageNotValidated');
    const rows=Array.from(root.querySelectorAll('[data-source-item-id]')),seen=new Set(),items=[];
    if(rows.length>32||Number(totalText)!==rows.length)return fail('PageNotValidated');
    for(const row of rows){const exactId=row.getAttribute('data-source-item-id'),kind=row.getAttribute('data-kind'),serverName=row.getAttribute('data-name');
      if(!id(exactId)||!['File','Folder'].includes(kind)||!name(serverName)||seen.has(exactId))return fail('PageNotValidated');
      seen.add(exactId);items.push({exactId,kind,serverName});}
    items.sort((a,b)=>a.exactId<b.exactId?-1:a.exactId>b.exactId?1:0);
    const actual={kind:'Observed',baiduUserId:uid,generationToken,totalCount:rows.length,reachedEnd:true,isStable:true,items};
    const expected={...expectedSource};delete expected.observationFingerprint;
    return fail(JSON.stringify(actual)===JSON.stringify(expected)?'SourceRestored':'PageNotValidated');
  }catch(_){return fail('PageNotValidated');}
})(arguments[0],arguments[1],arguments[2],arguments[3]);
"""


def validate_payload(action, payload):
    protocol.validate_target_baseline_request(action, payload)


def select_stable_inventory_passes(first, second):
    """Accept only two independently complete observations with one exact wire fingerprint."""
    try:
        protocol.validate_target_inventory_result(first, require_fingerprint=False)
        protocol.validate_target_inventory_result(second, require_fingerprint=False)
        if first["kind"] != "Observed" or second["kind"] != "Observed":
            return {"kind": "PageNotValidated"}
        first_fingerprint = protocol.compute_target_inventory_fingerprint(first)
        second_fingerprint = protocol.compute_target_inventory_fingerprint(second)
        if not hmac.compare_digest(first_fingerprint, second_fingerprint):
            return {"kind": "PageNotValidated"}
        result = copy.deepcopy(second)
        result["items"] = sorted(result["items"], key=lambda item: item["exactId"])
        result["observationFingerprint"] = second_fingerprint
        protocol.validate_target_inventory_result(result)
        return result
    except Exception:
        return {"kind": "PageNotValidated"}


def _denial(value, fallback="BrowserUnavailable"):
    if type(value) is dict and set(value) == {"kind"} and value["kind"] in protocol.TARGET_DENIAL_KINDS:
        return {"kind": value["kind"]}
    return {"kind": fallback}


def _header_matches(expected, observed):
    try:
        protocol.validate_target_inventory_result(observed, require_fingerprint=False)
        if observed["kind"] != "Observed":
            return False
        actual = {field: observed[field] for field in (
            "scope", "browserId", "targetId", "devToolsEndpoint", "baiduUserId", "generationToken",
            "directoryExactId", "canonicalPath", "ancestorExactIds", "totalCount", "reachedEnd",
            "isStable", "enumerationStepCount")}
        actual["parentObservationFingerprint"] = protocol.compute_target_inventory_fingerprint(observed)
        return actual == expected
    except Exception:
        return False


def dispatch_bound(action, payload, connector, *, budget):
    """Execute one fixed target-baseline action against one immutable browser/target binding."""
    dispatched = False
    try:
        protocol.validate_target_baseline_request(action, payload)
        call = budget.call
        if call(lambda _: connector.browser_id) != payload["browserId"]:
            raise RuntimeError("binding")
        target = call(lambda remaining: connector.get_target(payload["targetId"], remaining))
        if target is None:
            raise RuntimeError("binding")

        def binding():
            if call(lambda _: connector.browser_id) != payload["browserId"]:
                raise RuntimeError("binding")
            if call(lambda remaining: connector.get_target(payload["targetId"], remaining)) is not target:
                raise RuntimeError("binding")
            if call(lambda _: target.target_id) != payload["targetId"]:
                raise RuntimeError("binding")
            if call(lambda _: connector.browser_id) != payload["browserId"]:
                raise RuntimeError("binding")

        def bound(operation):
            binding()
            value = call(operation)
            binding()
            return value

        def bound_dispatch(operation):
            nonlocal dispatched
            binding()
            def invoke(remaining):
                nonlocal dispatched
                dispatched = True
                return operation(remaining)
            value = call(invoke)
            binding()
            return value

        if action == "OpenTransferTargetRoot":
            value = bound_dispatch(lambda remaining: target.open_target_root(payload["expectedBaiduUserId"], remaining))
            if value == {"kind": "RootOpened"}:
                return {"kind": "RootOpened", "navigationState": "RouteOpened"}
            result = _denial(value)
            return {**result, "navigationState": "RouteOutcomeUnknown"}

        if action == "EnterTransferTargetDirectory":
            parent = bound(lambda remaining: target.read_target_inventory(
                payload, payload["expectedParent"]["directoryExactId"],
                payload["expectedParent"]["canonicalPath"],
                payload["expectedParent"]["ancestorExactIds"], remaining))
            if not _header_matches(payload["expectedParent"], parent):
                result = _denial(parent, "PageNotValidated")
                return {**result, "navigationState": "KnownNotDispatched"}
            entered = bound_dispatch(lambda remaining: target.enter_target_child(payload, parent, remaining))
            if type(entered) is dict and set(entered) == {"kind", "navigationAttempted"} \
                    and type(entered["navigationAttempted"]) is bool:
                dispatched = entered["navigationAttempted"]
                if not dispatched and entered["kind"] in protocol.TARGET_DENIAL_KINDS:
                    return {"kind": entered["kind"], "navigationState": "KnownNotDispatched"}
                if dispatched and entered["kind"] != "DirectoryEntered":
                    return {"kind": "BrowserUnavailable", "navigationState": "RouteOutcomeUnknown"}
            else:
                return {"kind": "BrowserUnavailable", "navigationState": "RouteOutcomeUnknown"}
            child_ancestors = parent["ancestorExactIds"] + [payload["childExactId"]]
            observed = bound(lambda remaining: target.read_target_inventory(
                payload, payload["childExactId"], payload["childCanonicalPath"], child_ancestors, remaining))
            if type(observed) is dict and observed.get("kind") == "Observed":
                protocol.validate_target_inventory_result(observed, require_fingerprint=False)
                return {"kind": "DirectoryEntered", "navigationState": "RouteOpened"}
            result = _denial(observed, "PageNotValidated")
            return {**result, "navigationState": "RouteOutcomeUnknown"}

        if action == "ReadTransferTargetDirectory":
            arguments = (payload, payload["expectedDirectoryExactId"], payload["expectedCanonicalPath"],
                         payload["expectedAncestorExactIds"])
            first = bound(lambda remaining: target.read_target_inventory(*arguments, remaining))
            if type(first) is not dict or first.get("kind") != "Observed":
                return _denial(first, "PageNotValidated")
            second = bound(lambda remaining: target.read_target_inventory(*arguments, remaining))
            return select_stable_inventory_passes(first, second)

        if action == "OpenTransferSourceAfterTargetRead":
            expected = payload["expectedTarget"]
            observed = bound(lambda remaining: target.read_target_inventory(
                payload, expected["directoryExactId"], expected["canonicalPath"],
                expected["ancestorExactIds"], remaining))
            if not _header_matches(expected, observed):
                result = _denial(observed, "PageNotValidated")
                return {**result, "navigationState": "KnownNotDispatched"}
            value = bound_dispatch(lambda remaining: target.restore_source(payload, remaining))
            if value == {"kind": "SourceRestored"}:
                return {"kind": "SourceRestored", "navigationState": "RouteOpened"}
            result = _denial(value)
            return {**result, "navigationState": "RouteOutcomeUnknown"}
        raise ValueError("action")
    except transfer_source._Stopped as stopped:
        if action == "ReadTransferTargetDirectory":
            return {"kind": stopped.kind}
        return {"kind": stopped.kind,
                "navigationState": "RouteOutcomeUnknown" if dispatched else "KnownNotDispatched"}
    except Exception:
        if action == "ReadTransferTargetDirectory":
            return {"kind": "BrowserUnavailable"}
        return {"kind": "BrowserUnavailable",
                "navigationState": "RouteOutcomeUnknown" if dispatched else "KnownNotDispatched"}
