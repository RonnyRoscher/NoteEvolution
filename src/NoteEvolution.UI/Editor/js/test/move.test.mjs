// Text blocks dragged from one editor of the split view into the other (jsdom): the target reports BlocksDropped with
// the whole blocks the other editor drags, not a note; text inside a block is not moved. Run with `npm test`.
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

const { NodeSelection, TextSelection } = await import('@tiptap/pm/state');

const A = '6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70';
const B = '92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081';
const C = 'a3d2c4e5-7e40-4b1d-9f6c-4d5e6f708192';

const block = (key, text) => ({ type: 'textBlock', attrs: { key }, content: [{ type: 'para', content: [{ type: 'text', text }] }] });

function open(blocks) {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push([method, ...args]); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({ type: 'doc', content: blocks }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const close = () => {
        api.destroy();
        host.remove();
    };
    return { editor, calls, close };
}

function drop(view) {
    const event = new window.Event('drop', { bubbles: true, cancelable: true });
    Object.assign(event, { clientX: 0, clientY: 0, dataTransfer: null });
    view.dom.dispatchEvent(event);
    return event;
}

test('A text block dragged from the other editor is reported as moved, not as a note', () => {
    const upper = open([block(A, 'Erster'), block(B, 'Zweiter')]);
    const lower = open([block(C, 'Dritter')]);
    const source = upper.editor.view;
    source.dragging = { slice: NodeSelection.create(source.state.doc, 0).content(), move: true };

    const event = drop(lower.editor.view);

    assert.ok(event.defaultPrevented);
    assert.deepEqual(lower.calls.filter(([m]) => m === 'BlocksDropped' || m === 'NoteDropped'),
        [['BlocksDropped', JSON.stringify([A]), C]]);
    // Nothing is inserted into the lower editor: C# moves the block and both halves show the saved book.
    assert.equal(lower.editor.state.doc.childCount, 1);
    source.dragging = null;
    upper.close();
    lower.close();
});

test('Text inside a block dragged from the other editor moves nothing', () => {
    const upper = open([block(A, 'Erster'), block(B, 'Zweiter')]);
    const lower = open([block(C, 'Dritter')]);
    const source = upper.editor.view;
    source.dragging = { slice: TextSelection.create(source.state.doc, 2, 5).content(), move: true };

    drop(lower.editor.view);

    assert.deepEqual(lower.calls.filter(([m]) => m === 'BlocksDropped' || m === 'NoteDropped'), []);
    assert.equal(lower.editor.state.doc.textContent, 'Dritter');
    source.dragging = null;
    upper.close();
    lower.close();
});

test('Without a drag in another editor a drop is a note', () => {
    const lower = open([block(C, 'Dritter')]);

    drop(lower.editor.view);

    assert.deepEqual(lower.calls.filter(([m]) => m === 'BlocksDropped' || m === 'NoteDropped'), [['NoteDropped', C]]);
    lower.close();
});
