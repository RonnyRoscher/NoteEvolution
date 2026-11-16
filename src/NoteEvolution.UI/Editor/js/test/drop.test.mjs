// What a drop from outside the editors reports (jsdom): NoteDropped with the element a note goes after and, for a
// marked range dragged by its grip, the innermost element under the pointer (dropElement). Run with `npm test`.
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

const H = '6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70';
const A = '92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081';
const P = 'a3d2c4e5-7e40-4b1d-9f6c-4d5e6f708192';
const B = 'b4e3d5f6-8f51-4c2e-8a7d-5e6f708192a3';

const para = (text, attrs = {}) => ({ type: 'para', attrs, content: [{ type: 'text', text }] });

/**
 * A heading (100–120), a text block with its own text (130–150) and a sub-point (160–180) in 130–190, and a second
 * text block (200–220), at these heights on the screen.
 */
function open() {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push([method, ...args]); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({
        type: 'doc',
        content: [
            { type: 'heading', attrs: { key: H, level: 1 }, content: [{ type: 'text', text: 'Eins' }] },
            { type: 'textBlock', attrs: { key: A }, content: [para('Erster'), para('Unterpunkt', { key: P, depth: 1 })] },
            { type: 'textBlock', attrs: { key: B }, content: [para('Zweiter')] },
        ],
    }), false, false);
    const view = host.querySelector('.ne-doc').editor.view;
    const place = (pos, top, bottom) => {
        view.nodeDOM(pos).getBoundingClientRect = () => ({ top, bottom, height: bottom - top, left: 0, right: 100, width: 100, x: 0, y: top });
    };
    const doc = view.state.doc;
    const blockA = doc.child(0).nodeSize;
    const blockB = blockA + doc.child(1).nodeSize;
    place(0, 100, 120);
    place(blockA, 130, 190);
    place(blockA + 1, 130, 150);
    place(blockA + 1 + doc.child(1).child(0).nodeSize, 160, 180);
    place(blockB, 200, 220);
    place(blockB + 1, 200, 220);
    const close = () => {
        api.destroy();
        host.remove();
    };
    return { view, calls, close };
}

function dropAt(view, clientY) {
    const event = new window.Event('drop', { bubbles: true, cancelable: true });
    Object.assign(event, { clientX: 10, clientY, dataTransfer: null });
    view.dom.dispatchEvent(event);
    return event;
}

/** The arguments of the NoteDropped calls: [afterKey, elementKey]. */
const dropped = calls => calls.filter(([m]) => m === 'NoteDropped').map(([, ...args]) => args);

test('A drop over a heading names the heading', () => {
    const editor = open();
    dropAt(editor.view, 110);
    assert.deepEqual(dropped(editor.calls), [[H, H]]);
    editor.close();
});

test('A drop over a text block\'s own text names the text block', () => {
    const editor = open();
    const event = dropAt(editor.view, 140);
    assert.ok(event.defaultPrevented);
    // A note goes after the heading (the block's middle is below the pointer); the range lands on the text block.
    assert.deepEqual(dropped(editor.calls), [[H, A]]);
    editor.close();
});

test('A drop over a sub-point names the sub-point', () => {
    const editor = open();
    dropAt(editor.view, 170);
    assert.deepEqual(dropped(editor.calls), [[A, P]]);
    editor.close();
});

test('A drop above the first element names none', () => {
    const editor = open();
    dropAt(editor.view, 50);
    assert.deepEqual(dropped(editor.calls), [[null, null]]);
    editor.close();
});

test('A drop over the last text block names it', () => {
    const editor = open();
    dropAt(editor.view, 215);
    assert.deepEqual(dropped(editor.calls), [[B, B]]);
    editor.close();
});
