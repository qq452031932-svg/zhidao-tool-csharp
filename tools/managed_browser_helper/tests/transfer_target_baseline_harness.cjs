'use strict';
const assert = require('node:assert/strict'), vm = require('node:vm'), fs = require('node:fs');
const input = JSON.parse(fs.readFileSync(0, 'utf8'));
const PAN_MAIN = 'https://pan.baidu.com/disk/main#/index?category=all';
let assertions = 0;
function forbidden(){ throw Error('forbidden capability'); }
function element(attrs={}) { return {attrs,nodeType:1,isConnected:true,hidden:false,children:[],parentElement:null,
  style:{display:'block',visibility:'visible',opacity:'1'},clicked:0,
  getAttribute(name){return Object.hasOwn(this.attrs,name)?this.attrs[name]:null;},
  getClientRects(){return [{width:1,height:1}];}, click(){this.clicked++;},
  querySelectorAll(selector){return descendants(this).filter(node=>matches(node,selector));}}; }
function add(parent,child){parent.children.push(child);child.parentElement=parent;return child;}
function descendants(node){return node.children.flatMap(child=>[child,...descendants(child)]);}
function detach(node){node.isConnected=false;for(const child of descendants(node))child.isConnected=false;}
function matches(node,selector){const match=/^\[([a-z-]+)\]$/.exec(selector);if(!match)throw Error('unexpected selector');return Object.hasOwn(node.attrs,match[1]);}
function fixture(options={}) {
  const body=element();
  if(options.source){const root=add(body,element({'data-managed-source-root':'true','data-uid':options.uid??'uid-1',
    'data-generation':'source-generation','data-total-count':'1','data-end':'true'}));
    add(root,element({'data-source-item-id':'source-1','data-kind':'File','data-name':'source.txt'}));
    if(options.pageState)add(body,element({'data-page-state':options.pageState}));return {body,root};}
  const root=add(body,element({'data-managed-target-root':'true','data-route':options.route??'root',
    'data-uid':options.uid??'uid-1','data-directory-id':'root-1','data-path':'/','data-ancestor-ids':'["root-1"]',
    'data-generation':'generation-1','data-total-count':'2','data-end':'true','data-steps':'1','data-page-index':'0',
    'data-observation-fingerprint':options.fingerprint??input.parentFingerprint??'missing'}));
  if(options.pageState)add(body,element({'data-page-state':options.pageState}));
  add(root,element({'data-target-item-id':'child-1','data-kind':'Folder','data-name':'child'}));
  add(root,element({'data-target-item-id':'file-2','data-kind':'File','data-name':'two.txt'}));
  add(root,element({'data-managed-target-reset':'true'}));
  return {body,root};
}
async function execute(script,options={}){
  const f=fixture(options);if(options.prepare)options.prepare(f);
  const location={href:options.href??PAN_MAIN};
  const document={querySelectorAll(selector){return descendants(f.body).filter(node=>matches(node,selector));}};
  const context=vm.createContext({document,location,TextEncoder,getComputedStyle:n=>n.style,
    fetch:forbidden,XMLHttpRequest:forbidden,WebSocket:forbidden,localStorage:{getItem:forbidden},
    sessionStorage:{getItem:forbidden},navigator:{sendBeacon:forbidden}});
  context.window=context;context.top=context;vm.runInContext('document.defaultView=window;',context);
  const args=options.args??['uid-1',1000,'44444444-4444-4444-4444-444444444444','target-1','http://127.0.0.1:43127/'];
  const argumentSource=options.argumentSource??JSON.stringify(args);
  const pending=vm.runInContext(`(function(){${script}\n}).apply(undefined,${argumentSource})`,context,{timeout:1000});
  if(options.mutate)options.mutate(f,context);
  return {result:JSON.parse(JSON.stringify(await pending)),fixture:f};
}
function pagedState(scenario,mutation){
  const item=(exactId,serverName=exactId,kind='File')=>({exactId,kind,serverName});
  let pages,total;
  if(scenario==='stable'||scenario==='wrong-count'||scenario==='wrong-end'||scenario==='enter-mutation'||scenario==='click-error'||
    scenario==='second-name'||scenario==='second-kind'||scenario==='second-duplicate'||scenario==='third-marker'||
    scenario==='marker-missing'||scenario==='third-child'){
    pages=[[item('a','a','Folder'),item('b')],[item('c'),item('d','d','Folder')],[item('e','e','Folder')]];
    total=scenario==='wrong-count'?6:5;
  }else if(scenario==='enter-duplicate'){
    pages=[[item('a','a','Folder'),item('b'),item('d','d','Folder')],[item('c'),item('d','d','Folder')],[item('e','e','Folder')]];total=5;
  }else if(scenario==='enter-conflict'){
    pages=[[item('a','a','Folder'),item('b'),item('d','wrong','File')],[item('c'),item('d','d','Folder')],[item('e','e','Folder')]];total=5;
  }else if(scenario==='enter-not-found'){
    pages=[[item('a','a','Folder'),item('b')],[item('c')],[item('e','e','Folder')]];total=5;
  }else if(scenario==='duplicate-same'){pages=[[item('a'),item('b')],[item('b'),item('c')]];total=3;
  }else if(scenario==='no-progress'){pages=[[item('a')],[],[item('b')]];total=2;
  }else if(scenario==='conflict'){pages=[[item('same','a')],[item('same','b'),item('other')]];total=2;
  }else if(scenario==='over-64'){pages=Array.from({length:65},(_,i)=>[item('p'+i)]);total=65;
  }
  else if(scenario==='over-1024'){const values=Array.from({length:1025},(_,i)=>item('i'+String(i).padStart(4,'0')));
    pages=Array.from({length:Math.ceil(values.length/64)},(_,i)=>values.slice(i*64,(i+1)*64));total=1024;}
  else throw Error('unknown paged scenario');
  const state={pages,total,current:pages.length-1,resets:0,nexts:0,clicks:0,clickNodes:[],roots:[],rows:[],mutation,scenario};
  state.makeRoot=function(){const index=state.current;
    const generation=mutation==='generation'||(scenario==='enter-mutation'&&state.resets>=2)?'changed':'generation-1';
    const root=element({'data-managed-target-root':'true','data-route':'root','data-uid':'uid-1',
      'data-directory-id':'root-1','data-path':'/','data-ancestor-ids':'["root-1"]','data-generation':generation,
      'data-total-count':String(total),'data-end':String(index===pages.length-1&&scenario!=='wrong-end'),
      'data-page-index':String(index),'data-observation-fingerprint':
        scenario==='third-marker'&&state.resets>=3?'f'.repeat(64):
        scenario==='marker-missing'&&state.resets>=3?null:(input.pagedParentFingerprint??'missing')});
    for(const entry of pages[index]){const secondMutation=state.resets>=2&&entry.exactId==='e';
      const row=add(root,element({'data-target-item-id':entry.exactId,
        'data-kind':scenario==='second-kind'&&secondMutation?'File':
          scenario==='third-child'&&state.resets>=3&&entry.exactId==='d'?'File':entry.kind,
        'data-name':(mutation==='item'&&entry.exactId==='e')||(scenario==='second-name'&&secondMutation)?'changed':entry.serverName}));
      state.rows.push(row);row.click=()=>{row.clicked++;state.clicks++;state.clickNodes.push(row);
        if(scenario==='click-error')throw Error('after click');};}
    if(scenario==='second-duplicate'&&state.resets>=2&&index===pages.length-1)
      add(root,element({'data-target-item-id':'b','data-kind':'File','data-name':'b'}));
    const reset=add(root,element({'data-managed-target-reset':'true'}));reset.click=()=>{reset.clicked++;state.resets++;
      detach(state.root);state.current=0;state.root=state.makeRoot();};
    if(index<pages.length-1){const next=add(root,element({'data-managed-target-next':'true'}));
      next.click=()=>{next.clicked++;state.nexts++;detach(state.root);state.current++;state.root=state.makeRoot();};}
    state.roots.push(root);return root;};
  state.root=state.makeRoot();return state;
}
async function executePaged(script,scenario,mutation){
  const state=pagedState(scenario,mutation),location={href:PAN_MAIN};
  const nodes=()=>[state.root,...descendants(state.root)];
  const document={querySelectorAll(selector){return nodes().filter(node=>matches(node,selector));}};
  const context=vm.createContext({document,location,TextEncoder,getComputedStyle:n=>n.style,
    fetch:forbidden,XMLHttpRequest:forbidden,WebSocket:forbidden,localStorage:{getItem:forbidden},
    sessionStorage:{getItem:forbidden},navigator:{sendBeacon:forbidden}});
  context.window=context;context.top=context;vm.runInContext('document.defaultView=window;',context);
  const args=['uid-1',1000,'44444444-4444-4444-4444-444444444444','target-1','http://127.0.0.1:43127/'];
  const pending=vm.runInContext(`(function(){${script}\n}).apply(undefined,${JSON.stringify(args)})`,context,{timeout:3000});
  return {result:JSON.parse(JSON.stringify(await pending)),state};
}
async function executePagedEnter(script,scenario,expectedParent,childId){
  const state=pagedState(scenario),location={href:PAN_MAIN};
  const nodes=()=>[state.root,...descendants(state.root)];
  const document={querySelectorAll(selector){return nodes().filter(node=>matches(node,selector));}};
  const context=vm.createContext({document,location,TextEncoder,getComputedStyle:n=>n.style,
    fetch:forbidden,XMLHttpRequest:forbidden,WebSocket:forbidden,localStorage:{getItem:forbidden},
    sessionStorage:{getItem:forbidden},navigator:{sendBeacon:forbidden}});
  context.window=context;context.top=context;vm.runInContext('document.defaultView=window;',context);
  const child=expectedParent.items.find(item=>item.exactId===childId), childName=child?.serverName??childId;
  const args=['uid-1',1000,'44444444-4444-4444-4444-444444444444','target-1','http://127.0.0.1:43127/',
    expectedParent,childId,childName,'/'+childName,input.pagedParentFingerprint];
  const pending=vm.runInContext(`(function(){${script}\n}).apply(undefined,${JSON.stringify(args)})`,context,{timeout:3000});
  return {result:JSON.parse(JSON.stringify(await pending)),state};
}
async function root(){
  const script=input.rootScript;
  assert.deepEqual((await execute(script)).result,{kind:'RootOpened'});assertions++;
  for(const options of [{href:'https://foreign.test/'},{route:'directory'},{pageState:'Unknown'}]){
    assert.deepEqual((await execute(script,options)).result,{kind:'PageNotValidated'});assertions++;
  }
  assert.deepEqual((await execute(script,{pageState:'LoginRequired'})).result,{kind:'LoginRequired'});assertions++;
  assert.deepEqual((await execute(script,{pageState:'VerificationRequired'})).result,{kind:'VerificationRequired'});assertions++;
  assert.deepEqual((await execute(script,{uid:'other'})).result,{kind:'AccountIdentityMismatch'});assertions++;
}
async function parent(){
  const script=input.parentScript;
  const valid=(await execute(script)).result;
  assert.deepEqual(valid,{kind:'Observed',scope:'AllItems',browserId:'44444444-4444-4444-4444-444444444444',
    targetId:'target-1',devToolsEndpoint:'http://127.0.0.1:43127/',baiduUserId:'uid-1',generationToken:'generation-1',
    directoryExactId:'root-1',canonicalPath:'/',ancestorExactIds:['root-1'],totalCount:2,reachedEnd:true,isStable:true,
    enumerationStepCount:1,items:[{exactId:'child-1',kind:'Folder',serverName:'child'},{exactId:'file-2',kind:'File',serverName:'two.txt'}]});assertions++;
  const base=['uid-1',1000,'44444444-4444-4444-4444-444444444444','target-1','http://127.0.0.1:43127/'];
  assert.deepEqual((await execute(script,{args:[...base,null,null,null]})).result,valid);assertions++;
  const baseSource=JSON.stringify(base).slice(0,-1);
  for(const suffix of [',null,undefined,undefined]',',null,null,undefined]',',null,undefined,null]',
    ',undefined,null,null]',',undefined,undefined,null]',',undefined,null,undefined]']){
    assert.deepEqual((await execute(script,{argumentSource:baseSource+suffix})).result,{kind:'PageNotValidated'});assertions++;}
  const invalid=[f=>{f.root.attrs['data-total-count']='3';},f=>{f.root.attrs['data-end']='false';},
    f=>{f.root.attrs['data-steps']='65';},f=>{f.root.children[1].attrs['data-target-item-id']='child-1';},
    f=>{f.root.attrs['data-generation']='';},f=>{f.root.attrs['data-ancestor-ids']='[]';},
    f=>{f.root.attrs['data-route']='directory';f.root.attrs['data-directory-id']='nested-1';
      f.root.attrs['data-path']='/nested';f.root.attrs['data-ancestor-ids']='["root-1","nested-1"]';},
    f=>{for(let i=0;i<1023;i++)add(f.root,element({'data-target-item-id':'x'+i,'data-kind':'File','data-name':'x'}));}];
  for(const prepare of invalid){
    const result=await execute(script,{prepare});
    assert.deepEqual(result.result,{kind:'PageNotValidated'});assertions++;}
  for(const pageState of ['LoginRequired','VerificationRequired']){
    assert.deepEqual((await execute(script,{pageState})).result,{kind:pageState});assertions++;}
}
async function enter(){
  const parent=(await execute(input.parentScript)).result;
  const args=['uid-1',1000,'44444444-4444-4444-4444-444444444444','target-1','http://127.0.0.1:43127/',
    parent,'child-1','child','/child',input.parentFingerprint];
  const success=await execute(input.enterScript,{args});
  assert.deepEqual(success.result,{kind:'DirectoryEntered',navigationAttempted:true});
  assert.equal(success.fixture.root.children[0].clicked,1);assertions++;
  for(const variant of [
    {args:[...args.slice(0,6),'wrong','child','/child']},
    {args:[...args.slice(0,6),'child-1','other','/other']},
    {args,prepare:f=>{f.root.attrs['data-generation']='changed';}},
    {args,prepare:f=>{f.root.children[0].attrs['data-name']='changed';}},
    {args,prepare:f=>{f.root.children[1].attrs['data-target-item-id']='child-1';}},
  ]){const value=await execute(input.enterScript,variant);
    assert.deepEqual(value.result,{kind:'PageNotValidated',navigationAttempted:false});
    assert.equal(value.fixture.root.children[0].clicked,0);assertions++;}
  for(const pageState of ['LoginRequired','VerificationRequired']){
    const value=await execute(input.enterScript,{args,pageState});
    assert.deepEqual(value.result,{kind:pageState,navigationAttempted:false});
    assert.equal(value.fixture.root.children[0].clicked,0);assertions++;}
  const uncertain=await execute(input.enterScript,{args,prepare:f=>{f.root.children[0].click=function(){this.clicked++;throw Error('after click');};}});
  assert.deepEqual(uncertain.result,{kind:'OutcomeUnknown',navigationAttempted:true});
  assert.equal(uncertain.fixture.root.children[0].clicked,1);assertions++;
}
async function postRead(){
  const prepare=f=>{f.root.attrs['data-route']='directory';f.root.attrs['data-directory-id']='child-1';
    f.root.attrs['data-path']='/child';f.root.attrs['data-ancestor-ids']='["root-1","child-1"]';
    f.root.attrs['data-generation']='generation-2';f.root.children[0].attrs['data-target-item-id']='grandchild-1';};
  const args=['uid-1',1000,'44444444-4444-4444-4444-444444444444','target-1','http://127.0.0.1:43127/','child-1','/child',['root-1','child-1']];
  const value=(await execute(input.postReadScript,{prepare,args})).result;
  assert.equal(value.kind,'Observed');assert.equal(value.directoryExactId,'child-1');assert.equal(value.canonicalPath,'/child');
  assert.deepEqual(value.ancestorExactIds,['root-1','child-1']);assert.equal(value.generationToken,'generation-2');assertions++;
  for(const change of [f=>{prepare(f);f.root.attrs['data-route']='root';},f=>{prepare(f);f.root.attrs['data-path']='/wrong';},
    f=>{prepare(f);f.root.attrs['data-ancestor-ids']='["root-1","wrong"]';}]){
    assert.deepEqual((await execute(input.postReadScript,{prepare:change,args})).result,{kind:'PageNotValidated'});assertions++;}
}
async function probePasses(){
  const first=(await execute(input.passScript)).result;
  const mutations={generation:f=>{f.root.attrs['data-generation']='changed';},total:f=>{f.root.attrs['data-total-count']='1';},
    item:f=>{f.root.children[1].attrs['data-name']='changed';},path:f=>{f.root.attrs['data-path']='/changed';f.root.attrs['data-directory-id']='changed';f.root.attrs['data-ancestor-ids']='["root-1","changed"]';},
    ancestry:f=>{f.root.attrs['data-ancestor-ids']='["other"]';},page:f=>{f.root.attrs['data-page-index']='1';},
    malformed:f=>{f.root.attrs['data-end']='false';}};
  const second=(await execute(input.passScript,{prepare:mutations[input.mutation]})).result;
  process.stdout.write(JSON.stringify({first,second}));
}
async function restore(){
  const sourceUri='https://pan.baidu.com/s/source_1?pwd=A1b2';
  const expected={kind:'Observed',baiduUserId:'uid-1',generationToken:'source-generation',totalCount:1,reachedEnd:true,isStable:true,
    items:[{exactId:'source-1',kind:'File',serverName:'source.txt'}],observationFingerprint:'4cdb5ac7a450d6125ebaef9287cb8431f3976b5374ebc5de96c2570786995956'};
  const args=['uid-1',1000,sourceUri,expected];
  assert.deepEqual((await execute(input.restoreScript,{source:true,href:sourceUri,args})).result,{kind:'SourceRestored'});assertions++;
  assert.deepEqual((await execute(input.restoreScript,{source:true,href:'https://foreign.test/',args})).result,{kind:'PageNotValidated'});assertions++;
  assert.deepEqual((await execute(input.restoreScript,{source:true,href:sourceUri,args,uid:'other'})).result,{kind:'AccountIdentityMismatch'});assertions++;
  assert.deepEqual((await execute(input.restoreScript,{source:true,href:sourceUri,args,pageState:'LoginRequired'})).result,{kind:'LoginRequired'});assertions++;
  assert.deepEqual((await execute(input.restoreScript,{source:true,href:sourceUri,args,pageState:'VerificationRequired'})).result,{kind:'VerificationRequired'});assertions++;
  for(const prepare of [f=>{f.root.attrs['data-generation']='changed';},f=>{f.root.children[0].attrs['data-name']='changed';},f=>{f.root.attrs['data-end']='false';}]){
    assert.deepEqual((await execute(input.restoreScript,{source:true,href:sourceUri,args,prepare})).result,{kind:'PageNotValidated'});assertions++;}
}
async function paged(){
  const stable=await executePaged(input.passScript,'stable');
  assert.equal(stable.result.kind,'Observed');assert.equal(stable.result.totalCount,5);
  assert.equal(stable.result.enumerationStepCount,3);assert.deepEqual(stable.result.items.map(x=>x.exactId),['a','b','c','d','e']);
  assert.equal(stable.state.resets,1);assert.equal(stable.state.nexts,2);assert.ok(new Set(stable.state.roots).size>=4);assertions++;
  for(const scenario of ['duplicate-same','no-progress','over-64','over-1024','conflict','wrong-count','wrong-end']){
    assert.deepEqual((await executePaged(input.passScript,scenario)).result,{kind:'PageNotValidated'});assertions++;}
  const first=(await executePaged(input.passScript,'stable')).result;
  for(const mutation of ['generation','item']){
    const second=(await executePaged(input.passScript,'stable',mutation)).result;
    assert.notDeepEqual(second,first);assertions++;}
}
async function pagedEnter(){
  const parent=(await executePaged(input.parentScript,'stable')).result;
  assert.equal(parent.kind,'Observed');
  for(const childId of ['a','d','e']){
    const value=await executePagedEnter(input.enterScript,'stable',parent,childId);
    assert.deepEqual(value.result,{kind:'DirectoryEntered',navigationAttempted:true});
    assert.equal(value.state.clicks,1);assert.equal(value.state.resets,3);
    assert.equal(value.state.clickNodes.length,1);assert.equal(value.state.clickNodes[0].isConnected,true);
    assert.ok(value.state.rows.filter(row=>row.getAttribute('data-target-item-id')===childId&&row!==value.state.clickNodes[0])
      .every(row=>row.isConnected===false));assertions++;}
  for(const scenario of ['enter-duplicate','enter-conflict','enter-mutation','enter-not-found']){
    const value=await executePagedEnter(input.enterScript,scenario,parent,'d');
    assert.deepEqual(value.result,{kind:'PageNotValidated',navigationAttempted:false});
    assert.equal(value.state.clicks,0);assertions++;}
  for(const scenario of ['second-name','second-kind','second-duplicate']){
    const value=await executePagedEnter(input.enterScript,scenario,parent,'d');
    assert.deepEqual(value.result,{kind:'PageNotValidated',navigationAttempted:false});
    assert.equal(value.state.clicks,0);assertions++;}
  for(const scenario of ['third-marker','marker-missing','third-child']){
    const value=await executePagedEnter(input.enterScript,scenario,parent,'d');
    assert.deepEqual(value.result,{kind:'PageNotValidated',navigationAttempted:false});
    assert.equal(value.state.clicks,0);assertions++;}
  const uncertain=await executePagedEnter(input.enterScript,'click-error',parent,'d');
  assert.deepEqual(uncertain.result,{kind:'OutcomeUnknown',navigationAttempted:true});
  assert.equal(uncertain.state.clicks,1);assertions++;
}
(async()=>{if(input.mode==='root')await root();else if(input.mode==='parent')await parent();else if(input.mode==='enter')await enter();else if(input.mode==='post-read')await postRead();else if(input.mode==='paged')await paged();else if(input.mode==='paged-enter')await pagedEnter();else if(input.mode==='probe-passes'){await probePasses();return;}else if(input.mode==='restore')await restore();else throw Error('unknown harness mode');
  process.stdout.write(`${assertions} target baseline assertions passed\n`);
})().catch(error=>{process.stderr.write(String(error.stack));process.exitCode=1;});
