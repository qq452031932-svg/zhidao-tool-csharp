'use strict';

// This process never imports a browser or makes network calls. The only fetch in
// the VM is the fixture below; Python supplies the immutable production script.
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const {script} = JSON.parse(fs.readFileSync(0, 'utf8'));
const FIXED_URL = 'https://pan.baidu.com/api/gettemplatevariable?fields=%5B%22uk%22%2C%22username%22%5D';
const ROOT = 'https://pan.baidu.com/';
const MAIN = 'https://pan.baidu.com/disk/main#/index?category=all';
const MAIN_HOME_FLOW = 'https://pan.baidu.com/disk/main?from=homeFlow#/index?category=all';
const BODY = '{"errno":0,"result":{"uk":9007199254740993,"username":"private-name"}}';
const unavailable = {kind: 'BrowserUnavailable'};
let assertions = 0;

async function execute(options = {}) {
  const stats = {fetches: [], reads: 0, cancels: 0, aborts: 0};
  const chunks = options.chunks || [new TextEncoder().encode(options.body ?? BODY)];
  let index = 0;
  const reader = {
    async read() {
      stats.reads++;
      if (options.readHook) options.readHook(context, stats);
      if (options.readError) throw new Error('private-reader-error');
      if (options.readPending) return new Promise(() => {});
      if (index < chunks.length) return {done: false, value: chunks[index++]};
      return {done: true, value: undefined};
    },
    cancel() {
      stats.cancels++;
      if (options.cancelThrows) throw new Error('private-cancel-error');
      if (options.cancelRejects) return Promise.reject(new Error('private-cancel-error'));
      if (options.cancelPending) return new Promise(() => {});
      return Promise.resolve();
    },
    releaseLock() {},
  };
  const response = {
    status: 200,
    redirected: false,
    url: FIXED_URL,
    headers: {get: name => name.toLowerCase() === 'content-length' ? (options.length ?? null) : null},
    body: {getReader: () => reader, cancel: () => reader.cancel()},
    ...options.response,
  };
  const context = vm.createContext({
    URL, Uint8Array, TextDecoder, Date, Number, setTimeout, clearTimeout,
    AbortController: class extends AbortController {
      abort() { stats.aborts++; super.abort(); }
    },
    location: {href: options.href ?? ROOT},
    document: {},
    fetch: async (url, init) => {
      stats.fetches.push({url, init});
      if (options.fetchHook) options.fetchHook(context, stats);
      if (options.fetchError) throw new Error('private-network-cookie-error');
      if (options.fetchPending) return new Promise(() => {});
      return response;
    },
  });
  context.window = context;
  context.top = context;
  // Establish the browser identity relation inside the VM's own global realm.
  vm.runInContext('document.defaultView = window;', context);
  if (options.frame) context.top = {};
  const start = Date.now();
  const result = await vm.runInContext(`(function(){${script}\n}).call(undefined, ${options.budget ?? 100})`, context);
  // Cross-realm objects intentionally normalized only after the actual code runs.
  return {result: JSON.parse(JSON.stringify(result)), stats, elapsed: Date.now() - start};
}

async function check(options, expected, extra) {
  const observed = await execute(options);
  assert.deepEqual(observed.result, expected);
  if (extra) extra(observed);
  assertions++;
}

