// setRepetitionMarks in a DOM (jsdom): repetitions are underlined as decorations at the offsets C# gives; they are only
// shown, move along with typing, stay out of the undo history and report no change. Run with `npm test` in the folder
// above.
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
const P = '7b1e2c3d-4e5f-4a6b-9c7d-8e9f0a1b2c3d';

const textA = 'Erster Text mit Wiederholung';
const textB = 'Zweiter Text';
const textP = 'Unterpunkt hier';

/** Block A with a sub-point P, then block B. */
const docJson = () => JSON.stringify({
    type: 'doc',
    content: [
        {
            type: 'textBlock',
            attrs: { key: A },
            content: [
                { type: 'para', attrs: { key: null }, content: [{ type: 'text', text: textA }] },
                { type: 'para', attrs: { key: P, depth: 1 }, content: [{ type: 'text', text: textP }] },
            ],
        },
        { type: 'textBlock', attrs: { key: B }, content: [{ type: 'para', attrs: { key: null }, content: [{ type: 'text', text: textB }] }] },
    ],
});

/** An editor with the document above; calls holds what it reported to .NET (changes are sent at once). */
function setUp() {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push({ method, args }); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0, changeDelay: 0 });
    api.setDocument(docJson(), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const marked = cls => [...host.querySelectorAll(`.${cls}`)];
    const block = key => host.querySelector(`.ne-block[data-key="${key}"]`);
    const callsOf = method => calls.filter(call => call.method === method);
    return { host, api, editor, marked, block, callsOf, done: () => { api.destroy(); host.remove(); } };
}

const mark = (key, start, length, kind, title = `${kind} Titel`) => ({ key, start, length, kind, title });

test('marks decorate the given ranges with their class and title, the document is unchanged', () => {
    const s = setUp();
    const before = s.editor.getJSON();

    s.api.setRepetitionMarks([
        mark(A, 7, 4, 'literal', 'Auch in: B'),
        mark(P, 0, 10, 'semantic'),
        mark(B, 0, 7, 'literal'),
        // Outside the paragraph and for a key the document does not hold: ignored.
        mark(B, 8, 50, 'literal'),
        mark('00000000-0000-4000-8000-000000000000', 0, 3, 'literal'),
    ]);

    assert.deepEqual(s.marked('ne-rep-literal').map(el => el.textContent), ['Text', 'Zweiter']);
    assert.equal(s.marked('ne-rep-literal')[0].title, 'Auch in: B');
    assert.deepEqual(s.marked('ne-rep-semantic').map(el => el.textContent), ['Unterpunkt']);
    assert.equal(s.marked('ne-rep-current').length, 0);
    assert.deepEqual(s.editor.getJSON(), before);

    // An empty list takes them all away.
    s.api.setRepetitionMarks([]);
    assert.equal(s.host.querySelectorAll('.ne-rep-literal, .ne-rep-semantic').length, 0);
    s.done();
});

test('a current mark gives its text block the ne-rep-current-block class', () => {
    const s = setUp();

    // The sub-point's mark counts for the text block that holds it.
    s.api.setRepetitionMarks([mark(B, 0, 7, 'literal'), mark(P, 0, 10, 'current')]);

    assert.deepEqual(s.marked('ne-rep-current').map(el => el.textContent), ['Unterpunkt']);
    assert.equal(s.block(A).classList.contains('ne-rep-current-block'), true);
    assert.equal(s.block(B).classList.contains('ne-rep-current-block'), false);

    s.api.setRepetitionMarks([mark(B, 0, 7, 'current')]);
    assert.equal(s.block(A).classList.contains('ne-rep-current-block'), false);
    assert.equal(s.block(B).classList.contains('ne-rep-current-block'), true);
    s.done();
});

test('typing maps the marks; new marks replace the old ones', () => {
    const s = setUp();
    s.api.setRepetitionMarks([mark(A, 7, 4, 'literal')]);

    // "XY" at the start of block A's text: the mark stays on "Text".
    s.editor.commands.insertContentAt(2, 'XY');
    assert.equal(s.editor.state.doc.child(0).child(0).textContent, `XY${textA}`);
    assert.deepEqual(s.marked('ne-rep-literal').map(el => el.textContent), ['Text']);

    // The next marks are at the offsets of the new text.
    s.api.setRepetitionMarks([mark(A, 0, 8, 'semantic')]);
    assert.equal(s.marked('ne-rep-literal').length, 0);
    assert.deepEqual(s.marked('ne-rep-semantic').map(el => el.textContent), ['XYErster']);

    // They are kept for the next document, at the same offsets.
    s.api.setDocument(docJson(), false, false);
    assert.deepEqual(s.marked('ne-rep-semantic').map(el => el.textContent), ['Erster T']);
    s.done();
});

test('setting marks adds nothing to the undo history and reports no change', () => {
    const s = setUp();
    s.editor.commands.setTextSelection(4);
    const selection = s.editor.state.selection.toJSON();
    const changes = s.callsOf('DocumentChanged').length;

    s.api.setRepetitionMarks([mark(A, 0, 6, 'literal'), mark(B, 0, 7, 'current')]);

    assert.equal(undoDepth(s.editor.state), 0);
    assert.equal(s.callsOf('DocumentChanged').length, changes);
    assert.equal(s.api.takePendingDocument(), null);
    assert.deepEqual(s.editor.state.selection.toJSON(), selection);

    // An undo after a text edit reverts only the text, not the marks set after it.
    s.editor.commands.insertContentAt(4, 'X');
    s.api.setRepetitionMarks([mark(B, 0, 7, 'literal')]);
    assert.equal(undoDepth(s.editor.state), 1);
    assert.equal(s.editor.commands.undo(), true);
    assert.equal(s.editor.state.doc.child(0).child(0).textContent, textA);
    assert.deepEqual(s.marked('ne-rep-literal').map(el => el.textContent), ['Zweiter']);
    assert.equal(s.editor.commands.undo(), false);
    s.done();
});
