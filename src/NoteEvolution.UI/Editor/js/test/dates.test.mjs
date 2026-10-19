// setBlockDates in a DOM (jsdom): the dates of a text block are only shown; they are no edit, stay out of the undo
// history and leave the cursor where it is. Run with `npm test` in the folder above.
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
const B = '92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081';

function block(key, text) {
    return { type: 'textBlock', attrs: { key }, content: [{ type: 'para', attrs: { key: null }, content: [{ type: 'text', text }] }] };
}

/** An editor with two text blocks; calls holds what it reported to .NET. */
function setUp() {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push({ method, args }); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({ type: 'doc', content: [block(A, 'Erster Text'), block(B, 'Zweiter Text')] }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const datesOf = key => host.querySelector(`.ne-block[data-key="${key}"] .ne-block-dates`);
    const changes = () => calls.filter(call => call.method === 'DocumentChanged').length;
    return { host, api, editor, datesOf, changes, done: () => { api.destroy(); host.remove(); } };
}

test('setBlockDates shows the label, adds no history step, reports no change and keeps the selection', () => {
    const s = setUp();
    s.editor.commands.setTextSelection(4);
    const selection = s.editor.state.selection.toJSON();
    assert.equal(s.datesOf(A).hidden, true);

    s.api.setBlockDates([{ key: A, dates: '19.10.2026', datesTitle: 'Erstellt 01.10.2026 · geändert 19.10.2026' }]);

    assert.equal(s.datesOf(A).textContent, '19.10.2026');
    assert.equal(s.datesOf(A).title, 'Erstellt 01.10.2026 · geändert 19.10.2026');
    assert.equal(s.datesOf(A).hidden, false);
    assert.equal(s.datesOf(B).hidden, true);
    assert.equal(undoDepth(s.editor.state), 0);
    assert.equal(s.changes(), 0);
    assert.deepEqual(s.editor.state.selection.toJSON(), selection);
    s.done();
});

test('an undo after a text edit reverts only the text, not the dates set after it', () => {
    const s = setUp();
    s.editor.commands.insertContentAt(4, 'X');
    assert.equal(s.changes(), 1);
    const depth = undoDepth(s.editor.state);
    assert.equal(depth, 1);

    s.api.setBlockDates([{ key: A, dates: '19.10.2026', datesTitle: null }]);
    assert.equal(undoDepth(s.editor.state), depth);
    assert.equal(s.changes(), 1);

    assert.equal(s.editor.commands.undo(), true);

    assert.equal(s.editor.state.doc.child(0).textContent, 'Erster Text');
    assert.equal(s.datesOf(A).textContent, '19.10.2026');
    assert.equal(s.editor.state.doc.child(0).attrs.dates, '19.10.2026');
    // Nothing left to undo: the dates were no step of their own.
    assert.equal(s.editor.commands.undo(), false);
    assert.equal(s.datesOf(A).textContent, '19.10.2026');
    s.done();
});

test('typing in a block with dates does not rewrite its label', () => {
    const s = setUp();
    s.api.setBlockDates([{ key: A, dates: '19.10.2026', datesTitle: 'Erstellt' }]);
    const label = s.datesOf(A).firstChild;
    let titleWrites = 0;
    new MutationObserver(records => { titleWrites += records.length; }).observe(s.datesOf(A), { attributes: true });

    s.editor.commands.insertContentAt(4, 'X');

    assert.equal(s.datesOf(A).firstChild, label);
    // Mutation records are delivered as a microtask.
    return Promise.resolve().then(() => {
        assert.equal(titleWrites, 0);
        s.done();
    });
});
