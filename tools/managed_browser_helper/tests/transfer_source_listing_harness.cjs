'use strict';
// No browser/network: run the actual immutable script in a synthetic VM.
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const {script, probe} = JSON.parse(fs.readFileSync(0, 'utf8'));
const SOURCE = 'https://pan.baidu.com/s/synthetic_-123?pwd=a1B2';
const UID = 'synthetic-private-uid';
let assertions = 0;
function forbidden() { throw Error('forbidden capability'); }
function element(attrs = {}, options = {}) {
  const node = {
    attrs, children: [], nodeType: 1, tagName: options.tag || 'DIV',
    textContent: options.text || '', parentElement: null, isConnected: true, hidden: false,
    style: {display: 'block', visibility: 'visible', opacity: '1'},
    getAttribute(name) { return Object.hasOwn(this.attrs, name) ? this.attrs[name] : null; },
    getClientRects() { return this.noRects ? [] : [{width: 50, height: 20}]; },
    contains(other) { for (let p = other; p; p = p.parentElement) if (p === this) return true; return false; },
    querySelectorAll(selector) { return descendants(this).filter(n => matches(n, selector)); },
    click: forbidden, setAttribute: forbidden, appendChild: forbidden, dispatchEvent: forbidden,
    scrollIntoView: forbidden, remove: forbidden,
    get innerHTML() { forbidden(); }, get value() { forbidden(); },
  };
  return node;
}
function descendants(node) { return node.children.flatMap(child => [child, ...descendants(child)]); }
function matches(node, selector) {
  return selector.split(',').some(part => {
    const value = part.trim();
    if (['main', 'form'].includes(value)) return node.tagName.toLowerCase() === value;
    const match = /^\[([\w-]+)(?:="([^"]*)")?\]$/.exec(value);
    if (!match) throw Error('unexpected selector: ' + selector);
    return Object.hasOwn(node.attrs, match[1]) && (match[2] === undefined || node.attrs[match[1]] === match[2]);
  });
}
function add(parent, child) { parent.children.push(child); child.parentElement = parent; return child; }
function fixture(options) {
  const body = element();
  const root = add(body, element({'aria-label': '分享文件 保存到网盘', 'data-generation': 'generation-1'}, {tag: 'MAIN'}));
  const uid = add(body, element({'data-uid': UID}));
  const total = add(root, element({'data-total-count': '2'}));
  const end = add(root, element({'data-list-end': 'true'}));
  const rows = [add(root, element({'data-fsid': '202', 'data-kind': 'file', 'data-name': '应用.apk'})),
                add(root, element({'data-file-id': '101', 'data-kind': 'folder', 'title': '测试文件夹'}))];
  const f = {body, root, uid, total, end, rows};
  if (options.prepare) options.prepare(f);
  return f;
}
async function execute(options = {}) {
  const f = fixture(options);
  let clock = 0, reads = 0, context;
  const doc = {querySelectorAll(selector) {
    reads++;
    if (options.expire) clock = 1000;
    return options.overflow ? Array(257).fill(f.root) : descendants(f.body).filter(n => matches(n, selector));
  }};
  context = vm.createContext({document: doc, location: {href: options.href ?? SOURCE},
    Date: {now: () => clock}, TextEncoder, getComputedStyle: node => node.style,
    fetch: forbidden, XMLHttpRequest: forbidden, WebSocket: forbidden,
    setTimeout: forbidden, localStorage: {getItem: forbidden}, sessionStorage: {getItem: forbidden},
    navigator: {sendBeacon: forbidden},
  });
  context.window = context;
  context.top = options.frame ? {} : context;
  vm.runInContext('document.defaultView = window;', context);
  const requestedSource = Object.hasOwn(options, 'expectedSourceUrl') ? options.expectedSourceUrl : SOURCE;
  const pending = vm.runInContext(`(function(){${script}\n}).call(undefined, ${JSON.stringify(options.uid ?? UID)}, ${options.budget ?? 100}, ${JSON.stringify(requestedSource)})`, context, {timeout: 1000});
  // The script reaches its microtask suspension before this mutation executes.
  if (options.mutate) options.mutate(f, context);
  const result = JSON.parse(JSON.stringify(await pending));
  return {result, reads};
}
async function check(options, kind = 'PageNotValidated', verify) {
  const {result, reads} = await execute(options);
  if (kind !== 'Observed') assert.deepEqual(result, {kind});
  else assert.equal(result.kind, kind);
  if (verify) verify(result, reads);
  assertions++;
}
(async () => {
  if (probe) {
    process.stdout.write(JSON.stringify((await execute(probe)).result));
    return;
  }
  await check({}, 'Observed', result => assert.deepEqual(result, {
    kind: 'Observed', baiduUserId: UID, generationToken: 'generation-1', totalCount: 2,
    reachedEnd: true, isStable: true,
    items: [{exactId: '101', kind: 'Folder', serverName: '测试文件夹'},
            {exactId: '202', kind: 'File', serverName: '应用.apk'}],
  }));
  // The outer target can report SOURCE before and after an ABA route change.
  // A valid *different* share with the same UID must fail inside the actual script.
  for (const href of [SOURCE.replace('synthetic_-123', 'other-source'),
    SOURCE.replace('synthetic_-123', 'other-source').split('?')[0],
    SOURCE.replace('a1B2', 'C3d4')]) {
    await check({href}, 'PageNotValidated', (_, reads) => assert.equal(reads, 0));
  }
  await check({href: SOURCE.split('?')[0]}, 'PageNotValidated', (_, reads) => assert.equal(reads, 0));
  await check({expectedSourceUrl: SOURCE.split('?')[0]}, 'PageNotValidated', (_, reads) => assert.equal(reads, 0));
  await check({expectedSourceUrl: SOURCE.split('?')[0], href: SOURCE.split('?')[0]}, 'Observed');
  for (const expectedSourceUrl of [null, 1, {}, [], '', SOURCE + '\n', SOURCE + '#x',
    SOURCE.replace('https:', 'http:'), SOURCE.replace('synthetic_-123', '中'),
    SOURCE.replace('synthetic_-123', 'a'.repeat(129)), SOURCE.replace('a1B2', 'abcdx')]) {
    await check({expectedSourceUrl}, 'PageNotValidated', (_, reads) => assert.equal(reads, 0));
  }
  for (const kind of ['folder', 'file']) await check({prepare: f => {
    f.root.children = f.root.children.filter(n => n !== f.rows[1]);
    f.total.attrs['data-total-count'] = '1';
    f.rows[0].attrs['data-kind'] = kind;
  }}, 'Observed', result => assert.equal(result.items.length, 1));
  await check({prepare: f => {
    f.rows[0].attrs['data-name'] = ' same name ';
    f.rows[1].attrs.title = ' same name ';
    f.root.attrs['data-list-version'] = f.root.attrs['data-generation'];
    f.uid.attrs['data-uk'] = UID;
  }}, 'Observed');
  for (const kind of ['MAIN', 'FORM', 'DIV']) await check({prepare: f => {
    f.root.tagName = kind;
    f.root.attrs.role = kind === 'DIV' ? 'dialog' : 'main';
  }}, 'Observed');
  for (const count of [0, 32, 33]) await check({prepare: f => {
    f.root.children = [f.total, f.end];
    f.total.attrs['data-total-count'] = String(count);
    for (let i = 0; i < count; i++) add(f.root, element({'data-fsid': String(i).padStart(3, '0'), 'data-kind': 'file', 'data-name': '😀'.repeat(64)}));
  }}, count === 32 ? 'Observed' : 'PageNotValidated');
  for (const prepare of [
    f => {delete f.root.attrs['data-generation'];},
    f => {f.root.attrs['data-list-version'] = 'conflict';},
    f => {f.root.attrs['data-generation'] = '😀'.repeat(65);},
    f => {f.root.attrs['data-generation'] = '\ud800';},
    f => {f.root.attrs['aria-label'] = '下载';},
    f => {f.root.children = f.root.children.filter(n => n !== f.total);},
    f => {f.root.children = f.root.children.filter(n => n !== f.end);},
    f => {f.total.attrs['data-total-count'] = '3';},
    f => {f.total.attrs['data-total-count'] = '2.0';},
    f => {f.total.attrs['data-total-count'] = '02';},
    f => {f.total.attrs['data-total-count'] = 'Infinity';},
    f => {f.end.attrs['data-list-end'] = 'false';},
    f => {add(f.root, element({'data-total-count': '2'}));},
    f => {add(f.root, element({'data-list-end': 'true'}));},
    f => {delete f.rows[0].attrs['data-kind'];},
    f => {f.rows[0].attrs['data-kind'] = 'File';},
    f => {f.rows[0].attrs['data-kind'] = 'other';},
    f => {f.rows[0].attrs['data-file-id'] = 'conflict';},
    f => {f.rows[0].attrs.title = 'conflict';},
    f => {f.rows[0].attrs['data-fsid'] = '101';},
    f => {f.rows[0].attrs['data-fsid'] = 'a'.repeat(129);},
    f => {f.rows[0].attrs['data-fsid'] = '中';},
    f => {f.rows[0].attrs['data-name'] = '';},
    f => {f.rows[0].attrs['data-name'] = 'x\n';},
    f => {f.rows[0].attrs['data-name'] = '😀'.repeat(65);},
    f => {f.rows[0].attrs['data-name'] = '\ud800';},
    f => {f.rows[0].attrs['data-id'] = '202'; delete f.rows[0].attrs['data-fsid'];},
    f => {add(f.root, element({}, {tag: 'FORM'}));},
    f => {add(f.body, element({'aria-label': '分享文件', 'role': 'main'}));},
    f => {const extra = add(f.body, element({'aria-label': '分享文件', 'role': 'main'})); extra.hidden = true;},
    f => {f.body.children = f.body.children.filter(n => n !== f.uid);},
    f => {f.uid.attrs['data-uid'] = '';},
    f => {f.uid.attrs['data-uid'] = '\ud800';},
  ]) await check({prepare});
  for (const target of ['root', 'uid', 'total', 'end', 'row']) {
    for (const change of [n => {n.hidden = true;}, n => {n.style.display = 'none';},
      n => {n.style.visibility = 'hidden';}, n => {n.style.visibility = 'collapse';},
      n => {n.style.opacity = '0';}, n => {n.noRects = true;},
      n => {n.isConnected = false;}, n => {n.attrs['aria-hidden'] = 'true';}]) {
      await check({prepare: f => change(target === 'row' ? f.rows[0] : f[target])});
    }
  }
  await check({prepare: f => {f.body.hidden = true;}});
  for (const prepare of [f => {f.uid.attrs['data-uid'] = 'other';},
    f => {f.uid.attrs['data-uk'] = 'other';},
    f => {add(f.body, element({'data-uk': 'other'}));}]) await check({prepare}, 'AccountIdentityMismatch');
  for (const mutate of [
    f => {f.root.attrs['data-generation'] = 'other';},
    f => {f.rows[0].attrs['data-name'] = 'other';},
    f => {f.rows[0].attrs['data-kind'] = 'folder';},
    f => {f.rows[0].attrs['data-fsid'] = '203';},
    f => {f.rows[0].attrs.title = f.rows[0].attrs['data-name'];},
    f => {f.total.attrs['data-total-count'] = '3';},
    f => {f.end.attrs['data-list-end'] = 'false';},
    f => {f.end.hidden = true;},
    f => {f.rows[0].hidden = true;},
    f => {f.root.attrs['aria-label'] = 'other';},
    f => {const replacement = element({...f.root.attrs}, {tag: 'MAIN'}); replacement.children = f.root.children; replacement.parentElement = f.body; f.body.children = [replacement, f.uid]; for (const c of replacement.children) c.parentElement = replacement;},
    (f, c) => {c.location.href = SOURCE.split('?')[0];},
    (f, c) => {c.document = {};},
    f => {const copy = element({...f.rows[0].attrs}); f.root.children = f.root.children.map(n => n === f.rows[0] ? copy : n); copy.parentElement = f.root;},
  ]) await check({mutate});
  await check({mutate: f => {f.uid.attrs['data-uid'] = 'other';}}, 'AccountIdentityMismatch');
  for (const href of ['https://pan.baidu.com/', 'https://passport.baidu.com/', 'https://foreign.test/s/a', SOURCE + '\n', SOURCE + '#x'])
    await check({href}, 'PageNotValidated', (_, reads) => assert.equal(reads, 0));
  await check({frame: true});
  await check({overflow: true});
  await check({expire: true}, 'TimedOut');
  await check({budget: 0}, 'TimedOut');
  process.stdout.write(`${assertions} offline listing assertions passed\n`);
})().catch(error => {process.stderr.write(String(error.stack)); process.exitCode = 1;});
