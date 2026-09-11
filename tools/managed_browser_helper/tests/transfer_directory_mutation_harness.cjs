'use strict';
// Actual fixed scripts in a synthetic VM only: no browser, network, storage, or process authority.
const assert = require('node:assert/strict'), vm = require('node:vm'), fs = require('node:fs');
const input = JSON.parse(fs.readFileSync(0, 'utf8'));
const DIRECTORY_SOURCE = 'https://pan.baidu.com/s/fixture_1?pwd=1234';
const SOURCE = 'https://pan.baidu.com/s/synthetic_-123?pwd=a1B2';
const uid = input.directory.baiduUserId;
let assertions = 0;
function forbidden() { throw Error('forbidden capability'); }
function element(attrs = {}, options = {}) {
  return {attrs, children: [], nodeType: 1, tagName: options.tag || 'DIV', textContent: options.text || '',
    parentElement: null, isConnected: true, hidden: false, disabled: false, inert: false, form: null, value: '',
    style: {display: 'block', visibility: 'visible', opacity: '1', pointerEvents: 'auto'},
    getAttribute(name) { return Object.hasOwn(this.attrs, name) ? this.attrs[name] : null; },
    getClientRects() { return this.noRects ? [] : [{width: 50, height: 20}]; },
    contains(other) { for (let p = other; p; p = p.parentElement) if (p === this) return true; return false; },
    querySelectorAll(selector) { return descendants(this).filter(node => matches(node, selector)); },
    click: forbidden, dispatchEvent() { return true; }, setAttribute: forbidden, appendChild: forbidden,
    scrollIntoView: forbidden, remove: forbidden, get innerHTML() { forbidden(); },
  };
}
function descendants(node) { return node.children.flatMap(child => [child, ...descendants(child)]); }
function matches(node, selector) {
  return selector.split(',').some(part => {
    const value = part.trim();
    if (['main', 'form', 'button'].includes(value)) return node.tagName.toLowerCase() === value;
    const classMatch = /^\.([\w-]+)$/.exec(value);
    if (classMatch) return String(node.attrs.class || '').split(/\s+/).includes(classMatch[1]);
    const match = /^\[([\w-]+)(?:="([^"]*)")?\]$/.exec(value);
    if (!match) throw Error('unexpected selector: ' + selector);
    return Object.hasOwn(node.attrs, match[1]) && (match[2] === undefined || node.attrs[match[1]] === match[2]);
  });
}
function add(parent, child) { parent.children.push(child); child.parentElement = parent; return child; }
function mutationFixture(action, options, expected) {
  const body = element();
  const dialogAttrs = {role: 'dialog', 'aria-label': '保存到 选择保存位置 新建文件夹',
    'data-parent-id': options.parent || expected.parentExactId,
    'data-path': options.path || expected.canonicalPath,
    'data-ancestor-ids': JSON.stringify(options.ancestors || expected.ancestorExactIds),
    'data-generation': options.generation || expected.generationToken};
  if (action === 'ConfirmCreateTransferDirectory') {
    dialogAttrs['data-create-dialog'] = 'true'; dialogAttrs['data-create-name'] = 'created';
  }
  const dialog = add(body, element(dialogAttrs));
  const identity = add(body, element({'data-uid': options.uid || uid}));
  const total = add(dialog, element({'data-total-count': '2'}));
  const end = add(dialog, element({'data-list-end': 'true'}));
  const rows = [add(dialog, element({'data-file-id': '101', 'data-kind': 'folder', title: '测试文件夹'})),
    add(dialog, element({'data-fsid': '202', 'data-kind': 'folder', 'data-name': ' spaced name '}))];
  if (options.preItemMismatch) rows[1].attrs['data-name'] = 'forged pre item';
  const inputNode = element({'data-managed-directory-input': 'true', type: options.inputType || 'text'}, {tag: options.inputTag || 'INPUT'});
  inputNode.disabled = !!options.inputDisabled;
  inputNode.readOnly = !!options.inputReadOnly;
  inputNode.inert = !!options.inputInert;
  inputNode.hidden = !!options.inputHidden;
  if (options.inputPointerBlocked) inputNode.style.pointerEvents = 'none';
  if (options.inputFormBound) inputNode.form = {};
  if (options.inputDisabledAttribute) inputNode.attrs.disabled = '';
  if (options.inputReadOnlyAttribute) inputNode.attrs.readonly = '';
  if (options.inputInertAttribute) inputNode.attrs.inert = '';
  if (options.inputAriaDisabled) inputNode.attrs['aria-disabled'] = 'true';
  inputNode.dispatchEvent = event => {
    if (event.type === 'input' || event.type === 'change') dialog.attrs['data-create-name'] = inputNode.value;
    return true;
  };
  if (options.inputEventFault) inputNode.dispatchEvent = () => { throw Error('synthetic post-click input event fault'); };
  if (options.inputSetFault) Object.defineProperty(inputNode, 'value', {
    get() { return ''; }, set() { throw Error('synthetic post-click input setter fault'); }});
  if (!options.inputSetFault) inputNode.value = options.confirmInputValue || 'created';
  if (action === 'ConfirmCreateTransferDirectory' && !options.confirmInputMissing) {
    if (options.inputAncestor) {
      const ancestor = add(dialog, element({}, {tag: options.inputAncestor}));
      ancestor.disabled = !!options.inputAncestorDisabled; ancestor.inert = !!options.inputAncestorInert;
      if (options.inputAncestorAriaDisabled) ancestor.attrs['aria-disabled'] = 'true';
      if (options.inputAncestorPointerBlocked) ancestor.style.pointerEvents = 'none';
      add(ancestor, inputNode);
    } else add(dialog, inputNode);
    if (options.duplicateInput) add(dialog, element({'data-managed-directory-input': 'true', type: 'text'}, {tag: 'INPUT'}));
  }
  const actionName = {OpenTransferDirectoryRoot: 'open-root', EnterTransferDirectory: 'enter-child',
    BeginCreateTransferDirectory: 'begin-create', ConfirmCreateTransferDirectory: 'confirm-create',
    CancelTransferPicker: 'cancel-picker'}[action];
  const control = add(dialog, element({'data-managed-directory-action': actionName,
    'data-exact-id': options.wrongId ? 'wrong-id' : action === 'OpenTransferDirectoryRoot' ? expected.ancestorExactIds[0] : '101',
    'data-canonical-path': action === 'OpenTransferDirectoryRoot' && !options.wrongRootPath ? '/' : '/目标目录/测试文件夹',
    'data-name': options.wrongName ? 'wrong-name' : '测试文件夹',
    'data-new-segment': options.wrongSegment ? 'wrong-segment' : 'created'}, {tag: options.unsafe ? 'A' : 'BUTTON'}));
  if (options.controlPointerBlocked) control.style.pointerEvents = 'none';
  let clicks = 0, postRowReads = 0;
  control.click = () => {
    clicks++;
    if (options.throwClick) throw Error('post-dispatch synthetic fault');
    if (action === 'BeginCreateTransferDirectory' && !options.inputMissing) {
      dialog.attrs['data-create-dialog'] = 'true'; dialog.attrs['data-create-name'] = 'created';
      if (options.inputAncestor) {
        const ancestor = add(dialog, element({}, {tag: options.inputAncestor}));
        ancestor.disabled = !!options.inputAncestorDisabled; ancestor.inert = !!options.inputAncestorInert;
        if (options.inputAncestorAriaDisabled) ancestor.attrs['aria-disabled'] = 'true';
        if (options.inputAncestorPointerBlocked) ancestor.style.pointerEvents = 'none';
        add(ancestor, inputNode);
      } else add(dialog, inputNode);
      if (options.duplicateInput) add(dialog, element({'data-managed-directory-input': 'true', type: 'text'}, {tag: 'INPUT'}));
    }
    if (action === 'OpenTransferDirectoryRoot') {
      dialog.attrs['data-parent-id'] = expected.ancestorExactIds[0];
      dialog.attrs['data-path'] = '/';
      dialog.attrs['data-ancestor-ids'] = JSON.stringify([expected.ancestorExactIds[0]]);
      dialog.attrs['data-generation'] = options.postGeneration || 'directory-root-generation-2';
    }
    if (action === 'EnterTransferDirectory') {
      dialog.attrs['data-parent-id'] = '101';
      dialog.attrs['data-path'] = '/目标目录/测试文件夹';
      dialog.attrs['data-ancestor-ids'] = JSON.stringify([...expected.ancestorExactIds, '101']);
      dialog.attrs['data-generation'] = options.postGeneration || 'directory-child-generation-2';
      rows[0].attrs['data-file-id'] = '301';
      rows[0].attrs.title = '子目录内容';
    }
    if (options.postPath) dialog.attrs['data-path'] = options.postPath;
    if (options.postAncestors) dialog.attrs['data-ancestor-ids'] = JSON.stringify(options.postAncestors);
    if (options.postMissingEnd) dialog.children = dialog.children.filter(node => node !== end);
  };
  if (options.duplicate) add(dialog, element({'data-managed-directory-action': actionName}, {tag: 'BUTTON'}));
  if (options.competing) add(body, element({role: 'alertdialog', 'aria-label': '请完成安全验证'}));
  if (options.securityOverlayClass) {
    const overlay = add(body, element({class: options.securityOverlayClass}));
    overlay.hidden = !!options.securityOverlayHidden;
  }
  if (options.nested) add(dialog, element({role: 'dialog', 'aria-label': '登录百度网盘'}));
  const originalQuery = dialog.querySelectorAll.bind(dialog);
  dialog.querySelectorAll = selector => {
    if (options.preFault && selector.startsWith('[data-managed')) throw Error('synthetic pre-click fault');
    const result = originalQuery(selector);
    if (clicks && selector === '[data-fsid],[data-file-id]') {
      postRowReads++;
      if (options.postSnapshotDrift && postRowReads === 2) rows[1].attrs['data-name'] = 'drifted between post snapshots';
    }
    return result;
  };
  return {body, dialog, identity, total, end, rows, inputNode, control, clicks: () => clicks};
}
async function mutate(action, options = {}) {
  const expected = options.directory || input.directory;
  const f = mutationFixture(action, options, expected);
  const document = {defaultView: null, querySelectorAll(selector) {
    return descendants(f.body).filter(node => matches(node, selector));
  }};
  const context = vm.createContext({document, location: {href: DIRECTORY_SOURCE}, Date: {now: () => 0},
    TextEncoder, getComputedStyle: node => node.style, Event: class {constructor(type, value) {this.type = type; this.value = value;}},
    fetch: forbidden, XMLHttpRequest: forbidden, WebSocket: forbidden, setTimeout: forbidden,
    localStorage: {getItem: forbidden}, sessionStorage: {getItem: forbidden}, navigator: {sendBeacon: forbidden}});
  context.window = context; context.top = context; vm.runInContext('document.defaultView = window;', context);
  const payload = {expectedBaiduUserId: uid, sourceUri: DIRECTORY_SOURCE, expectedDirectory: expected,
    rootBreadcrumbExactId: expected.ancestorExactIds[0], childExactId: '101',
    childCanonicalPath: '/目标目录/测试文件夹', newSegment: 'created'};
  const value = await vm.runInContext(`(function(){${input.mutation}}).call(undefined,${JSON.stringify(action)},${JSON.stringify(payload)},${options.budget ?? 100})`, context, {timeout: 1000});
  return {value: JSON.parse(JSON.stringify(value)), clicks: f.clicks(), inputValue: f.inputNode.value};
}
function sourceFixture(actual, options) {
  const body = element();
  const root = add(body, element({'aria-label': '分享文件 保存到网盘', 'data-generation': actual.generationToken}, {tag: 'MAIN'}));
  const identity = add(body, element({'data-uid': actual.baiduUserId}));
  const total = add(root, element({'data-total-count': String(actual.items.length)}));
  const end = add(root, element({'data-list-end': 'true'}));
  const rows = actual.items.map((item, index) => add(root, element({[index % 2 ? 'data-fsid' : 'data-file-id']: item.exactId,
    'data-kind': item.kind.toLowerCase(), [index % 2 ? 'data-name' : 'title']: item.serverName})));
  const marker = add(body, element({'data-original-external-share': 'true',
    'data-source-fingerprint': options.markerFingerprint || input.source.observationFingerprint}));
  if (options.dialog) add(body, element({role: 'dialog', 'aria-label': '保存到 选择保存位置'}));
  if (options.hiddenDialog) {
    const hidden = add(body, element({role: 'alertdialog', 'aria-label': '请完成安全验证'})); hidden.hidden = true;
  }
  if (options.securityOverlayClass) {
    const overlay = add(body, element({class: options.securityOverlayClass}));
    overlay.hidden = !!options.securityOverlayHidden;
  }
  if (options.destinationControl) add(body, element({'data-managed-directory-action': 'cancel-picker'}, {tag: 'BUTTON'}));
  let rowReads = 0;
  const originalQuery = root.querySelectorAll.bind(root);
  root.querySelectorAll = selector => {
    const result = originalQuery(selector);
    if (selector === '[data-fsid],[data-file-id]' && ++rowReads === 3 && options.finalDrift)
      rows[0].attrs[Object.hasOwn(rows[0].attrs, 'title') ? 'title' : 'data-name'] = 'final snapshot drift';
    return result;
  };
  return {body, root, identity, total, end, rows, marker};
}
async function closed(expected = input.source, options = {}) {
  const actual = options.actualSource || input.source;
  const f = sourceFixture(actual, options);
  const document = {defaultView: null, querySelectorAll(selector) {
    return descendants(f.body).filter(node => matches(node, selector));
  }};
  const context = vm.createContext({document, location: {href: options.href || SOURCE}, Date: {now: () => 0},
    TextEncoder, getComputedStyle: node => node.style, fetch: forbidden, XMLHttpRequest: forbidden,
    WebSocket: forbidden, setTimeout: forbidden, localStorage: {getItem: forbidden},
    sessionStorage: {getItem: forbidden}, navigator: {sendBeacon: forbidden}});
  context.window = context; context.top = context; vm.runInContext('document.defaultView = window;', context);
  const payload = {expectedBaiduUserId: uid, expectedSource: expected, sourceUri: SOURCE};
  const pending = vm.runInContext(`(function(){${input.closed}}).call(undefined,${JSON.stringify(payload)},${options.budget ?? 100})`, context, {timeout: 1000});
  if (options.mutate) options.mutate(f, context);
  return JSON.parse(JSON.stringify(await pending));
}
function checked(value, expected) { assert.deepEqual(value, expected); assertions++; }
function checkedClosed(value, kind = 'PageNotValidated') {
  assert.equal(value.kind, kind);
  if (kind === 'PickerClosed') {
    const expected = {...input.source}; delete expected.observationFingerprint;
    assert.deepEqual(value.postSource, expected);
  } else assert.deepEqual(value, {kind});
  assertions++;
}
(async () => {
  const successes = {OpenTransferDirectoryRoot: 'RootOpened', EnterTransferDirectory: 'DirectoryEntered',
    BeginCreateTransferDirectory: 'CreateDialogOpened', ConfirmCreateTransferDirectory: 'DirectoryCreated',
    CancelTransferPicker: 'PickerCancelled'};
  for (const [action, kind] of Object.entries(successes)) {
    const result = await mutate(action);
    assert.equal(result.value.kind, kind); assert.equal(result.clicks, 1);
    if (action === 'OpenTransferDirectoryRoot') {
      assert.equal(result.value.postDirectory.parentExactId, 'root-1');
      assert.deepEqual(result.value.postDirectory.ancestorExactIds, ['root-1']);
    } else if (action === 'EnterTransferDirectory') {
      assert.equal(result.value.postDirectory.parentExactId, '101');
      assert.deepEqual(result.value.postDirectory.ancestorExactIds, ['root-1', 'parent-1', '101']);
    } else assert.deepEqual(result.value, {kind});
    if (action === 'BeginCreateTransferDirectory') assert.equal(result.inputValue, 'created');
    assertions++;
  }
  for (const action of Object.keys(successes)) {
    for (const options of [{duplicate: true}, {competing: true}, {nested: true}, {parent: 'wrong-parent'},
      {path: '/wrong'}, {ancestors: ['root-1', 'wrong-parent']}, {generation: 'stale'}, {unsafe: true},
      {controlPointerBlocked: true}, {preFault: true}, {preItemMismatch: true}]) {
      const result = await mutate(action, options); checked(result.value, {kind: 'PageNotValidated'}); assert.equal(result.clicks, 0);
    }
    const fault = await mutate(action, {throwClick: true}); checked(fault.value, {kind: 'OutcomeUnknown'}); assert.equal(fault.clicks, 1);
    const mismatch = await mutate(action, {uid: 'wrong-uid'}); checked(mismatch.value, {kind: 'AccountIdentityMismatch'}); assert.equal(mismatch.clicks, 0);
    checked((await mutate(action, {budget: 0})).value, {kind: 'TimedOut'});
    for (const securityOverlayClass of ['login-dialog', 'security-verification', 'verify-dialog']) {
      const visibleOverlay = await mutate(action, {securityOverlayClass});
      checked(visibleOverlay.value, {kind: 'PageNotValidated'}); assert.equal(visibleOverlay.clicks, 0);
      const hiddenOverlay = await mutate(action, {securityOverlayClass, securityOverlayHidden: true});
      assert.equal(hiddenOverlay.value.kind, successes[action]); assert.equal(hiddenOverlay.clicks, 1); assertions++;
    }
  }
  let result = await mutate('OpenTransferDirectoryRoot', {wrongId: true}); checked(result.value, {kind: 'PageNotValidated'}); assert.equal(result.clicks, 0);
  result = await mutate('OpenTransferDirectoryRoot', {wrongRootPath: true}); checked(result.value, {kind: 'PageNotValidated'}); assert.equal(result.clicks, 0);
  result = await mutate('EnterTransferDirectory', {wrongId: true}); checked(result.value, {kind: 'PageNotValidated'}); assert.equal(result.clicks, 0);
  result = await mutate('EnterTransferDirectory', {wrongName: true}); checked(result.value, {kind: 'PageNotValidated'}); assert.equal(result.clicks, 0);
  for (const action of ['BeginCreateTransferDirectory', 'ConfirmCreateTransferDirectory']) {
    result = await mutate(action, {wrongSegment: true}); checked(result.value, {kind: 'PageNotValidated'}); assert.equal(result.clicks, 0);
  }
  for (const options of [{confirmInputMissing: true}, {confirmInputValue: 'wrong-value'}, {duplicateInput: true},
    {inputTag: 'TEXTAREA'}, {inputType: 'submit'}, {inputFormBound: true}, {inputDisabled: true},
    {inputDisabledAttribute: true}, {inputReadOnly: true}, {inputReadOnlyAttribute: true},
    {inputInert: true}, {inputInertAttribute: true}, {inputAriaDisabled: true}, {inputHidden: true},
    {inputPointerBlocked: true}, {inputAncestor: 'FORM'}, {inputAncestor: 'A'},
    {inputAncestor: 'LABEL'}, {inputAncestor: 'SUMMARY'},
    {inputAncestor: 'DIV', inputAncestorDisabled: true}, {inputAncestor: 'DIV', inputAncestorInert: true},
    {inputAncestor: 'DIV', inputAncestorAriaDisabled: true}, {inputAncestor: 'DIV', inputAncestorPointerBlocked: true}]) {
    result = await mutate('ConfirmCreateTransferDirectory', options);
    checked(result.value, {kind: 'PageNotValidated'}); assert.equal(result.clicks, 0);
  }
  for (const options of [{inputMissing: true}, {duplicateInput: true}, {inputTag: 'TEXTAREA'}, {inputType: 'submit'},
    {inputFormBound: true}, {inputHidden: true}, {inputPointerBlocked: true},
    {inputDisabled: true}, {inputDisabledAttribute: true}, {inputReadOnly: true},
    {inputReadOnlyAttribute: true}, {inputInert: true}, {inputInertAttribute: true}, {inputAriaDisabled: true},
    {inputAncestor: 'FORM'},
    {inputAncestor: 'A'}, {inputAncestor: 'LABEL'}, {inputAncestor: 'SUMMARY'},
    {inputAncestor: 'DIV', inputAncestorDisabled: true}, {inputAncestor: 'DIV', inputAncestorInert: true},
    {inputAncestor: 'DIV', inputAncestorAriaDisabled: true}, {inputAncestor: 'DIV', inputAncestorPointerBlocked: true},
    {inputSetFault: true}, {inputEventFault: true}]) {
    result = await mutate('BeginCreateTransferDirectory', options);
    checked(result.value, {kind: 'OutcomeUnknown'}); assert.equal(result.clicks, 1);
  }
  for (const action of ['OpenTransferDirectoryRoot', 'EnterTransferDirectory']) {
    result = await mutate(action, {postSnapshotDrift: true}); checked(result.value, {kind: 'OutcomeUnknown'}); assert.equal(result.clicks, 1);
    result = await mutate(action, {postPath: '/wrong-post-path'}); checked(result.value, {kind: 'OutcomeUnknown'}); assert.equal(result.clicks, 1);
    result = await mutate(action, {postAncestors: ['forged-root']}); checked(result.value, {kind: 'OutcomeUnknown'}); assert.equal(result.clicks, 1);
    result = await mutate(action, {postMissingEnd: true}); checked(result.value, {kind: 'OutcomeUnknown'}); assert.equal(result.clicks, 1);
  }
  checkedClosed(await closed(), 'PickerClosed');
  checked(await closed(input.source, {dialog: true}), {kind: 'PageNotValidated'});
  checkedClosed(await closed(input.source, {hiddenDialog: true}), 'PickerClosed');
  for (const securityOverlayClass of ['login-dialog', 'security-verification', 'verify-dialog']) {
    checked(await closed(input.source, {securityOverlayClass}), {kind: 'PageNotValidated'});
    checkedClosed(await closed(input.source, {securityOverlayClass, securityOverlayHidden: true}), 'PickerClosed');
  }
  checked(await closed(input.source, {destinationControl: true}), {kind: 'PageNotValidated'});
  checked(await closed(input.source, {href: 'https://pan.baidu.com/s/other?pwd=a1B2'}), {kind: 'PageNotValidated'});
  checkedClosed(await closed(input.source, {markerFingerprint: '0'.repeat(64)}), 'PickerClosed');
  checked(await closed(input.source, {actualSource: {...input.source, generationToken: 'forged-generation'}}), {kind: 'PageNotValidated'});
  checked(await closed(input.source, {actualSource: {...input.source, items: input.source.items.map((item, index) =>
    index ? item : {...item, serverName: 'forged item'})}}), {kind: 'PageNotValidated'});
  checked(await closed(input.source, {mutate: f => {f.root.attrs['data-generation'] = 'repeated-snapshot-drift';}}), {kind: 'PageNotValidated'});
  checked(await closed(input.source, {finalDrift: true}), {kind: 'PageNotValidated'});
  checked(await closed(input.source, {budget: 0}), {kind: 'TimedOut'});
  process.stdout.write(`${assertions} offline directory mutation assertions passed\n`);
})().catch(error => {process.stderr.write(String(error.stack)); process.exitCode = 1;});
