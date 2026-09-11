'use strict';
const assert=require('node:assert/strict'),vm=require('node:vm');
let input='';process.stdin.setEncoding('utf8');process.stdin.on('data',c=>input+=c);process.stdin.on('end',()=>run(JSON.parse(input)));
function node(attrs={}){return {attrs:{...attrs},children:[],parentElement:null,isConnected:true,disabled:false,inert:false,
  style:{display:'block',visibility:'visible',pointerEvents:'auto'},tagName:'DIV',textContent:'',clicked:0,
  getAttribute(name){return Object.hasOwn(this.attrs,name)?this.attrs[name]:null;},
  setAttribute(name,value){this.attrs[name]=String(value);},
  dispatchEvent(){return true;},
  querySelectorAll(selector){return descendants(this).filter(value=>matches(value,selector));}};}
function add(parent,child){child.parentElement=parent;parent.children.push(child);return child;}
function descendants(root){const out=[];for(const child of root.children){out.push(child,...descendants(child));}return out;}
function matches(value,selector){
  if(selector==='label')return value.tagName==='LABEL';
  if(selector==='input[type="checkbox"]')return value.tagName==='INPUT'&&value.getAttribute('type')==='checkbox';
  const map={'[data-page-state]':'data-page-state','[data-managed-share-page]':'data-managed-share-page',
    '[data-managed-share-item]':'data-managed-share-item','[data-managed-share-submit]':'data-managed-share-submit',
    '[data-managed-share-official-entry]':'data-managed-share-official-entry',
    '[data-managed-share-official-result]':'data-managed-share-official-result'};
  return Object.hasOwn(map,selector)&&value.getAttribute(map[selector])!==null;
}
function fixture(context,scenario,document){
  const root=node({'data-managed-share-page':'true','data-route':'disk-main','data-uid':context.baiduUserId,
    'data-browser-id':context.browserId,'data-browser-target-id':context.targetId,
    'data-directory-id':context.targetDirectoryExactId,'data-directory-path':context.targetCanonicalPath,
    'data-list-complete':'true','data-list-stable':'true',
    'data-item-set-fingerprint':context.exactItemSetFingerprint});
  const items=context.exactItemIds.map(exactId=>add(root,node({
    'data-managed-share-item':'true','data-exact-id':exactId})));
  const item=items[0];
  const control=add(root,node({'data-managed-share-submit':'true','data-eligible':'true','type':'button'}));
  control.tagName='BUTTON';control.click=function(){control.clicked++;};
  const shareWindow=add(root,node());
  const label=add(shareWindow,node());label.tagName='LABEL';label.textContent='分享链接自动填充提取码';
  const checkboxes=[];
  const createCheckbox=()=>{const checkbox=document.createElement('input');checkbox.setAttribute('type','checkbox');
    checkbox.forceUnchecked=scenario==='checkbox-stays-unchecked';checkbox.dispatchEvent=function(event){
      state.events.push(`${event.type}:${checkbox.checked}`);
      if(scenario==='checkbox-unchecked-after-change'&&event.type==='change')checkbox._checked=false;
      return true;};checkboxes.push(checkbox);return checkbox;};
  if(scenario!=='checkbox-missing')add(label,createCheckbox());
  if(scenario==='checkbox-duplicate')add(label,createCheckbox());
  if(scenario==='checkbox-disabled')checkboxes[0].disabled=true;
  const old=add(root,node({'data-managed-share-official-entry':'true','data-share-id':'old-share',
    'data-share-url':'https://pan.baidu.com/s/old_share','data-exact-item-ids':JSON.stringify(context.exactItemIds),
    'data-permanent':'true','data-unlimited':'true','data-code-mode':'Fixed','data-effective-code':'A1b2'}));
  const official=node({'data-managed-share-official-result':'true','data-status':'accepted',
    'data-adapter-request-id':'request-1','data-share-id':'new-share','data-share-url':'https://pan.baidu.com/s/new_share',
    'data-exact-item-ids':JSON.stringify(context.exactItemIds),'data-permanent':'true','data-unlimited':'true',
    'data-code-mode':'Fixed','data-effective-code':'A1b2'});
  if(scenario==='accepted'||scenario==='wrong-item')add(root,official);
  if(scenario==='wrong-item')official.attrs['data-exact-item-ids']='["item-2"]';
  const state={roots:[root],root,item,items,control,old,states:[],checkboxes,events:[]};
  if(scenario==='duplicate-root'){state.roots.push(node({'data-managed-share-page':'true'}));return state;}
  if(scenario==='uid')root.attrs['data-uid']='other';
  if(scenario==='unstable')root.attrs['data-list-stable']='false';
  if(scenario==='wrong-item-page')item.attrs['data-exact-id']='item-2';
  if(scenario==='click-throws')control.click=function(){control.clicked++;throw Error('after click');};
  return state;
}
async function execute(script,args,context,scenario){
  const HTMLElement=function HTMLElement(){},HTMLButtonElement=function HTMLButtonElement(){};
  const HTMLInputElement=class HTMLInputElement extends HTMLElement {
    constructor(){super();Object.assign(this,node());this.tagName='INPUT';this._checked=false;}
  };
  Object.setPrototypeOf(HTMLButtonElement.prototype,HTMLElement.prototype);
  const document={createElement(tagName){
      if(String(tagName).toLowerCase()==='input')return new HTMLInputElement();
      const element=node();element.tagName=String(tagName).toUpperCase();return element;},
    querySelectorAll(selector){if(selector==='[data-page-state]')return state.states;
    if(selector==='[data-managed-share-page]')return state.roots;
    return [state.root,...descendants(state.root)].filter(value=>matches(value,selector));},defaultView:null};
  const state=fixture(context,scenario,document),location={href:scenario==='foreign-route'?'https://foreign.test/':
    'https://pan.baidu.com/disk/main#/index?category=all'};
  const forbidden=()=>{throw Error('forbidden side effect');};
  const Event=function Event(type,options={}){this.type=type;this.bubbles=options.bubbles===true;};
  const sandbox={document,location,TextEncoder,Event,getComputedStyle:value=>value.style,
    setTimeout:forbidden,setInterval:forbidden,fetch:forbidden,XMLHttpRequest:forbidden,WebSocket:forbidden,
    navigator:{sendBeacon:forbidden}};
  const vmContext=vm.createContext(sandbox);vmContext.window=vmContext;vmContext.top=vmContext;
  Object.defineProperty(HTMLElement.prototype,'click',{configurable:false,writable:false,value:function(){
    state.events.push(`click:${state.checkboxes[0]&&state.checkboxes[0].checked}`);state.control.clicked++;}});
  Object.defineProperty(HTMLInputElement.prototype,'checked',{configurable:false,
    get:function(){return this._checked===true;},set:function(value){if(!this.forceUnchecked)this._checked=Boolean(value);}});
  Object.setPrototypeOf(state.control,HTMLButtonElement.prototype);
  assert.ok(state.checkboxes.every(checkbox=>checkbox instanceof HTMLInputElement&&
    checkbox.constructor===HTMLInputElement&&checkbox.getAttribute('type')==='checkbox'),
    'checkboxes must be HTMLInputElement instances when created by the synthetic DOM');
  vmContext.HTMLElement=HTMLElement;vmContext.HTMLButtonElement=HTMLButtonElement;vmContext.HTMLInputElement=HTMLInputElement;
  vm.runInContext('document.defaultView=window;',vmContext);
  const value=vm.runInContext(`(function(){${script}\n}).apply(undefined,${JSON.stringify(args)})`,vmContext,{timeout:3000});
  return {result:JSON.parse(JSON.stringify(await value)),state};
}
async function listing(value){
  for(const [scenario,kind] of [['valid','Observed'],['duplicate-root','PageNotValidated'],['foreign-route','PageNotValidated'],
    ['uid','AccountIdentityMismatch'],['unstable','PageNotValidated']]){
    const observed=await execute(value.listingScript,[value.context,value.context.timeoutMilliseconds],value.context,scenario);
    assert.equal(observed.result.kind,kind,scenario);assert.equal(observed.state.control.clicked,0,scenario);
  }
}
async function submit(value){
  let observed=await execute(value.submitScript,[value.context,value.baselineShareIdsFingerprint,
    value.baselineObservationFingerprint,value.baselineShareIdsFingerprint,value.baselineObservationFingerprint,
    value.context.timeoutMilliseconds],value.context,'valid');
  assert.deepEqual(observed.result,{kind:'ClickAttempted',dispatchState:'ClickDispatched'});
  assert.equal(observed.state.control.clicked,1);
  assert.equal(observed.state.checkboxes[0].checked,true);
  assert.deepEqual(observed.state.events,['input:true','change:true','click:true']);
  assert.ok(observed.state.items.every(item=>item.getAttribute('data-selected')==='true'));
  for(const scenario of ['checkbox-missing','checkbox-duplicate','checkbox-disabled','checkbox-stays-unchecked',
    'checkbox-unchecked-after-change']){
    observed=await execute(value.submitScript,[value.context,value.baselineShareIdsFingerprint,
      value.baselineObservationFingerprint,value.baselineShareIdsFingerprint,value.baselineObservationFingerprint,
      value.context.timeoutMilliseconds],value.context,scenario);
    assert.equal(observed.result.dispatchState,'KnownNotDispatched',scenario);
    assert.equal(observed.state.control.clicked,0,scenario);
  }
  observed=await execute(value.submitScript,[value.context,'9'.repeat(64),value.baselineObservationFingerprint,
    value.baselineShareIdsFingerprint,value.baselineObservationFingerprint,value.context.timeoutMilliseconds],value.context,'valid');
  assert.deepEqual(observed.result,{kind:'BaselineMismatch',dispatchState:'KnownNotDispatched'});
  assert.equal(observed.state.control.clicked,0);
  observed=await execute(value.submitScript,[value.context,value.baselineShareIdsFingerprint,
    value.baselineObservationFingerprint,value.baselineShareIdsFingerprint,value.baselineObservationFingerprint,
    value.context.timeoutMilliseconds],value.context,'wrong-item-page');
  assert.equal(observed.result.dispatchState,'KnownNotDispatched');assert.equal(observed.state.control.clicked,0);
  assert.ok(!/await|setTimeout|setInterval|requestAnimationFrame|\.submit\s*\(/.test(value.submitScript));
  assert.ok(!/control\.click|\.click\s*\(/.test(value.submitScript));
  assert.match(value.submitScript,/分享链接自动填充提取码/);
  assert.match(value.submitScript,/input\[type=["']checkbox["']\]/);
  assert.ok(!/arguments\[[6-9]\]/.test(value.submitScript));
}
async function outcome(value){
  for(const [scenario,kind] of [['accepted','AcceptedBound'],['wrong-item','PageNotValidated'],['valid','PageNotValidated'],
    ['foreign-route','PageNotValidated']]){
    const observed=await execute(value.outcomeScript,[value.context,value.baselineShareIdsFingerprint,
      value.baselineObservationFingerprint,value.context.timeoutMilliseconds],value.context,scenario);
    assert.equal(observed.result.kind,kind,scenario);assert.equal(observed.state.control.clicked,0,scenario);
  }
  assert.ok(!/\.click\s*\(|location\s*=(?!=)|\.href\s*=(?!=)|\.get\s*\(/.test(value.outcomeScript));
}
async function run(value){
  if(value.mode==='listing')await listing(value);else if(value.mode==='submit')await submit(value);
  else if(value.mode==='outcome')await outcome(value);else throw Error('unknown mode');
  process.stdout.write('share submission assertions passed\n');
}
