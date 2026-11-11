// Enter and paste in a text block (jsdom): a part split off a sub-bullet or off the block's own text, or a pasted
// sub-bullet, remembers where it came from (splitFrom), so that C# links it to the same notes. Run with `npm test`.
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

const BLOCK = '6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70';
const FIRST = '92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081';
const SECOND = 'a3d2c4e5-7e40-4b1d-9f6c-4d5e6f708192';

function para(key, depth, text) {
    return { type: 'para', attrs: { key, depth }, content: [{ type: 'text', text }] };
}

function open() {
    const host = document.createElement('div');
    document.body.appendChild(host);
    const dotnet = { invokeMethodAsync: () => Promise.resolve() };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    api.setDocument(JSON.stringify({
        type: 'doc',
        content: [{ type: 'textBlock', attrs: { key: BLOCK }, content: [para(null, 0, 'Text'), para(FIRST, 1, 'Erster'), para(SECOND, 1, 'Zweiter')] }],
    }), false, false);
    const editor = host.querySelector('.ne-doc').editor;
    const enter = () =>
        editor.view.dom.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    /** [text, key (a new one as "new"), splitFrom, depth] of every paragraph of the block. */
    const paras = () => {
        const known = [BLOCK, FIRST, SECOND, null];
        const result = [];
        editor.state.doc.firstChild.forEach(p => result.push([
            p.textContent, known.includes(p.attrs.key) ? p.attrs.key : 'new', p.attrs.splitFrom ?? null, p.attrs.depth]));
        return result;
    };
    const close = () => {
        api.destroy();
        host.remove();
    };
    return { api, editor, enter, paras, close };
}

test('Enter inside a sub-bullet: the new part comes from it', () => {
    const { api, enter, paras, close } = open();
    api.reveal(FIRST, 3, false);
    enter();
    assert.deepEqual(paras(), [
        ['Text', null, null, 0], ['Ers', FIRST, null, 1], ['ter', 'new', FIRST, 1], ['Zweiter', SECOND, null, 1]]);
    close();
});

test('Enter inside the text of the block: the new sub-bullet comes from the text block', () => {
    const { api, enter, paras, close } = open();
    api.reveal(BLOCK, 2, false);
    enter();
    assert.deepEqual(paras(), [
        ['Te', null, null, 0], ['xt', 'new', BLOCK, 1], ['Erster', FIRST, null, 1], ['Zweiter', SECOND, null, 1]]);
    close();
});

test('Enter at the end of a sub-bullet: an empty one without origin', () => {
    const { api, enter, paras, close } = open();
    api.reveal(FIRST, 6, false);
    enter();
    assert.deepEqual(paras(), [
        ['Text', null, null, 0], ['Erster', FIRST, null, 1], ['', 'new', null, 1], ['Zweiter', SECOND, null, 1]]);
    close();
});

test('Enter at the start of a sub-bullet: an empty one before it, the text keeps its key', () => {
    const { api, editor, enter, paras, close } = open();
    api.reveal(SECOND, 0, false);
    enter();
    assert.deepEqual(paras(), [
        ['Text', null, null, 0], ['Erster', FIRST, null, 1], ['', 'new', null, 1], ['Zweiter', SECOND, null, 1]]);
    assert.equal(editor.state.selection.$from.parent.textContent, 'Zweiter');
    assert.equal(editor.state.selection.$from.parentOffset, 0);
    close();
});

test('A copy of a sub-bullet gets a new key and comes from the original', () => {
    const { editor, paras, close } = open();
    const block = editor.state.doc.firstChild;
    const copy = block.child(1);
    editor.view.dispatch(editor.state.tr.insert(1 + block.content.size, copy));
    assert.deepEqual(paras(), [
        ['Text', null, null, 0], ['Erster', FIRST, null, 1], ['Zweiter', SECOND, null, 1], ['Erster', 'new', FIRST, 1]]);
    close();
});
