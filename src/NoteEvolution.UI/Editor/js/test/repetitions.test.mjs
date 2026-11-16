// setBlockRepetitions in a DOM (jsdom): the "≈ n" mark at the right edge of a text block is only shown; it is no edit,
// stays out of the undo history, and a click on it reports RepetitionClicked without moving the cursor. Run with
// `npm test` in the folder above.
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

/** A mark as .NET sends it: the count and the localized title. */
const mark = count => ({ count, title: `${count} Titel` });

const docJson = () => JSON.stringify({ type: 'doc', content: [block(A, 'Erster Text'), block(B, 'Zweiter Text')] });

/** An editor with two text blocks; calls holds what it reported to .NET. */
function setUp() {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push({ method, args }); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(docJson(), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const markOf = key => host.querySelector(`.ne-block[data-key="${key}"] .ne-block-rep`);
    const callsOf = method => calls.filter(call => call.method === method);
    return { host, api, editor, markOf, callsOf, done: () => { api.destroy(); host.remove(); } };
}

test('setBlockRepetitions shows ≈ n on the block and hides it for 0', () => {
    const s = setUp();
    s.editor.commands.setTextSelection(4);
    const selection = s.editor.state.selection.toJSON();
    assert.equal(s.markOf(A).hidden, true);

    s.api.setBlockRepetitions({ [A]: mark(3) });

    assert.equal(s.markOf(A).textContent, '≈ 3');
    // The title comes from .NET (localized); the script has no text of its own.
    assert.equal(s.markOf(A).title, '3 Titel');
    assert.equal(s.markOf(A).getAttribute('aria-label'), '3 Titel');
    assert.equal(s.markOf(A).hidden, false);
    assert.equal(s.markOf(A).getAttribute('contenteditable'), 'false');
    assert.equal(s.markOf(B).hidden, true);
    // Shown only: no history step, no change reported, the cursor stays.
    assert.equal(undoDepth(s.editor.state), 0);
    assert.equal(s.callsOf('DocumentChanged').length, 0);
    assert.deepEqual(s.editor.state.selection.toJSON(), selection);

    // 0 or a key that is absent takes the mark away again.
    s.api.setBlockRepetitions({ [A]: mark(0), [B]: mark(1) });
    assert.equal(s.markOf(A).hidden, true);
    assert.equal(s.markOf(B).textContent, '≈ 1');
    s.api.setBlockRepetitions({});
    assert.equal(s.markOf(B).hidden, true);
    assert.equal(undoDepth(s.editor.state), 0);
    s.done();
});

test('a click on the mark reports RepetitionClicked with the block key and changes no selection', () => {
    const s = setUp();
    s.editor.commands.setTextSelection(4);
    const selection = s.editor.state.selection.toJSON();
    s.api.setBlockRepetitions({ [A]: mark(2), [B]: mark(5) });

    s.markOf(B).dispatchEvent(new window.MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    s.markOf(B).dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));

    assert.deepEqual(s.callsOf('RepetitionClicked').map(call => call.args), [[B]]);
    assert.deepEqual(s.editor.state.selection.toJSON(), selection);
    assert.equal(s.callsOf('DocumentChanged').length, 0);
    s.done();
});

test('an undo after a text edit reverts only the text, not the marks set after it', () => {
    const s = setUp();
    s.editor.commands.insertContentAt(4, 'X');
    assert.equal(undoDepth(s.editor.state), 1);

    s.api.setBlockRepetitions({ [A]: mark(4) });
    assert.equal(undoDepth(s.editor.state), 1);

    assert.equal(s.editor.commands.undo(), true);
    assert.equal(s.editor.state.doc.child(0).textContent, 'Erster Text');
    assert.equal(s.markOf(A).textContent, '≈ 4');
    assert.equal(s.editor.commands.undo(), false);
    s.done();
});

test('the marks do not survive setDocument: they show again only when sent again', () => {
    const s = setUp();
    s.api.setBlockRepetitions({ [A]: mark(3) });
    assert.equal(s.markOf(A).hidden, false);

    s.api.setDocument(docJson(), false, false);
    assert.equal(s.markOf(A).hidden, true);
    assert.equal(s.markOf(A).textContent, '');

    s.api.setBlockRepetitions({ [A]: mark(3) });
    assert.equal(s.markOf(A).textContent, '≈ 3');
    s.done();
});

test('a block copied from a marked one carries no mark', () => {
    const s = setUp();
    s.api.setBlockRepetitions({ [A]: mark(3) });
    const copy = block(A, 'Kopie');
    // The copy is pasted after the original: its key is taken, so it gets a new one and no mark.
    s.editor.commands.insertContentAt(s.editor.state.doc.content.size, copy);
    const marked = [...s.host.querySelectorAll('.ne-block-rep')].filter(mark => !mark.hidden);
    assert.equal(marked.length, 1);
    s.done();
});
