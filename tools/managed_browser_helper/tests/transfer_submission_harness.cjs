'use strict';
const assert=require('node:assert/strict'),vm=require('node:vm');
let input='';process.stdin.setEncoding('utf8');process.stdin.on('data',c=>input+=c);process.stdin.on('end',()=>run(JSON.parse(input)));
function node(attrs={}){return {attrs:{...attrs},children:[],parentElement:null,isConnected:true,disabled:false,inert:false,
  style:{display:'block',visibility:'visible',pointerEvents:'auto'},tagName:'DIV',textContent:'',clicked:0,
  getAttribute(name){return Object.hasOwn(this.attrs,name)?this.attrs[name]:null;},
  contains(other){for(let p=other;p;p=p.parentElement)if(p===this)return true;return false;},
  querySelectorAll(selector){return descendants(this).filter(value=>matches(value,selector));}};}
function add(parent,child){child.parentElement=parent;parent.children.push(child);return child;}
function descendants(root){const out=[];for(const child of root.children){out.push(child,...descendants(child));}return out;}
function matches(value,selector){
  if(selector==='[data-page-state]')return value.getAttribute('data-page-state')!==null;
  if(selector==='[data-managed-transfer-submission]')return value.getAttribute('data-managed-transfer-submission')!==null;
  if(selector==='[data-managed-transfer-source-item]')return value.getAttribute('data-managed-transfer-source-item')!==null;
  if(selector==='[data-managed-transfer-submit]')return value.getAttribute('data-managed-transfer-submit')!==null;
  if(selector==='[data-managed-transfer-official-result]')return value.getAttribute('data-managed-transfer-official-result')!==null;
  if(selector==='[data-managed-transfer-acknowledgement]')return value.getAttribute('data-managed-transfer-acknowledgement')!==null;
  return false;
}
function fixture(context,scenario){
  const root=node({'data-managed-transfer-submission':'true','data-route':'transfer-picker',
    'data-uid':context.baiduUserId,'data-browser-id':context.browserId,'data-browser-target-id':context.targetId,
    'data-source-generation':context.sourceGenerationToken,
    'data-source-observation-fingerprint':context.sourceObservationFingerprint,
    'data-source-fingerprint':context.sourceFingerprint,'data-selected-target-id':context.selectedTargetExactId,
    'data-selected-target-path':context.selectedTargetCanonicalPath,
    'data-target-baseline-fingerprint':context.targetBaselineFingerprint,
    'data-duplicate-policy':context.duplicatePolicy,'data-source-complete':'true','data-source-count':'1'});
  const source=add(root,node({'data-managed-transfer-source-item':'true','data-source-id':context.sourceExactId,
    'data-kind':context.sourceItemKind}));
  const control=add(root,node({'data-managed-transfer-submit':'true','data-eligible':'true','type':'button'}));
  const activate=()=>{control.clicked++;
    if(scenario==='click-detaches')root.isConnected=false;
    if(scenario==='click-changes')root.attrs['data-source-generation']='changed';
    if(scenario==='click-throws')throw Error('after click');};
  const pagePatchedClick=()=>{control.patchedCalls++;activate();activate();};
  control.tagName='BUTTON';control.patchedCalls=0;
  if(scenario!=='prototype-patched-click')
    control.click=scenario==='instance-patched-click'?pagePatchedClick:activate;
  const roots=[root],states=[];
  if(scenario==='duplicate-root')roots.push(node({'data-managed-transfer-submission':'true'}));
  if(scenario==='duplicate-control')add(root,node({'data-managed-transfer-submit':'true','data-eligible':'true'}));
  if(scenario==='login'||scenario==='verification')states.push(node({'data-page-state':scenario==='login'?'LoginRequired':'VerificationRequired'}));
  if(scenario==='uid')root.attrs['data-uid']='other';
  if(scenario==='source-zero'){root.children=root.children.filter(value=>value!==source);root.attrs['data-source-count']='0';}
  if(scenario==='source-many')add(root,node({'data-managed-transfer-source-item':'true','data-source-id':'source-2','data-kind':'File'}));
  if(scenario==='incomplete')root.attrs['data-source-complete']='false';
  if(scenario==='destination')root.attrs['data-selected-target-id']='other-target';
  const official=node({'data-managed-transfer-official-result':'true','data-status':'accepted',
    'data-request-id':'请求-甲','data-target-id':context.selectedTargetExactId,
    'data-source-id':context.sourceExactId,'data-destination-id':'destination-1'});
  if(scenario==='wrong-official-target')official.attrs['data-target-id']='other-target';
  if(scenario==='conflict')official.attrs['data-status']='conflict';
  if(scenario==='rejected')official.attrs['data-status']='rejected';
  if(['accepted','wrong-official-target','conflict','rejected','other-valid-source'].includes(scenario))add(root,official);
  if(scenario==='toast')add(root,node({'data-managed-transfer-acknowledgement':'true'}));
  return {root,roots,states,control,activate,pagePatchedClick,queries:0,navigations:0,scenario};
}
async function execute(script,args,context,scenario){
  const state=fixture(context,scenario),location={href:scenario==='foreign-route'?'https://foreign.test/':
    scenario==='other-valid-source'?'https://pan.baidu.com/s/other_source':context.sourceUri};
  const document={querySelectorAll(selector){state.queries++;
    if(scenario==='mutation'&&selector==='[data-managed-transfer-submission]'&&state.queries>2)
      state.root.attrs['data-source-generation']='changed';
    if(scenario==='route-drift'&&state.queries>2)location.href='https://pan.baidu.com/s/changed';
    if(selector==='[data-page-state]')return state.states;
    if(selector==='[data-managed-transfer-submission]')return state.roots;
    return [state.root,...descendants(state.root)].filter(value=>matches(value,selector));},defaultView:null};
  const forbidden=()=>{throw Error('forbidden side effect');};
  const sandbox={document,location,TextEncoder,getComputedStyle:value=>value.style,
    setTimeout:forbidden,setInterval:forbidden,fetch:forbidden,XMLHttpRequest:forbidden,WebSocket:forbidden,
    navigator:{sendBeacon:forbidden}};
  const vmContext=vm.createContext(sandbox);vmContext.window=vmContext;vmContext.top=vmContext;
  const HTMLElement=function HTMLElement(){};
  const HTMLButtonElement=function HTMLButtonElement(){};
  Object.setPrototypeOf(HTMLButtonElement.prototype,HTMLElement.prototype);
  if(scenario!=='native-primitive-unavailable')Object.defineProperty(HTMLElement.prototype,'click',{
    configurable:false,writable:false,value:function(){state.activate();}
  });
  if(scenario==='prototype-patched-click')Object.defineProperty(HTMLButtonElement.prototype,'click',{
    configurable:false,writable:false,value:state.pagePatchedClick
  });
  if(scenario!=='button-type-spoof')Object.setPrototypeOf(state.control,HTMLButtonElement.prototype);
  vmContext.HTMLElement=HTMLElement;vmContext.HTMLButtonElement=HTMLButtonElement;
  vm.runInContext('document.defaultView=window;',vmContext);
  const value=vm.runInContext(`(function(){${script}\n}).apply(undefined,${JSON.stringify(args)})`,vmContext,{timeout:3000});
  return {result:JSON.parse(JSON.stringify(await value)),state};
}
async function readiness(value){
  for(const [scenario,kind] of [['valid','Ready'],['duplicate-root','PageNotValidated'],['duplicate-control','PageNotValidated'],
    ['foreign-route','PageNotValidated'],['route-drift','PageNotValidated'],['login','LoginRequired'],
    ['verification','VerificationRequired'],['uid','AccountIdentityMismatch'],['source-zero','PageNotValidated'],
    ['source-many','PageNotValidated'],['incomplete','PageNotValidated'],['destination','PageNotValidated'],
    ['other-valid-source','PageNotValidated'],['mutation','PageNotValidated']]){
    const observed=await execute(value.readinessScript,[value.context,value.context.timeoutMilliseconds],value.context,scenario);
    assert.equal(observed.result.kind,kind,scenario);assert.equal(observed.state.control.clicked,0,scenario);
  }
}
async function submit(value){
  let observed=await execute(value.submitScript,[value.context,value.readinessFingerprint,value.readinessFingerprint,value.context.timeoutMilliseconds],value.context,'valid');
  assert.deepEqual(observed.result,{kind:'ClickAttempted',dispatchState:'ClickDispatched'});assert.equal(observed.state.control.clicked,1);
  for(const scenario of ['instance-patched-click','prototype-patched-click']){
    observed=await execute(value.submitScript,[value.context,value.readinessFingerprint,value.readinessFingerprint,value.context.timeoutMilliseconds],value.context,scenario);
    assert.deepEqual(observed.result,{kind:'ClickAttempted',dispatchState:'ClickDispatched'},scenario);
    assert.equal(observed.state.control.clicked,1,scenario);assert.equal(observed.state.control.patchedCalls,0,scenario);
  }
  for(const scenario of ['native-primitive-unavailable','button-type-spoof']){
    observed=await execute(value.submitScript,[value.context,value.readinessFingerprint,value.readinessFingerprint,value.context.timeoutMilliseconds],value.context,scenario);
    assert.deepEqual(observed.result,{kind:'PageNotValidated',dispatchState:'KnownNotDispatched'},scenario);
    assert.equal(observed.state.control.clicked,0,scenario);assert.equal(observed.state.control.patchedCalls,0,scenario);
  }
  observed=await execute(value.submitScript,[value.context,'9'.repeat(64),value.readinessFingerprint,value.context.timeoutMilliseconds],value.context,'valid');
  assert.deepEqual(observed.result,{kind:'ReadinessMismatch',dispatchState:'KnownNotDispatched'});assert.equal(observed.state.control.clicked,0);
  for(const scenario of ['mutation','duplicate-control','route-drift']){
    observed=await execute(value.submitScript,[value.context,value.readinessFingerprint,value.readinessFingerprint,value.context.timeoutMilliseconds],value.context,scenario);
    assert.equal(observed.result.dispatchState,'KnownNotDispatched',scenario);assert.equal(observed.state.control.clicked,0,scenario);
  }
  observed=await execute(value.submitScript,[value.context,value.readinessFingerprint,value.readinessFingerprint,value.context.timeoutMilliseconds],value.context,'other-valid-source');
  assert.deepEqual(observed.result,{kind:'PageNotValidated',dispatchState:'KnownNotDispatched'});
  assert.equal(observed.state.control.clicked,0);
  observed=await execute(value.submitScript,[value.context,value.readinessFingerprint,value.readinessFingerprint,value.context.timeoutMilliseconds],value.context,'click-throws');
  assert.deepEqual(observed.result,{kind:'ClickAttempted',dispatchState:'DispatchOutcomeUnknown'});assert.equal(observed.state.control.clicked,1);
  for(const scenario of ['click-detaches','click-changes']){
    observed=await execute(value.submitScript,[value.context,value.readinessFingerprint,value.readinessFingerprint,value.context.timeoutMilliseconds],value.context,scenario);
    assert.deepEqual(observed.result,{kind:'ClickAttempted',dispatchState:'DispatchOutcomeUnknown'});
    assert.equal(observed.state.control.clicked,1,scenario);
  }
  assert.ok(!/await|setTimeout|setInterval|requestAnimationFrame|\.submit\s*\(/.test(value.submitScript));
  assert.ok(!/control\.click|\.click\s*\(/.test(value.submitScript));
}
async function outcome(value){
  for(const [scenario,kind] of [['accepted','AcceptedBound'],['toast','UnboundAcknowledgement'],['conflict','Conflict'],
    ['rejected','ExplicitlyRejected'],['login','LoginRequired'],['verification','VerificationRequired'],
    ['wrong-official-target','PageNotValidated'],['other-valid-source','PageNotValidated'],['valid','PageNotValidated']]){
    const observed=await execute(value.outcomeScript,[value.context,value.readinessFingerprint,value.readinessFingerprint,value.context.timeoutMilliseconds],value.context,scenario);
    assert.equal(observed.result.kind,kind,scenario);assert.equal(observed.state.control.clicked,0,scenario);
    if(kind==='AcceptedBound')assert.deepEqual(observed.result,{kind,officialRequestId:'请求-甲',exactTargetId:value.context.selectedTargetExactId,
      sourceExactId:value.context.sourceExactId,destinationExactId:'destination-1'});
  }
  assert.ok(!/\.click\s*\(|location\s*=(?!=)|\.href\s*=(?!=)|\.get\s*\(/.test(value.outcomeScript));
}
async function run(value){
  if(value.mode==='readiness')await readiness(value);else if(value.mode==='submit')await submit(value);
  else if(value.mode==='outcome')await outcome(value);else throw Error('unknown mode');
  process.stdout.write('transfer submission assertions passed\n');
}
