// Ctrl+Z in the editor (jsdom): with something to undo the editor undoes it; with an empty history (a freshly loaded
// document, e.g. after a structure command) it asks C# for the undo of the header. Run with `npm test`.
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

const A = '6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70';

function open() {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push([method, ...args]); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({
        type: 'doc',
        content: [{ type: 'textBlock', attrs: { key: A }, content: [{ type: 'para', content: [{ type: 'text', text: 'Text' }] }] }],
    }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const undoKey = () =>
        editor.view.dom.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true, cancelable: true }));
    const requests = () => calls.filter(([method]) => method === 'UndoRequested').length;
    const close = () => {
        api.destroy();
        host.remove();
    };
    return { editor, undoKey, requests, close };
}

test('Ctrl+Z with an empty history asks for the undo of the header', () => {
    const { editor, undoKey, requests, close } = open();

    undoKey();

    assert.equal(requests(), 1);
    assert.equal(editor.state.doc.textContent, 'Text');
    close();
});

test('Ctrl+Z with typed text undoes it in the editor, then asks for the header', () => {
    const { editor, undoKey, requests, close } = open();
    editor.commands.insertContentAt(3, 'XY');
    assert.equal(editor.state.doc.textContent, 'TXYext');

    undoKey();
    assert.equal(editor.state.doc.textContent, 'Text');
    assert.equal(requests(), 0);

    undoKey();
    assert.equal(requests(), 1);
    close();
});
