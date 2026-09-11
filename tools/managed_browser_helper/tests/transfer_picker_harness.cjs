'use strict';
// Actual immutable production script, no browser/CDP/network or storage.
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const {script, expectedSource} = JSON.parse(fs.readFileSync(0, 'utf8'));
const SOURCE = 'https://pan.baidu.com/s/synthetic_-123?pwd=a1B2';
const UID = 'synthetic-private-uid';
let assertions = 0;
function forbidden() { throw Error('forbidden capability'); }
function element(attrs = {}, tagName = 'DIV') {
  return {attrs, tagName, children: [], parentElement: null, nodeType: 1, isConnected: true,
    hidden: false, disabled: false, inert: false, textContent: '',
    style: {display: 'block', visibility: 'visible', opacity: '1', pointerEvents: 'auto'},
    getAttribute(name) { return Object.hasOwn(this.attrs, name) ? this.attrs[name] : null; },
    getClientRects() { return this.noRects ? [] : [{width: 50, height: 20}]; },
    contains(other) { for (let p = other; p; p = p.parentElement) if (p === this) return true; return false; },
    querySelectorAll(selector) { return descendants(this).filter(n => matches(n, selector)); },
    click: forbidden, setAttribute: forbidden, appendChild: forbidden, dispatchEvent: forbidden,
    scrollIntoView: forbidden, remove: forbidden,
    get innerHTML() { forbidden(); }, get value() { forbidden(); }};
}
function descendants(node) { return node.children.flatMap(child => [child, ...descendants(child)]); }
function matches(node, selector) {
  return selector.split(',').some(part => {
    const value = part.trim();
    if (['main', 'form', 'button'].includes(value)) return node.tagName.toLowerCase() === value;
    const match = /^\[([\w-]+)(?:="([^"]*)")?\]$/.exec(value);
    if (!match) throw Error('unexpected selector: ' + selector);
    return Object.hasOwn(node.attrs, match[1]) && (match[2] === undefined || node.attrs[match[1]] === match[2]);
  });
}
function add(parent, child) { parent.children.push(child); child.parentElement = parent; return child; }
function replace(old, replacement) {
  const parent = old.parentElement;
  parent.children = parent.children.map(n => n === old ? replacement : n);
  replacement.parentElement = parent;
  replacement.children = old.children;
  for (const n of replacement.children) n.parentElement = replacement;
  old.isConnected = false;
}
function fixture() {
  const body = element();
  const root = add(body, element({'aria-label': '分享文件 保存到网盘', 'data-generation': 'generation-1'}, 'MAIN'));
  const uid = add(body, element({'data-uid': UID}));
  const total = add(root, element({'data-total-count': '2'}));
  const end = add(root, element({'data-list-end': 'true'}));
  const rows = [add(root, element({'data-fsid': '202', 'data-kind': 'file', 'data-name': '应用.apk'})),
                add(root, element({'data-file-id': '101', 'data-kind': 'folder', 'title': '测试文件夹'}))];
  const control = add(root, element({'aria-label': '选择保存位置'}, 'BUTTON'));
  return {body, root, uid, total, end, rows, control};
}
async function execute(options = {}) {
  const f = fixture();
  let clock = 0, clicks = 0, context, snapshots = 0;
  f.control.click = function () {
    assert.equal(this, f.control);
    clicks++;
    if (options.throwClick) throw Error('private click exception');
    if (options.expireClick) clock = 1000;
    if (options.afterClick) options.afterClick(f, context);
  };
  if (options.prepare) options.prepare(f);
  const doc = {querySelectorAll(selector) {
    if (selector === 'main,form,[role="main"],[role="dialog"]') {
      snapshots++;
      if (options.lastSnapshot && snapshots === 3) options.lastSnapshot(f, context);
    }
    if (options.expireRead) clock = 1000;
    return options.overflow ? Array(257).fill(f.root) : descendants(f.body).filter(n => matches(n, selector));
  }};
  context = vm.createContext({document: doc, location: {href: options.href ?? SOURCE},
    Date: {now: () => clock}, TextEncoder, getComputedStyle: node => node.style,
    fetch: forbidden, XMLHttpRequest: forbidden, WebSocket: forbidden, setTimeout: forbidden,
    localStorage: {getItem: forbidden}, sessionStorage: {getItem: forbidden}, navigator: {sendBeacon: forbidden}});
  context.window = context;
  context.top = options.frame ? {} : context;
  vm.runInContext('document.defaultView = window;', context);
  const source = Object.hasOwn(options, 'expected') ? options.expected : expectedSource;
  const sourceUrl = Object.hasOwn(options, 'sourceUrl') ? options.sourceUrl : SOURCE;
  const pending = vm.runInContext(`(function(){${script}\n}).call(undefined, ${JSON.stringify(options.uid ?? UID)}, ${options.budget ?? 100}, ${JSON.stringify(sourceUrl)}, ${JSON.stringify(source)})`, context, {timeout: 1000});
  if (options.mutate) options.mutate(f, context);
  if (options.expireAwait) clock = 1000;
  return {result: JSON.parse(JSON.stringify(await pending)), clicks, snapshots};
}
async function check(options, kind = 'PageNotValidated', expectedClicks = 0) {
  const {result, clicks, snapshots} = await execute(options);
  assert.deepEqual(result, {kind});
  assert.equal(clicks, expectedClicks);
  if (kind === 'PickerOpened') assert.ok(snapshots >= 3, 'requires synchronous final snapshot');
  assertions++;
}
(async () => {
  await check({}, 'PickerOpened', 1);
  await check({prepare: f => {f.control.attrs['aria-label'] = '选择保存目录';}}, 'PickerOpened', 1);
  await check({prepare: f => {f.control.tagName = 'DIV'; f.control.attrs.role = 'button';}}, 'PickerOpened', 1);
  await check({prepare: f => {delete f.control.attrs['aria-label']; f.control.textContent = '选择保存位置';}}, 'PickerOpened', 1);
  await check({href: SOURCE.split('?')[0], sourceUrl: SOURCE.split('?')[0]}, 'PickerOpened', 1);
  await check({expected: {...expectedSource, items: [...expectedSource.items].reverse()}}, 'PickerOpened', 1);
  await check({throwClick: true}, 'OutcomeUnknown', 1);
  await check({expireClick: true}, 'OutcomeUnknown', 1);
  await check({afterClick: (f, c) => {c.document = {};}}, 'OutcomeUnknown', 1);
  for (const href of ['https://foreign.invalid/s/x', SOURCE + '#x', SOURCE + '\n', SOURCE.replace('synthetic_-123', 'other'), SOURCE.split('?')[0]]) await check({href});
  for (const sourceUrl of [null, 1, '', SOURCE + '\n', SOURCE.replace('https:', 'http:')]) await check({sourceUrl});
  for (const expected of [null, {}, [], {...expectedSource, kind: 'Other'}, {...expectedSource, totalCount: true},
    {...expectedSource, totalCount: 3}, {...expectedSource, isStable: false}, {...expectedSource, reachedEnd: false},
    {...expectedSource, generationToken: 'changed'}, {...expectedSource, observationFingerprint: 'bad'},
    {...expectedSource, items: []}, {...expectedSource, extra: 'private'},
    {...expectedSource, items: [{...expectedSource.items[0], serverName: 'wrong'}, expectedSource.items[1]]},
    {...expectedSource, items: [{...expectedSource.items[0], kind: 'File'}, expectedSource.items[1]]},
    {...expectedSource, items: [{...expectedSource.items[0], exactId: 'wrong'}, expectedSource.items[1]]},
    {...expectedSource, items: [expectedSource.items[0], expectedSource.items[0]]}]) await check({expected});
  await check({expected: {...expectedSource, baiduUserId: 'other'}}, 'AccountIdentityMismatch');
  const changes = [
    f => {f.root.attrs['data-generation'] = 'changed';},
    f => {f.root.attrs['data-list-version'] = 'conflict';},
    f => {f.rows[0].attrs['data-name'] = 'renamed';},
    f => {f.rows[0].attrs['data-kind'] = 'folder';},
    f => {f.rows[0].attrs['data-fsid'] = 'changed';},
    f => {f.total.attrs['data-total-count'] = '3';},
    f => {f.end.attrs['data-list-end'] = 'false';},
    f => {f.root.hidden = true;},
    f => {f.rows[0].isConnected = false;},
    f => {f.root.attrs['aria-label'] = 'other';},
    f => {add(f.body, element({role: 'dialog'}));},
    f => {add(f.body, element({role: 'alertdialog'}));},
    f => {add(f.root, element({role: 'alertdialog'}));},
    f => {add(f.root, element({'aria-label': '选择保存位置'}, 'BUTTON'));},
    f => {add(f.body, element({'aria-label': '选择保存目录'}, 'BUTTON'));},
    f => {add(f.control, element({}, 'BUTTON'));},
    f => {const parent = element({role: 'button'}); replace(f.control, parent); add(parent, f.control); f.control.isConnected = true; f.control.children = [];},
    f => {f.control.attrs['aria-label'] = '保存到网盘';},
    f => {f.control.attrs['aria-label'] = '转存';},
    f => {f.control.attrs['aria-label'] = '选择目录';},
    f => {f.control.attrs['aria-label'] = '测试文件夹';},
    f => {f.control.attrs['aria-label'] = ' 选择保存位置 ';},
    f => {f.control.attrs['aria-labelledby'] = 'unverified-label';},
    f => {f.control.tagName = 'A';},
    f => {f.control.tagName = 'A'; f.control.attrs.role = 'button'; f.control.attrs.href = 'https://foreign.invalid/';},
    f => {f.control.tagName = 'INPUT'; f.control.attrs.role = 'button'; f.control.attrs.type = 'submit';},
    f => {f.control.tagName = 'SUMMARY'; f.control.attrs.role = 'button';},
    f => {const parent = element({href: 'https://foreign.invalid/'}, 'A'); replace(f.control, parent); add(parent, f.control); f.control.isConnected = true; f.control.children = [];},
    f => {f.control.attrs.type = 'submit';},
    f => {f.control.attrs.type = 'reset';},
    f => {f.control.form = {};},
    f => {f.control.attrs.role = 'link';},
    f => {f.control.disabled = true;},
    f => {f.control.attrs.disabled = '';},
    f => {f.control.attrs['aria-disabled'] = 'true';},
    f => {f.control.inert = true;},
    f => {f.control.attrs.inert = '';},
    f => {f.body.inert = true;},
    f => {f.control.hidden = true;},
    f => {f.control.noRects = true;},
    f => {f.control.isConnected = false;},
    f => {f.control.style.display = 'none';},
    f => {f.control.style.visibility = 'hidden';},
    f => {f.control.style.opacity = '0';},
    f => {f.control.style.pointerEvents = 'none';},
    f => {f.control.attrs['aria-hidden'] = 'true';},
  ];
  for (const change of changes) {
    await check({prepare: change});
    await check({mutate: change});
    await check({lastSnapshot: change});
  }
  for (const mutate of [f => replace(f.root, element({...f.root.attrs}, 'MAIN')),
    f => replace(f.rows[0], element({...f.rows[0].attrs})),
    f => replace(f.control, element({...f.control.attrs}, 'BUTTON')),
    (f, c) => {c.location.href = SOURCE.split('?')[0];},
    (f, c) => {c.document = {};},
    f => {f.control.textContent = 'changed';}]) await check({mutate});
  for (const phase of ['prepare', 'mutate', 'lastSnapshot']) await check({[phase]: f => {f.uid.attrs['data-uid'] = 'other';}}, 'AccountIdentityMismatch');
  await check({frame: true});
  await check({overflow: true});
  await check({budget: 0}, 'TimedOut');
  await check({expireRead: true}, 'TimedOut');
  await check({expireAwait: true}, 'TimedOut');
  await check({lastSnapshot: (f, c) => {c.Date.now = () => 1000;}}, 'TimedOut');
  process.stdout.write(`${assertions} offline picker assertions passed\n`);
})().catch(error => {process.stderr.write(String(error.stack)); process.exitCode = 1;});
