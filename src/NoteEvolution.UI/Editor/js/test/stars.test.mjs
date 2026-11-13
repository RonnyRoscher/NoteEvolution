// Bold and italic show their Markdown stars in the paragraph with the cursor (jsdom): ** and * around the text, only
// while the editor has the focus; Backspace after the opening stars or Delete before the closing ones removes the
// formatting; toggleMark sets and removes it for the buttons of the section bar. Run with `npm test`.
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
const B = '92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081';
const D = 'a3d2c4e5-7e40-4b1d-9f6c-4d5e6f708192';

const text = (value, ...marks) => (marks.length
    ? { type: 'text', text: value, marks: marks.map(type => ({ type })) }
    : { type: 'text', text: value });
const para = (key, depth, ...content) => ({ type: 'para', attrs: { key, depth }, content });
const block = (key, ...paras) => ({ type: 'textBlock', attrs: { key }, content: paras });

/** Block A: "ab FETT cd" with a sub-point D "x KURSIV y"; block B: "vor BEIDES nach" (bold and italic). */
function open() {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const dotnet = { invokeMethodAsync: () => Promise.resolve() };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({
        type: 'doc',
        content: [
            block(A, para(null, 0, text('ab '), text('FETT', 'bold'), text(' cd')),
                para(D, 1, text('x '), text('KURSIV', 'italic'), text(' y'))),
            block(B, para(null, 0, text('vor '), text('BEIDES', 'bold', 'italic'), text(' nach'))),
        ],
    }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const focus = () => editor.view.dom.dispatchEvent(new window.FocusEvent('focus'));
    const blur = () => editor.view.dom.dispatchEvent(new window.FocusEvent('blur'));
    /** The paragraphs as shown, stars included, e.g. "ab **FETT** cd". */
    const shown = () => [...host.querySelectorAll('.ne-para')].map(p => p.textContent);
    const key = name =>
        editor.view.dom.dispatchEvent(new window.KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }));
    /** The marks of every text run of the document, e.g. "FETT:bold". */
    const marks = () => {
        const result = [];
        editor.state.doc.descendants(node => {
            if (node.isText && node.marks.length > 0) {
                result.push(node.text + ':' + node.marks.map(m => m.type.name).sort().join('+'));
            }
        });
        return result;
    };
    const close = () => {
        api.destroy();
        host.remove();
    };
    return { api, editor, focus, blur, shown, key, marks, close };
}

test('The paragraph with the cursor shows the stars, the others do not', () => {
    const { api, focus, shown, close } = open();
    focus();

    api.reveal(A, 1, false);
    assert.deepEqual(shown(), ['ab **FETT** cd', 'x KURSIV y', 'vor BEIDES nach']);

    api.reveal(D, 1, false);
    assert.deepEqual(shown(), ['ab FETT cd', 'x *KURSIV* y', 'vor BEIDES nach']);

    api.reveal(B, 1, false);
    assert.deepEqual(shown(), ['ab FETT cd', 'x KURSIV y', 'vor ***BEIDES*** nach']);
    close();
});

test('Without the focus no stars are shown', () => {
    const { api, focus, blur, shown, close } = open();
    api.reveal(A, 1, false);
    assert.deepEqual(shown(), ['ab FETT cd', 'x KURSIV y', 'vor BEIDES nach']);

    focus();
    assert.equal(shown()[0], 'ab **FETT** cd');
    blur();
    assert.equal(shown()[0], 'ab FETT cd');
    close();
});

test('The stars are no text of the document', () => {
    const { api, editor, focus, close } = open();
    focus();
    api.reveal(A, 1, false);

    assert.equal(editor.state.doc.firstChild.firstChild.textContent, 'ab FETT cd');
    close();
});

test('Backspace right after the opening stars removes the formatting', () => {
    const { api, focus, shown, key, marks, close } = open();
    focus();
    api.reveal(A, 3, false);

    key('Backspace');

    assert.deepEqual(marks(), ['KURSIV:italic', 'BEIDES:bold+italic']);
    assert.equal(shown()[0], 'ab FETT cd');
    close();
});

test('Delete right before the closing stars removes the formatting', () => {
    const { api, focus, key, marks, close } = open();
    focus();
    api.reveal(D, 8, false);

    key('Delete');

    assert.deepEqual(marks(), ['FETT:bold', 'BEIDES:bold+italic']);
    close();
});

test('Bold and italic together: italic goes first, then bold', () => {
    const { api, focus, shown, key, marks, close } = open();
    focus();
    api.reveal(B, 4, false);

    key('Backspace');
    assert.deepEqual(marks(), ['FETT:bold', 'KURSIV:italic', 'BEIDES:bold']);
    assert.equal(shown()[2], 'vor **BEIDES** nach');

    key('Backspace');
    assert.deepEqual(marks(), ['FETT:bold', 'KURSIV:italic']);
    close();
});

test('Backspace elsewhere deletes a character as before', () => {
    const { api, editor, focus, key, marks, close } = open();
    focus();
    api.reveal(A, 5, false);

    key('Backspace');

    // jsdom has no native editing: the key is not handled here, so the marks stay.
    assert.deepEqual(marks(), ['FETT:bold', 'KURSIV:italic', 'BEIDES:bold+italic']);
    assert.equal(editor.state.doc.firstChild.firstChild.textContent, 'ab FETT cd');
    close();
});

test('toggleMark sets and removes bold and italic on the selected text', () => {
    const { api, editor, marks, close } = open();
    // "cd" at the end of the first paragraph: positions 2 + 8 .. 2 + 10.
    editor.commands.setTextSelection({ from: 10, to: 12 });

    api.toggleMark('bold');
    assert.deepEqual(marks(), ['FETT:bold', 'cd:bold', 'KURSIV:italic', 'BEIDES:bold+italic']);

    api.toggleMark('italic');
    assert.ok(marks().includes('cd:bold+italic'));

    api.toggleMark('bold');
    api.toggleMark('italic');
    assert.deepEqual(marks(), ['FETT:bold', 'KURSIV:italic', 'BEIDES:bold+italic']);
    close();
});

test('toggleMark on a selection across paragraphs formats all of it; an unknown mark does nothing', () => {
    const { api, editor, marks, close } = open();
    editor.commands.setTextSelection({ from: 2, to: 16 });

    api.toggleMark('bold');
    assert.deepEqual(marks(), ['ab FETT cd:bold', 'x :bold', 'KURSIV:italic', 'BEIDES:bold+italic']);

    api.toggleMark('underline');
    assert.deepEqual(marks(), ['ab FETT cd:bold', 'x :bold', 'KURSIV:italic', 'BEIDES:bold+italic']);
    close();
});
