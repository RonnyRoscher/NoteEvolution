// Cross references in a DOM (jsdom): the blockRef node shows its label, tooltip and missing state, is deleted whole and
// reports a click to .NET; it counts as one position of the text. Run with `npm test` in the folder above.
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
const { createEditor } = await import('../editor.js');
const { undoDepth } = await import('@tiptap/pm/history');

const A = '6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70';
const T = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const M = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';

const ref = (id, label, title, missing, marks) => ({
    type: 'blockRef', attrs: { id, label, title, missing }, ...(marks ? { marks } : {}),
});

/** An editor with one text block "Siehe [ref T] und [ref M, missing]" (the first reference bold); calls holds what it reported. */
function setUp() {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push({ method, args }); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({
        type: 'doc',
        content: [{
            type: 'textBlock',
            attrs: { key: A },
            content: [{
                type: 'para',
                attrs: { key: null, md: `Siehe **((${T}))** und ((${M}))` },
                content: [
                    { type: 'text', text: 'Siehe ' },
                    ref(T, '→ Eins', 'Buch › Eins', false, [{ type: 'bold' }]),
                    { type: 'text', text: ' und ' },
                    ref(M, '→ Verweis fehlt', 'Das Ziel fehlt', true),
                ],
            }],
        }],
    }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const para = () => editor.state.doc.child(0).child(0);
    const key = name => editor.view.dom.dispatchEvent(new window.KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }));
    return { host, api, editor, calls, para, key, done: () => { api.destroy(); host.remove(); } };
}

test('a reference shows its label and tooltip; a missing one is marked', () => {
    const s = setUp();
    const refs = s.host.querySelectorAll('.ne-ref');

    assert.equal(refs.length, 2);
    assert.equal(refs[0].textContent, '→ Eins');
    assert.equal(refs[0].title, 'Buch › Eins');
    assert.equal(refs[0].classList.contains('missing'), false);
    assert.equal(refs[0].contentEditable, 'false');
    assert.ok(refs[0].closest('strong'), 'the bold mark wraps the reference');
    assert.equal(refs[1].textContent, '→ Verweis fehlt');
    assert.equal(refs[1].title, 'Das Ziel fehlt');
    assert.equal(refs[1].classList.contains('missing'), true);
    s.done();
});

test('the document goes back with the reference node and its id; it is one position of the text', () => {
    const s = setUp();
    const content = s.editor.getJSON().content[0].content[0].content;

    assert.deepEqual(content.map(n => n.type), ['text', 'blockRef', 'text', 'blockRef']);
    assert.equal(content[1].attrs.id, T);
    assert.deepEqual(content[1].marks, [{ type: 'bold' }]);
    // "Siehe " (6) + reference (1) + " und " (5) + reference (1).
    assert.equal(s.para().content.size, 13);

    s.api.reveal(A, 7);
    const { $from } = s.editor.state.selection;
    assert.equal($from.parentOffset, 7);
    assert.equal($from.nodeBefore.type.name, 'blockRef');
    s.done();
});

test('Backspace after a reference and Delete before it remove it whole', () => {
    const s = setUp();
    // Right after the first reference (paragraph start 2 + 7).
    s.editor.commands.setTextSelection(9);

    s.key('Backspace');

    assert.equal(s.para().textContent, 'Siehe  und ');
    assert.equal(s.para().childCount, 2);
    assert.equal(s.editor.state.selection.from, 8);

    // Right before the remaining (missing) reference: "Siehe  und " is 11 positions.
    s.editor.commands.setTextSelection(13);
    s.key('Delete');

    assert.equal(s.para().textContent, 'Siehe  und ');
    let refs = 0;
    s.para().forEach(n => { refs += n.type.name === 'blockRef' ? 1 : 0; });
    assert.equal(refs, 0);
    assert.equal(s.host.querySelectorAll('.ne-ref').length, 0);
    s.done();
});

test('a click on a reference reports its id', () => {
    const s = setUp();

    s.host.querySelectorAll('.ne-ref')[1].dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));

    assert.deepEqual(s.calls.filter(c => c.method === 'ReferenceClicked').map(c => c.args), [[M]]);
    s.done();
});

