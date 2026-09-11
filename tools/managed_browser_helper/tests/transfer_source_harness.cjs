'use strict';

// Synthetic DOM only. Network, DOM mutation and input/click capabilities throw.
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const {script} = JSON.parse(fs.readFileSync(0, 'utf8'));
const SHARE = 'https://pan.baidu.com/s/Ab_-09?pwd=A1b2';
let assertions = 0;
function forbidden() { throw new Error('forbidden browser capability'); }
function element(options = {}) {
  const attrs = options.attrs || {};
  return Object.freeze({
    nodeType: 1, textContent: options.text || '', parentElement: options.parent || null,
    hidden: options.hidden || false, isConnected: options.connected !== false,
    styleFixture: {display: options.display || 'block', visibility: options.visibility || 'visible',
                   opacity: options.opacity || '1'},
    getAttribute: name => attrs[name] ?? null,
    getClientRects: () => options.rects === false ? [] : [{width: 40, height: 20}],
    querySelectorAll: selector => options.query ? options.query(selector) : [],
    closest: selector => selector === 'form' ? options.form || null : null,
    click: forbidden, setAttribute: forbidden, appendChild: forbidden,
    get innerHTML() { forbidden(); }, get value() { forbidden(); },
  });
}
function fixtures(options = {}) {
  const visibility = options.visibility || {};
  const submit = element({text: '提取文件', ...visibility});
  const form = element({query: () => [submit], ...visibility});
  const extraction = element({attrs: {name: 'pwd'}, form, ...visibility});
  const login = element({text: '登录百度网盘', ...visibility});
  const verification = element({text: '安全验证', ...visibility});
  const row = element({...visibility});
  const surface = element({query: () => options.empty ? [] : [row], ...visibility});
  const save = element({text: options.saveText || '保存到网盘', ...visibility});
  return selector => {
    if (selector.includes('input')) return options.extraction ? [extraction] : [];
    if (selector.includes('dialog')) return [...(options.login ? [login] : []), ...(options.verification ? [verification] : [])];
    if (selector.includes('share-file-list')) return options.source ? [surface] : [];
    if (selector.includes('button')) return options.source ? [save] : [];
    throw new Error('unexpected selector');
  };
}
function execute(options = {}) {
  let reads = 0;
  let clock = 0;
  let context;
  const query = fixtures(options);
  const document = {querySelectorAll(selector) {
    reads++;
    if (options.mutate && reads === 1) options.mutate(context);
    if (options.expire) clock = 1000;
    return options.overflow ? Array(257).fill(element()) : query(selector);
  }};
  context = vm.createContext({
    document, location: {href: options.href ?? SHARE},
    Date: {now: () => clock}, Number,
    getComputedStyle: node => node.styleFixture,
    fetch: forbidden, XMLHttpRequest: forbidden, WebSocket: forbidden,
    setTimeout: forbidden, localStorage: Object.freeze({getItem: forbidden}),
  });
  context.window = context;
  context.top = options.frame ? {} : context;
  vm.runInContext('document.defaultView = window;', context);
  const result = vm.runInContext(`(function(){${script}\n}).call(undefined, ${options.budget ?? 100})`, context,
                                 {timeout: 1000});
  return {result: JSON.parse(JSON.stringify(result)), reads};
}
function check(options, kind, extra) {
  const actual = execute(options);
  assert.deepEqual(actual.result, {kind});
  if (extra) extra(actual);
  assertions++;
}
check({source: true}, 'SourceOpened');
check({extraction: true}, 'ExtractionRequired');
check({login: true}, 'LoginRequired');
check({verification: true}, 'VerificationRequired');
check({}, 'PageNotValidated');
check({source: true, empty: true}, 'PageNotValidated');
check({source: true, saveText: '下载'}, 'PageNotValidated');
for (const options of [{source: true, extraction: true}, {source: true, login: true},
  {extraction: true, verification: true}, {login: true, verification: true}]) check(options, 'PageNotValidated');
for (const visibility of [{hidden: true}, {display: 'none'}, {visibility: 'hidden'},
  {visibility: 'collapse'}, {opacity: '0'}, {rects: false}, {connected: false},
  {parent: element({display: 'none'})}, {attrs: {'aria-hidden': 'true'}}]) {
  for (const hint of ['source', 'extraction', 'login', 'verification']) {
    check({[hint]: true, visibility}, 'PageNotValidated');
  }
}
for (const href of ['https://pan.baidu.com/', 'https://passport.baidu.com/', 'https://evil.test/s/a',
  SHARE + '#x', SHARE + '\n', SHARE.replace('pan.baidu.com', 'pan.baidu.com:443'),
  SHARE.replace('Ab_-09', '%41b_-09')]) {
  check({href, source: true}, 'PageNotValidated', ({reads}) => assert.equal(reads, 0));
}
check({source: true, frame: true}, 'PageNotValidated', ({reads}) => assert.equal(reads, 0));
check({source: true, overflow: true}, 'PageNotValidated');
check({source: true, expire: true}, 'TimedOut');
check({source: true, budget: 0}, 'TimedOut', ({reads}) => assert.equal(reads, 0));
check({source: true, mutate: c => {c.location.href = SHARE.split('?')[0];}}, 'PageNotValidated');
check({source: true, mutate: c => {c.document = {};}}, 'PageNotValidated');
process.stdout.write(`${assertions} offline source script assertions passed\n`);