(async () => {
  for (const href of [ROOT, MAIN, MAIN_HOME_FLOW, 'https://pan.baidu.com:443/']) {
    await check({href}, {kind: 'QueryResponse', status: 200, body: BODY}, ({stats}) => {
      assert.equal(stats.fetches.length, 1);
      const {url, init} = stats.fetches[0];
      assert.equal(url, FIXED_URL);
      assert.deepEqual(Object.keys(init).sort(), ['cache', 'credentials', 'method', 'mode', 'redirect', 'signal']);
      assert.equal(init.method, 'GET');
      assert.equal(init.credentials, 'same-origin');
      assert.equal(init.mode, 'same-origin');
      assert.equal(init.redirect, 'error');
      assert.equal(init.cache, 'no-store');
      assert.ok(init.signal instanceof AbortSignal);
    });
  }
  for (const href of ['https://passport.baidu.com/', 'https://evil.test/', 'http://pan.baidu.com/',
    'https://sub.pan.baidu.com/', 'https://pan.baidu.com.evil.test/', 'https://user@pan.baidu.com/',
    'https://user:pass@pan.baidu.com/', 'https://pan.baidu.com:444/', ROOT + '?q=1', ROOT + '#x',
    ROOT + '?', ROOT + '#', MAIN + '&x=1', 'https://pan.baidu.com/disk/main#/index?category=other',
    'https://pan.baidu.com/disk/main?x=1#/index?category=all',
    'https://pan.baidu.com/disk/main/extra#/index?category=all']) {
    await check({href}, unavailable, ({stats}) => assert.equal(stats.fetches.length, 0));
  }
  await check({frame: true}, unavailable, ({stats}) => assert.equal(stats.fetches.length, 0));
  // Old fake DOM identity attributes cannot create an observed result.
  await check({body: '{}', fetchHook: c => { c.document.querySelector = () => ({getAttribute: () => '42'}); }},
              {kind: 'QueryResponse', status: 200, body: '{}'});
  for (const hookName of ['fetchHook', 'readHook']) {
    for (const mutate of [c => {c.location.href = 'https://passport.baidu.com/';},
                          c => {c.location.href = MAIN;},
                          c => {c.document = {}; }]) {
      await check({[hookName]: mutate}, unavailable);
    }
  }
  for (const response of [{redirected: true}, {url: 'https://evil.test/private'}, {url: ''},
    {status: 0}, {status: true}, {status: 200.5}, {status: 600}, {body: null}]) {
    await check({response}, unavailable);
  }
  await check({length: '65537'}, unavailable, ({stats}) => assert.equal(stats.reads, 0));
  await check({length: '999999999999999999999999999'}, unavailable, ({stats}) => assert.equal(stats.reads, 0));
  for (const length of ['-1', 'NaN', '1.0', '65536x']) await check({length}, unavailable);
  await check({chunks: [new Uint8Array(32768), new Uint8Array(32769)]}, unavailable,
              ({stats}) => assert.ok(stats.cancels >= 1));
  await check({chunks: [new Uint8Array([0xc3, 0x28])]}, unavailable,
              ({stats}) => assert.ok(stats.cancels >= 1));
  const encoded = new TextEncoder().encode('中');
  await check({chunks: [encoded.slice(0, 1), encoded.slice(1)]},
              {kind: 'QueryResponse', status: 200, body: '中'});
  const limitBody = 'a'.repeat(65536);
  await check({body: limitBody, length: '65536'}, {kind: 'QueryResponse', status: 200, body: limitBody});
  for (const options of [{fetchPending: true}, {readPending: true},
                         {readPending: true, cancelPending: true},
                         {readPending: true, cancelThrows: true},
                         {readPending: true, cancelRejects: true}]) {
    await check({...options, budget: 20}, {kind: 'TimedOut'}, ({stats, elapsed}) => {
      assert.ok(elapsed < 1000, 'timeout must not await noncooperative fetch/read/cancel');
      assert.ok(stats.aborts >= 1);
      if (options.readPending) assert.ok(stats.cancels >= 1);
    });
  }
  for (const options of [{fetchError: true}, {readError: true}, {readError: true, cancelThrows: true},
                         {readError: true, cancelRejects: true}]) await check(options, unavailable);
  await check({budget: 0}, {kind: 'TimedOut'}, ({stats}) => assert.equal(stats.fetches.length, 0));
  process.stdout.write(`${assertions} offline script assertions passed\n`);
})().catch(error => { process.stderr.write(String(error.stack)); process.exitCode = 1; });
