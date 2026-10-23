// The focus in a DOM (jsdom): the editor tells dotnet each time it gets the focus, also where the cursor already is
// (the cursor report skips an unchanged cursor); in a split view that makes its half the active one.
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

function block(key, text) {
    return { type: 'textBlock', attrs: { key }, content: [{ type: 'para', attrs: { key: null }, content: [{ type: 'text', text }] }] };
}

test('getting the focus is reported every time, before the cursor, even at an unchanged cursor', () => {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push([method, ...args]); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({ type: 'doc', content: [block(A, 'Erster Text'), block(B, 'Zweiter Text')] }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const focus = () => editor.view.dom.dispatchEvent(new window.FocusEvent('focus'));
    const blur = () => editor.view.dom.dispatchEvent(new window.FocusEvent('blur'));
    const reported = () => calls.filter(([method]) => method === 'EditorFocused' || method === 'CursorChanged').map(([method, , key]) => method === 'CursorChanged' ? `cursor:${key}` : 'focus');

    api.reveal(B, 3);
    blur();
    calls.length = 0;

    focus();
    assert.deepEqual(reported(), ['focus']);

    // Away and back to the same place: the focus again, still no cursor report (it did not change).
    blur();
    focus();
    assert.deepEqual(reported(), ['focus', 'focus']);

    // Focused at another place: the focus first, then the cursor.
    blur();
    calls.length = 0;
    api.setDocument(JSON.stringify({ type: 'doc', content: [block(A, 'Erster Text'), block(B, 'Zweiter Text')] }), false, false);
    focus();
    assert.deepEqual(reported(), ['focus', `cursor:${A}`]);

    api.destroy();
    host.remove();
});

test('reveal without focus moves the cursor but leaves the focus where it is (the other half of a split view)', () => {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const other = document.createElement('input');
    document.body.appendChild(other);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push([method, ...args]); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({ type: 'doc', content: [block(A, 'Erster Text'), block(B, 'Zweiter Text')] }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    // A plain element elsewhere has the focus (not a text field, which reveal leaves alone anyway).
    const away = document.createElement('div');
    away.tabIndex = 0;
    document.body.appendChild(away);
    away.focus();
    calls.length = 0;

    api.reveal(B, 3, false);

    assert.equal(editor.state.selection.$from.parent.textContent, 'Zweiter Text');
    assert.equal(editor.state.selection.$from.parentOffset, 3);
    assert.equal(document.activeElement, away);
    assert.equal(calls.filter(([method]) => method === 'EditorFocused').length, 0);

    // By default the editor takes the focus.
    api.reveal(A);
    assert.notEqual(document.activeElement, away);

    api.destroy();
    host.remove();
    other.remove();
    away.remove();
});
