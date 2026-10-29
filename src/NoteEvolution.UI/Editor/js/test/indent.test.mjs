// Tab / Shift+Tab and the indent buttons in a DOM (jsdom, Einordnen spec 4): sub-points move in the editor, also from
// the buttons through the interop (shiftDepth at the cursor); a text block's own text and a sub-point of level 1
// leaving its block are section commands for C#. Run with `npm test` in the folder above.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';

const dom = new JSDOM('<!doctype html><html><body></body></html>', { pretendToBeVisual: true });
const { window } = dom;
for (const name of ['window', 'document', 'navigator', 'Node', 'Element', 'HTMLElement', 'HTMLInputElement',
    'HTMLTextAreaElement', 'HTMLSelectElement', 'MutationObserver', 'getComputedStyle', 'requestAnimationFrame',
    'cancelAnimationFrame', 'DOMParser', 'getSelection', 'KeyboardEvent']) {
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

const BLOCK = '6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70';
const FIRST = '92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081';
const SECOND = 'a3d2c4e5-7e40-4b1d-9f6c-4d5e6f708192';

function para(key, depth, text) {
    return { type: 'para', attrs: { key, depth }, content: [{ type: 'text', text }] };
}

/** A text block "Text" with the sub-points "Erster" and "Zweiter" (both level 1). */
const doc = () => JSON.stringify({
    type: 'doc',
    content: [{ type: 'textBlock', attrs: { key: BLOCK }, content: [para(null, 0, 'Text'), para(FIRST, 1, 'Erster'), para(SECOND, 1, 'Zweiter')] }],
});

function open() {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push([method, ...args]); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(doc(), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const depths = () => {
        const result = [];
        editor.state.doc.firstChild.forEach(p => result.push(p.attrs.depth));
        return result;
    };
    const key = (name, shiftKey = false) =>
        editor.view.dom.dispatchEvent(new window.KeyboardEvent('keydown', { key: name, shiftKey, bubbles: true, cancelable: true }));
    const commands = () => calls.filter(([method]) => method === 'SectionCommand').map(([, name]) => name);
    const close = () => {
        api.destroy();
        host.remove();
    };
    return { api, editor, depths, key, commands, close };
}

test('shiftDepth an der Cursor-Stelle über die Interop', () => {
    const { api, editor, depths, commands, close } = open();

    api.reveal(SECOND, 2, false);
    api.shiftDepth(1);
    assert.deepEqual(depths(), [0, 1, 2]);
    assert.equal(editor.state.selection.$from.parent.textContent, 'Zweiter');

    api.shiftDepth(-1);
    assert.deepEqual(depths(), [0, 1, 1]);

    // The first sub-point has none before it: it stays where it is, as with Tab.
    api.reveal(FIRST, 0, false);
    api.shiftDepth(1);
    assert.deepEqual(depths(), [0, 1, 1]);
    assert.deepEqual(commands(), []);
    close();
});

test('Tab in the text of a block and Shift+Tab on level 1 are section commands; deeper ones stay in the editor', () => {
    const { api, depths, key, commands, close } = open();

    // The block's own text: into the text block before it (C#).
    api.reveal(BLOCK, 1, false);
    key('Tab');
    assert.deepEqual(commands(), ['IndentText']);
    assert.deepEqual(depths(), [0, 1, 1]);

    // A sub-point after another: one step deeper in the editor, and back.
    api.reveal(SECOND, 0, false);
    key('Tab');
    assert.deepEqual(depths(), [0, 1, 2]);
    key('Tab', true);
    assert.deepEqual(depths(), [0, 1, 1]);
    assert.deepEqual(commands(), ['IndentText']);

    // Level 1 and Shift+Tab: a text block of its own (C#).
    key('Tab', true);
    assert.deepEqual(commands(), ['IndentText', 'OutdentDetail']);
    assert.deepEqual(depths(), [0, 1, 1]);
    close();
});