test('setRefLabels updates changed labels in place, without history and without a change report', () => {
    const s = setUp();
    s.editor.commands.setTextSelection(4);
    const selection = s.editor.state.selection.toJSON();

    s.api.setRefLabels([
        { id: T.toUpperCase(), label: '→ Eins neu', title: 'Buch › Eins neu', missing: false },
        { id: M, label: '→ Verweis fehlt', title: 'Das Ziel fehlt', missing: true },
    ]);

    const refs = s.host.querySelectorAll('.ne-ref');
    assert.equal(refs[0].textContent, '→ Eins neu');
    assert.equal(refs[0].title, 'Buch › Eins neu');
    assert.equal(refs[1].textContent, '→ Verweis fehlt');
    assert.equal(undoDepth(s.editor.state), 0);
    assert.equal(s.calls.filter(c => c.method === 'DocumentChanged').length, 0);
    assert.deepEqual(s.editor.state.selection.toJSON(), selection);

    // A deleted target: the reference turns missing.
    s.api.setRefLabels([{ id: T, label: '→ Verweis fehlt', title: 'Das Ziel fehlt', missing: true }]);

    assert.equal(s.host.querySelectorAll('.ne-ref')[0].classList.contains('missing'), true);
    assert.equal(s.editor.state.doc.child(0).child(0).child(1).attrs.missing, true);
    s.done();
});

test('setRefLabels turns a typed reference into a node, keeps the cursor and the marks; not while the cursor is inside', () => {
    const s = setUp();
    const typed = `((${A}))`;
    // Paragraph start 2 + 13 positions: the end of the paragraph.
    s.editor.commands.insertContentAt(15, { type: 'text', text: ` ${typed} x`, marks: [{ type: 'italic' }] });
    const depth = undoDepth(s.editor.state);
    const changes = s.calls.filter(c => c.method === 'DocumentChanged').length;
    const end = s.editor.state.doc.child(0).child(0).content.size + 2;
    const labels = [{ id: A, label: '→ Zwei', title: 'Buch › Zwei', missing: false }];

    // The cursor inside the typed reference: it stays text.
    s.editor.commands.setTextSelection(20);
    s.api.setRefLabels(labels);
    assert.equal(s.host.querySelectorAll('.ne-ref').length, 2);

    s.editor.commands.setTextSelection(end);
    s.api.setRefLabels(labels);

    const content = s.editor.getJSON().content[0].content[0].content;
    assert.deepEqual(content.map(n => n.type), ['text', 'blockRef', 'text', 'blockRef', 'text', 'blockRef', 'text']);
    assert.deepEqual({ ...content[5].attrs }, { id: A, label: '→ Zwei', title: 'Buch › Zwei', missing: false, foreign: false });
    assert.deepEqual(content[5].marks, [{ type: 'italic' }]);
    assert.equal(s.host.querySelectorAll('.ne-ref')[2].textContent, '→ Zwei');
    // The cursor stays at the end: the 40 characters became one position.
    assert.equal(s.editor.state.selection.from, end - typed.length + 1);
    assert.equal(undoDepth(s.editor.state), depth);
    assert.equal(s.calls.filter(c => c.method === 'DocumentChanged').length, changes);
    s.done();
});

test('a foreign reference shows its reference text like text, goes nowhere and is one position; a typed one becomes a node', () => {
    const s = setUp();
    const N = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
    const typed = `((${N}))`;
    // Paragraph start 2 + 13 positions: the end of the paragraph.
    s.editor.commands.insertContentAt(15, { type: 'text', text: ` ${typed}` });
    const end = s.editor.state.doc.child(0).child(0).content.size + 2;
    s.editor.commands.setTextSelection(end);

    s.api.setRefLabels([{ id: N, label: typed, title: '', missing: false, foreign: true }]);

    const content = s.editor.getJSON().content[0].content[0].content;
    assert.deepEqual(content.map(n => n.type), ['text', 'blockRef', 'text', 'blockRef', 'text', 'blockRef']);
    assert.equal(content[5].attrs.foreign, true);
    assert.equal(s.para().content.size, 15);
    const dom = s.host.querySelectorAll('.ne-ref')[2];
    assert.equal(dom.textContent, typed);
    assert.equal(dom.classList.contains('foreign'), true);
    dom.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
    assert.deepEqual(s.calls.filter(c => c.method === 'ReferenceClicked'), []);
    s.done();
});
