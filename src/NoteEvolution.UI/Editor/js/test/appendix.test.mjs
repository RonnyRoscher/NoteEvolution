// The appendix in a DOM (jsdom): people and source citations are superscript units that report clicks, a double click
// on a citation asks to change its locator, and glossary marks are decorations at the offsets C# gives. Run with
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
const { createEditor } = await import('../editor.js');

const A = '6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70';
const D = '7750a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f71';
const P = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const S = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const G = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';

const tick = () => new Promise(resolve => setTimeout(resolve, 5));

/**
 * An editor with a text block "Laut [person P] steht es[cite S, S. 12] so" and a detail "Achtsamkeit hilft"; calls
 * holds what it reported. A click on a citation waits 0 ms for a double click.
 */
function setUp() {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = { invokeMethodAsync: (method, ...args) => { calls.push({ method, args }); return Promise.resolve(); } };
    const api = createEditor(host, dotnet, { barHeight: 0, citeClickDelay: 0 });
    api.setDocument(JSON.stringify({
        type: 'doc',
        content: [{
            type: 'textBlock',
            attrs: { key: A },
            content: [
                {
                    type: 'para',
                    attrs: { key: null, md: `Laut ((${P})) steht es{{quelle ((${S})) S. 12}} so` },
                    content: [
                        { type: 'text', text: 'Laut ' },
                        { type: 'personRef', attrs: { id: P, label: 'AB', title: 'Anna Berg — Achtsamkeit', missing: false } },
                        { type: 'text', text: ' steht es' },
                        { type: 'sourceCite', attrs: { id: S, locator: 'S. 12', label: '[1, S. 12]', title: 'Anna Berg: Achtsam leben', missing: false } },
                        { type: 'text', text: ' so' },
                    ],
                },
                { type: 'para', attrs: { key: D, depth: 1, md: 'Achtsamkeit hilft' }, content: [{ type: 'text', text: 'Achtsamkeit hilft' }] },
            ],
        }],
    }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const click = (element, init = {}) => element.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true, ...init }));
    return { host, api, editor, calls, click, done: () => { api.destroy(); host.remove(); } };
}

test('personRef und sourceCite rendern hochgestellt, Klick meldet', async () => {
    const s = setUp();
    const person = s.host.querySelector('.ne-person-ref');
    const cite = s.host.querySelector('.ne-source-cite');

    assert.equal(person.tagName, 'SUP');
    assert.equal(person.textContent, 'AB');
    assert.equal(person.title, 'Anna Berg — Achtsamkeit');
    assert.equal(person.contentEditable, 'false');
    assert.equal(cite.tagName, 'SUP');
    assert.equal(cite.textContent, '[1, S. 12]');
    assert.equal(cite.title, 'Anna Berg: Achtsam leben');
    // Each is one position of the text: "Laut " (5) + 1 + " steht es" (9) + 1 + " so" (3).
    assert.equal(s.editor.state.doc.child(0).child(0).content.size, 19);
    assert.equal(s.editor.state.doc.child(0).child(0).textBetween(0, 19, null, n => n.type.name === 'sourceCite'
        ? `{{quelle ((${n.attrs.id})) ${n.attrs.locator}}}` : `((${n.attrs.id}))`), `Laut ((${P})) steht es{{quelle ((${S})) S. 12}} so`);

    s.click(person);
    s.click(cite);
    await tick();

    assert.deepEqual(s.calls.filter(c => c.method === 'PersonClicked').map(c => c.args), [[P]]);
    assert.deepEqual(s.calls.filter(c => c.method === 'SourceClicked').map(c => c.args), [[S]]);

    // New labels: the number changes, a reference typed as text to a person becomes a person node.
    s.editor.commands.insertContentAt(21, ` ((${P}))`);
    s.api.setAppendixLabels(
        [{ id: P, label: 'AB', title: 'Anna Berg', missing: false }],
        [{ id: S, label: '2', title: 'Achtsam leben', missing: false }],
        []);
    assert.equal(s.host.querySelector('.ne-source-cite').textContent, '[2, S. 12]');
    assert.equal(s.host.querySelectorAll('.ne-person-ref').length, 2);
    assert.deepEqual([...s.host.querySelectorAll('.ne-person-ref')].map(e => e.title), ['Anna Berg', 'Anna Berg']);

    // A person who is gone: the cross reference labels make it a missing reference.
    s.api.setRefLabels([{ id: P, label: '→ Verweis fehlt', title: 'fehlt', missing: true }]);
    assert.equal(s.host.querySelectorAll('.ne-person-ref').length, 0);
    assert.equal(s.host.querySelectorAll('.ne-ref.missing').length, 2);
    s.done();
});

test('Glossar-Decoration an Offset, Strg+Klick meldet', () => {
    const s = setUp();

    // "steht" in the block's own text (offset 7, after "Laut " and the person) and "Achtsamkeit" in the detail.
    s.api.setAppendixLabels([], [], [
        { key: A, start: 7, length: 5, title: 'steht — Erklärung', entryKey: G },
        { key: D, start: 0, length: 11, title: 'Achtsamkeit — Bewusst', entryKey: G },
        { key: D, start: 10, length: 50, title: 'zu lang', entryKey: G },
        { key: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd', start: 0, length: 3, title: 'fremd', entryKey: G },
    ]);

    const marks = [...s.host.querySelectorAll('.ne-glossary-mark')];
    assert.deepEqual(marks.map(m => m.textContent), ['steht', 'Achtsamkeit']);
    assert.equal(marks[0].title, 'steht — Erklärung');
    assert.equal(marks[1].getAttribute('data-glossary'), G);

    // A plain click does nothing, Ctrl+click reports the entry.
    s.click(marks[1]);
    assert.equal(s.calls.filter(c => c.method === 'GlossaryClicked').length, 0);
    s.click(marks[1], { ctrlKey: true });
    assert.deepEqual(s.calls.filter(c => c.method === 'GlossaryClicked').map(c => c.args), [[G]]);

    // The marks stay for a new document of the same book, nothing is reported as an edit.
    assert.equal(s.calls.filter(c => c.method === 'DocumentChanged').length, 0);
    s.done();
});

test('Doppelklick auf sourceCite meldet CitationEdit', async () => {
    const s = setUp();
    const cite = s.host.querySelector('.ne-source-cite');

    s.click(cite);
    s.click(cite);
    cite.dispatchEvent(new window.MouseEvent('dblclick', { bubbles: true, cancelable: true }));
    await tick();

    // The citation is at offset 15 of the text block's own text; the clicks of the double click open nothing.
    assert.deepEqual(s.calls.filter(c => c.method === 'CitationEdit').map(c => c.args), [[S, A, 15]]);
    assert.equal(s.calls.filter(c => c.method === 'SourceClicked').length, 0);
    s.done();
});
