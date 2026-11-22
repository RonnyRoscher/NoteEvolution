// Typing reports the document lazily (jsdom): no full document per keystroke, one after a pause, at once on request.
// Run with `npm test` in the folder above.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';

const dom = new JSDOM('<!doctype html><html><body></body></html>', { pretendToBeVisual: true });
const { window } = dom;
for (const name of ['window', 'document', 'navigator', 'Node', 'Element', 'HTMLElement', 'HTMLInputElement',
    'HTMLTextAreaElement', 'HTMLSelectElement', 'MutationObserver', 'getComputedStyle', 'requestAnimationFrame',
    'cancelAnimationFrame', 'DOMParser', 'getSelection']) {
    Object.defineProperty(globalThis, name, {
        value: typeof window[name] === 'function' && !/^[A-Z]/.test(name) ? window[name].bind(window) : window[name],
        configurable: true,
        writable: true,
    });
}
// Not in jsdom: the editor only observes sizes to place the section bar, and ProseMirror measures ranges to scroll.
globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
window.ResizeObserver = globalThis.ResizeObserver;
const noRects = () => ({ length: 0, item: () => null, [Symbol.iterator]: [][Symbol.iterator] });
const zeroRect = () => ({ top: 0, bottom: 0, left: 0, right: 0, width: 0, height: 0, x: 0, y: 0 });
window.Range.prototype.getClientRects = noRects;
window.Range.prototype.getBoundingClientRect = zeroRect;
window.document.elementFromPoint = () => null;

const { createEditor } = await import('../editor.js');
const { undoDepth } = await import('@tiptap/pm/history');
const A = '6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70';

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

function setUp(options = {}) {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push({ method, args }); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0, ...options });
    api.setDocument(JSON.stringify({ type: 'doc', content: [{ type: 'textBlock', attrs: { key: A },
        content: [{ type: 'para', attrs: { key: null }, content: [{ type: 'text', text: 'Erster Text' }] }] }] }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const of = method => calls.filter(call => call.method === method);
    return { api, editor, of, done: () => { api.destroy(); host.remove(); } };
}

const textOf = json => JSON.parse(json).content[0].content[0].content[0].text;

test('typing sends no document per keystroke, one after the pause, with the last text', async () => {
    const s = setUp({ changeDelay: 60 });
    s.editor.commands.insertContentAt(4, 'a');
    s.editor.commands.insertContentAt(5, 'b');
    s.editor.commands.insertContentAt(6, 'c');
    assert.equal(s.of('DocumentChanged').length, 0);

    await wait(120);
    assert.equal(s.of('DocumentChanged').length, 1);
    assert.equal(textOf(s.of('DocumentChanged')[0].args[0]), 'Erabcster Text');
    s.done();
});

test('takePendingDocument returns the unsent document at once, then nothing; no later send', async () => {
    const s = setUp({ changeDelay: 60 });
    assert.equal(s.api.takePendingDocument(), null);
    s.editor.commands.insertContentAt(4, 'x');

    const json = s.api.takePendingDocument();
    assert.equal(textOf(json), 'Erxster Text');
    assert.equal(s.api.takePendingDocument(), null);
    await wait(120);
    assert.equal(s.of('DocumentChanged').length, 0);
    s.done();
});

test('a new document drops an unsent change', async () => {
    const s = setUp({ changeDelay: 60 });
    s.editor.commands.insertContentAt(4, 'x');
    s.api.setDocument(JSON.stringify({ type: 'doc', content: [{ type: 'textBlock', attrs: { key: A },
        content: [{ type: 'para', attrs: { key: null }, content: [{ type: 'text', text: 'Neu' }] }] }] }), false, false);

    assert.equal(s.api.takePendingDocument(), null);
    await wait(120);
    assert.equal(s.of('DocumentChanged').length, 0);
    s.done();
});

test('typing is reported cheaply, at most once per interval, without the document', async () => {
    const s = setUp({ changeDelay: 60, typingInterval: 80 });
    s.editor.commands.insertContentAt(4, 'a');
    s.editor.commands.insertContentAt(5, 'b');
    assert.equal(s.of('Typing').length, 1);
    assert.deepEqual(s.of('Typing')[0].args, []);

    await wait(100);
    s.editor.commands.insertContentAt(6, 'c');
    assert.equal(s.of('Typing').length, 2);
    s.done();
});

test('changeDelay 0 sends each change at once', () => {
    const s = setUp({ changeDelay: 0 });
    s.editor.commands.insertContentAt(4, 'a');
    assert.equal(s.of('DocumentChanged').length, 1);
    s.done();
});
