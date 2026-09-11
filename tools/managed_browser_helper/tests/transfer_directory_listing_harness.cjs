'use strict';
// Actual fixed script, synthetic DOM only: forbidden capabilities throw, no browser/network.
const assert = require('node:assert/strict'), vm = require('node:vm'), fs = require('node:fs');
const {script, probe} = JSON.parse(fs.readFileSync(0, 'utf8'));
const SOURCE = 'https://pan.baidu.com/s/fixture_1?pwd=1234', UID = 'synthetic-private-uid';
let assertions = 0;
function forbidden() { throw Error('forbidden capability'); }
function element(attrs = {}, text = '') {
  return {attrs, textContent:text, children:[], nodeType:1, parentElement:null, isConnected:true, hidden:false,
    style:{display:'block', visibility:'visible', opacity:'1'},
    getAttribute(name) { return Object.hasOwn(this.attrs,name) ? this.attrs[name] : null; },
    getClientRects() { return this.noRects ? [] : [{width:50,height:20}]; },
    contains(other) { for(let p=other;p;p=p.parentElement) if(p===this) return true; return false; },
    querySelectorAll(selector) { return descendants(this).filter(n=>matches(n,selector)); },
    click:forbidden,setAttribute:forbidden,appendChild:forbidden,dispatchEvent:forbidden,scrollIntoView:forbidden,
    get innerHTML(){forbidden();},get value(){forbidden();}};
}
function descendants(node) { return node.children.flatMap(child=>[child,...descendants(child)]); }
function matches(node,selector) {
  return selector.split(',').some(part=>{
    const match=/^\[([\w-]+)(?:="([^"]*)")?\]$/.exec(part.trim());
    if(!match) throw Error('unexpected selector');
    return Object.hasOwn(node.attrs,match[1]) && (match[2]===undefined || node.attrs[match[1]]===match[2]);
  });
}
function add(parent,child){parent.children.push(child);child.parentElement=parent;return child;}
function fixture(){
  const body=element(),root=add(body,element({role:'dialog','aria-label':'保存到 选择保存位置 新建文件夹',
    'data-generation':'directory-gen-1','data-parent-id':'parent-1','data-path':'/目标目录',
    'data-ancestor-ids':'["root-1","parent-1"]'}));
  const uid=add(body,element({'data-uid':UID})),total=add(root,element({'data-total-count':'2'})),end=add(root,element({'data-list-end':'true'}));
  const rows=[add(root,element({'data-fsid':'202','data-kind':'folder','data-name':' spaced name '})),
    add(root,element({'data-file-id':'101','data-kind':'folder',title:'测试文件夹'}))];
  return {body,root,uid,total,end,rows};
}
async function execute(options={}){
  const f=fixture();if(options.prepare)options.prepare(f);
  let clock=0,reads=0;
  const doc={querySelectorAll(selector){reads++;if(options.expire)clock=1000;return options.overflow?Array(257).fill(f.root):descendants(f.body).filter(n=>matches(n,selector));}};
  const context=vm.createContext({document:doc,location:{href:options.href??SOURCE},Date:{now:()=>clock},TextEncoder,
    getComputedStyle:n=>n.style,fetch:forbidden,XMLHttpRequest:forbidden,WebSocket:forbidden,setTimeout:forbidden,
    localStorage:{getItem:forbidden},sessionStorage:{getItem:forbidden},navigator:{sendBeacon:forbidden}});
  context.window=context;context.top=options.frame?{}:context;vm.runInContext('document.defaultView=window;',context);
  const source=Object.hasOwn(options,'expectedSourceUrl')?options.expectedSourceUrl:SOURCE;
  const pending=vm.runInContext(`(function(){${script}\n}).call(undefined,${JSON.stringify(options.uid??UID)},${options.budget??100},${JSON.stringify(source)})`,context,{timeout:1000});
  if(options.mutate)options.mutate(f,context);
  return {result:JSON.parse(JSON.stringify(await pending)),reads};
}
async function check(options,kind='PageNotValidated',verify){
  const {result,reads}=await execute(options);
  if(kind==='Observed')assert.equal(result.kind,kind);else assert.deepEqual(result,{kind});
  if(verify)verify(result,reads);assertions++;
}
(async()=>{
  if (probe) { process.stdout.write(JSON.stringify((await execute(probe)).result)); return; }
  await check({},'Observed',r=>assert.deepEqual(r,{kind:'Observed',baiduUserId:UID,generationToken:'directory-gen-1',parentExactId:'parent-1',canonicalPath:'/目标目录',ancestorExactIds:['root-1','parent-1'],totalCount:2,reachedEnd:true,isStable:true,items:[{exactId:'101',kind:'Folder',serverName:'测试文件夹'},{exactId:'202',kind:'Folder',serverName:' spaced name '}]}));
  for(const count of [0,1,32,33])await check({prepare:f=>{
    f.root.children=[f.total,f.end];f.total.attrs['data-total-count']=String(count);
    for(let i=0;i<count;i++)add(f.root,element({'data-fsid':String(i),'data-kind':'folder','data-name':'😀'.repeat(64)}));
  }},count<=32?'Observed':'PageNotValidated');
  await check({prepare:f=>{f.root.attrs['data-path']='/';f.root.attrs['data-ancestor-ids']='["parent-1"]';}},'Observed');
  await check({prepare:f=>{f.rows[0].attrs['data-name']=' ';}},'Observed');
  for(const overflow of [false,true])await check({prepare:f=>{
    const segments=Array(15).fill('a'.repeat(256)).concat('b'.repeat(240+(overflow?1:0)));
    f.root.attrs['data-path']='/'+segments.join('/');
    f.root.attrs['data-ancestor-ids']=JSON.stringify(Array.from({length:16},(_,i)=>'a'+i).concat('parent-1'));
  }},overflow?'PageNotValidated':'Observed');
  await check({prepare:f=>{f.root.attrs['data-path']='/'+Array(32).fill('a').join('/');f.root.attrs['data-ancestor-ids']=JSON.stringify(Array.from({length:32},(_,i)=>'a'+i).concat('parent-1'));}},'Observed');
  for(const path of ['a','/a/','//a','/a/../b','/a/./b','/a\\b',' /a','/a ','/a\n','/\ud800','/'+ '中'.repeat(86),'/' + Array(33).fill('a').join('/')])await check({prepare:f=>{f.root.attrs['data-path']=path;}});
  for(const ancestors of ['[]','["parent-1"]','["parent-1","parent-1"]','["root-1","wrong"]','["bad/id","parent-1"]','{}','not-json'])await check({prepare:f=>{f.root.attrs['data-ancestor-ids']=ancestors;}});
  for(const prepare of [
    f=>{delete f.root.attrs['data-parent-id'];},f=>{delete f.root.attrs['data-path'];},f=>{delete f.root.attrs['data-ancestor-ids'];},
    f=>{delete f.root.attrs['data-generation'];},f=>{f.root.attrs['data-list-version']='conflict';},
    f=>{f.root.attrs['data-parent-fsid']='conflict';},f=>{f.root.attrs['data-canonical-path']='/other';},
    f=>{f.root.attrs['data-generation']='\ud800';},f=>{f.root.attrs['data-generation']='😀'.repeat(65);},
    f=>{f.root.attrs['aria-label']='下载';},f=>{f.root.attrs.role='main';},
    f=>{f.total.attrs['data-total-count']='02';},f=>{f.total.attrs['data-total-count']='2.0';},f=>{f.total.attrs['data-total-count']='3';},
    f=>{f.end.attrs['data-list-end']='false';},f=>{f.root.children=f.root.children.filter(n=>n!==f.end);},
    f=>{f.root.children=f.root.children.filter(n=>n!==f.total);},f=>{add(f.root,element({'data-total-count':'2'}));},
    f=>{add(f.root,element({'data-list-end':'true'}));},f=>{f.rows[0].attrs['data-kind']='file';},
    f=>{f.rows[0].attrs['data-fsid']='root-1';},f=>{f.rows[0].attrs['data-fsid']='parent-1';},
    f=>{f.rows[0].attrs['data-file-id']='conflict';},f=>{f.rows[0].attrs.title='conflict';},
    f=>{f.rows[0].attrs['data-fsid']='101';},f=>{f.rows[0].attrs['data-fsid']='a'.repeat(129);},
    f=>{f.rows[0].attrs['data-name']='';},f=>{f.rows[0].attrs['data-name']='\ud800';},
    f=>{f.rows[0].attrs['data-name']='😀'.repeat(65);},f=>{f.rows[0].attrs['data-name']='x\n';},
    f=>{add(f.root,element({role:'dialog'}));},f=>{add(f.body,element({role:'dialog','aria-label':'保存到'}));},
    f=>{add(f.body,element({role:'dialog','aria-label':'请完成安全验证'}));},
    f=>{add(f.body,element({role:'dialog','aria-label':'登录百度网盘'}));},
    f=>{add(f.body,element({role:'dialog'}));},
    f=>{const n=add(f.body,element({role:'dialog','aria-label':'保存到'}));n.hidden=true;},
    f=>{f.body.children=f.body.children.filter(n=>n!==f.uid);},f=>{f.uid.attrs['data-uid']='';},
    f=>{f.uid.attrs['data-uid']='\ud800';},
  ])await check({prepare});
  for(const target of ['root','uid','total','end','row'])for(const change of [n=>{n.hidden=true;},n=>{n.style.display='none';},n=>{n.style.visibility='hidden';},n=>{n.style.opacity='0';},n=>{n.noRects=true;},n=>{n.isConnected=false;},n=>{n.attrs['aria-hidden']='true';}])await check({prepare:f=>change(target==='row'?f.rows[0]:f[target])});
  for(const prepare of [f=>{f.uid.attrs['data-uid']='other';},f=>{f.uid.attrs['data-uk']='other';},f=>{add(f.body,element({'data-uid':'other'}));}])await check({prepare},'AccountIdentityMismatch');
  for(const mutate of [f=>{f.root.attrs['data-generation']='other';},f=>{f.rows[0].attrs['data-name']='other';},
    f=>{add(f.body,element({role:'dialog','aria-label':'请完成安全验证'}));},
    f=>{add(f.body,element({role:'dialog','aria-label':'登录百度网盘'}));},
    f=>{add(f.body,element({role:'dialog'}));},
    f=>{f.root.attrs['data-path']='/other';},f=>{f.root.attrs['data-ancestor-ids']='["other-root","parent-1"]';},
    f=>{f.rows[0].hidden=true;},f=>{f.end.attrs['data-list-end']='false';},
    (f,c)=>{c.location.href=SOURCE.split('?')[0];},(f,c)=>{c.document={};},
    f=>{const copy=element({...f.rows[0].attrs});f.root.children=f.root.children.map(n=>n===f.rows[0]?copy:n);copy.parentElement=f.root;},
    f=>{const copy=element({...f.root.attrs});copy.children=f.root.children;copy.parentElement=f.body;f.body.children=[copy,f.uid];for(const n of copy.children)n.parentElement=copy;},
  ])await check({mutate});
  for(const href of ['https://foreign.test/','https://pan.baidu.com/','https://passport.baidu.com/',SOURCE.replace('fixture_1','other'),SOURCE.split('?')[0],SOURCE+'\n'])await check({href},'PageNotValidated',(_,reads)=>assert.equal(reads,0));
  await check({href:SOURCE.split('?')[0],expectedSourceUrl:SOURCE.split('?')[0]},'Observed');
  for(const expectedSourceUrl of [null,{},'',SOURCE+'#x'])await check({expectedSourceUrl});
  await check({frame:true});await check({overflow:true});await check({expire:true},'TimedOut');await check({budget:0},'TimedOut');
  process.stdout.write(`${assertions} offline directory assertions passed\n`);
})().catch(error=>{process.stderr.write(String(error.stack));process.exitCode=1;});
