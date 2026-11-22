// Enter at the end of a text block (jsdom): a new text block on the same level, not a sub-point.
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
const A = '6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70';

function setUp(blockContent) {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const dotnet = { invokeMethodAsync: () => Promise.resolve() };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({ type: 'doc', content: [{ type: 'textBlock', attrs: { key: A }, content: blockContent }] }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const press = () => editor.view.someProp('handleKeyDown', f => f(editor.view, new window.KeyboardEvent('keydown', { key: 'Enter' })));
    const shape = () => editor.getJSON().content.map(b => b.content.map(p => `${p.attrs.depth}:${(p.content ?? []).map(t => t.text).join('')}`));
    return { editor, press, shape, done: () => { api.destroy(); host.remove(); } };
}

const para = (text, depth = 0) => ({ type: 'para', attrs: { key: null, depth }, content: text ? [{ type: 'text', text }] : [] });

test('Enter at the end of a text block without sub-points: a new empty text block after it, the cursor in it', () => {
    const s = setUp([para('Erster Text')]);
    s.editor.commands.setTextSelection(2 + 'Erster Text'.length);
    s.press();

    assert.deepEqual(s.shape(), [['0:Erster Text'], ['0:']]);
    const json = s.editor.getJSON();
    assert.notEqual(json.content[1].attrs.key, A);
    assert.equal(s.editor.state.selection.$from.index(0), 1);
    s.done();
});

test('Enter at the end of a sub-point: a sub-point of the same depth, as before', () => {
    const s = setUp([para('Eigen'), para('Unterpunkt', 1), para('Tiefer', 2)]);
    const end = s.editor.state.doc.child(0).nodeSize - 2;
    s.editor.commands.setTextSelection(end);
    s.press();

    assert.deepEqual(s.shape(), [['0:Eigen', '1:Unterpunkt', '2:Tiefer', '2:']]);
    s.done();
});

test('Enter at the end of the own text of a block with sub-points: a sub-point, as before', () => {
    const s = setUp([para('Eigen'), para('Unterpunkt', 1)]);
    s.editor.commands.setTextSelection(2 + 'Eigen'.length);
    s.press();

    assert.deepEqual(s.shape(), [['0:Eigen', '1:', '1:Unterpunkt']]);
    s.done();
});

test('Enter inside the text of a block: a sub-point split off, as before', () => {
    const s = setUp([para('Erster Text')]);
    s.editor.commands.setTextSelection(2 + 'Erster'.length);
    s.press();

    assert.deepEqual(s.shape(), [['0:Erster', '1: Text']]);
    s.done();
});
