// reveal in a DOM (jsdom): the cursor goes to the start of an element's text or an offset into it, clamped to the text.
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
const B = '92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081';
const D = 'a3d2c4e5-7e40-4b1d-9f6c-4d5e6f708192';

function block(key, text, detail) {
    const content = [{ type: 'para', attrs: { key: null }, content: [{ type: 'text', text }] }];
    if (detail) {
        content.push({ type: 'para', attrs: { key: detail.key, depth: 1 }, content: [{ type: 'text', text: detail.text }] });
    }
    return { type: 'textBlock', attrs: { key }, content };
}

test('reveal puts the cursor at the start of the text or an offset into it, clamped to the text', () => {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const dotnet = { invokeMethodAsync: () => Promise.resolve() };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({
        type: 'doc',
        content: [block(A, 'Erster Text', { key: D, text: 'Ein Detail' }), block(B, 'Zweiter Text')],
    }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const cursor = () => {
        const $from = editor.state.selection.$from;
        return [$from.parent.textContent, $from.parentOffset];
    };

    api.reveal(A);
    assert.deepEqual(cursor(), ['Erster Text', 0]);
    api.reveal(A, 6);
    assert.deepEqual(cursor(), ['Erster Text', 6]);
    api.reveal(A, 99);
    assert.deepEqual(cursor(), ['Erster Text', 11]);
    api.reveal(D, 3);
    assert.deepEqual(cursor(), ['Ein Detail', 3]);
    api.reveal(B, -2);
    assert.deepEqual(cursor(), ['Zweiter Text', 0]);

    api.destroy();
    host.remove();
});
