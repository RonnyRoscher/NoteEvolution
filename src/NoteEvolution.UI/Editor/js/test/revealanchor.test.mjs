// reveal in a DOM (jsdom) with a faked layout: the marking's gap for the section bar (ne-current-last) above a revealed
// block must be gone before the reveal scrolls, which is why EditorPane sends the marking first. Run with `npm test` in the folder above.
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
globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
window.ResizeObserver = globalThis.ResizeObserver;
const noRects = () => ({ length: 0, item: () => null, [Symbol.iterator]: [][Symbol.iterator] });
const rect = (top, height) => ({ top, bottom: top + height, left: 0, right: 0, width: 0, height, x: 0, y: top });
window.Range.prototype.getClientRects = noRects;
window.Range.prototype.getBoundingClientRect = () => rect(0, 0);
window.document.elementFromPoint = () => null;
const { createEditor } = await import('../editor.js');

// The layout: the scroller shows client 0..BLOCK; each block of the document is BLOCK px high, plus GAP below the
// end of the marking (ne-current-last), as app.css has it with the section bar's height.
const BLOCK = 100;
const GAP = 40;
const scroller = document.createElement('div');
scroller.style.overflowY = 'auto';
let scrollTop = 0;
Object.defineProperty(scroller, 'scrollTop', { get: () => scrollTop, set: value => { scrollTop = Math.max(0, value); } });
scroller.getBoundingClientRect = () => rect(0, BLOCK);
document.body.appendChild(scroller);
const host = document.createElement('div');
scroller.appendChild(host);

window.Element.prototype.getBoundingClientRect = function () {
    const blocks = this.parentElement?.classList.contains('ProseMirror') ? [...this.parentElement.children] : null;
    if (!blocks) {
        return rect(0, 0);
    }
    let top = -scrollTop;
    for (const block of blocks.slice(0, blocks.indexOf(this))) {
        top += BLOCK + (block.classList.contains('ne-current-last') ? GAP : 0);
    }
    return rect(top, BLOCK);
};
window.Element.prototype.scrollIntoView = function () {
    scrollTop += this.getBoundingClientRect().top;
};

const keys = ['6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70', '92c1b3d4-6d3f-4a0c-8e5b-3c4d5e6f7081',
    'a3d2c4e5-7e40-4b1d-9f6c-4d5e6f708192', 'b4e3d5f6-8f51-4c2e-a07d-5e6f708192a3'];
const block = (key, text) => ({ type: 'textBlock', attrs: { key }, content: [{ type: 'para', attrs: { key: null }, content: [{ type: 'text', text }] }] });

test('a block revealed after its marking is in place stays at the top; marked after the reveal it slides up', () => {
    const dotnet = { invokeMethodAsync: () => Promise.resolve() };
    const api = createEditor(host, dotnet, { barHeight: GAP });
    api.setDocument(JSON.stringify({ type: 'doc', content: keys.map((key, i) => block(key, `Text ${i}`)) }), false, false);
    const blockDom = index => host.querySelector('.ProseMirror').children[index];
    const top = index => blockDom(index).getBoundingClientRect().top;

    // The cursor was in the first block: its marking has the gap below it.
    api.setMarked([{ key: keys[0], ownTextOnly: false }]);
    assert.equal(top(2), 2 * BLOCK + GAP);

    // As EditorPane does it: the new marking first (the gap above goes away), then the reveal scrolls to the block.
    api.setMarked([{ key: keys[2], ownTextOnly: false }]);
    api.reveal(keys[2]);
    assert.equal(top(2), 0);

    // The other order (the bug): the gap above the revealed block disappears after the scroll and pushes it up.
    api.reveal(keys[3]);
    assert.equal(top(3), 0);
    api.setMarked([{ key: keys[3], ownTextOnly: false }]);
    assert.equal(top(3), -GAP);

    api.destroy();
});
