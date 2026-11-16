// While something is dragged (jsdom): the editor scrolls when the pointer is near its top or bottom edge, and the
// document is marked as dragging (the section bar lets drops through). Run with `npm test`.
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
globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
window.ResizeObserver = globalThis.ResizeObserver;
const noRects = () => ({ length: 0, item: () => null, [Symbol.iterator]: [][Symbol.iterator] });
const zeroRect = () => ({ top: 0, bottom: 0, left: 0, right: 0, width: 0, height: 0, x: 0, y: 0 });
window.Range.prototype.getClientRects = noRects;
window.Range.prototype.getBoundingClientRect = zeroRect;
window.document.elementFromPoint = () => null;
Object.defineProperty(window, 'innerHeight', { value: 800, configurable: true });
const { createEditor, dragScrollSpeed } = await import('../editor.js');

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

/** An editor in a scrolling pane that shows 100–600 on the screen, 0–400 across. */
function open() {
    document.body.replaceChildren();
    const scroller = document.createElement('div');
    scroller.style.overflowY = 'auto';
    scroller.getBoundingClientRect = () => ({ top: 100, bottom: 600, left: 0, right: 400, width: 400, height: 500, x: 0, y: 100 });
    let top = 1000;
    Object.defineProperty(scroller, 'scrollTop', { get: () => top, set: value => { top = value; } });
    const host = document.createElement('div');
    scroller.appendChild(host);
    document.body.appendChild(scroller);
    createEditor(host, { invokeMethodAsync: () => Promise.resolve(null) });
    return scroller;
}

function fire(type, x = 0, y = 0, buttons = 1) {
    const event = new window.Event(type, { bubbles: true, cancelable: true });
    Object.assign(event, { clientX: x, clientY: y, buttons });
    document.body.dispatchEvent(event);
}

test('speed: none in the middle and outside, faster towards the edge', () => {
    assert.equal(dragScrollSpeed(350, 100, 600), 0);
    assert.equal(dragScrollSpeed(160, 100, 600), 0);
    assert.equal(dragScrollSpeed(540, 100, 600), 0);
    assert.equal(dragScrollSpeed(99, 100, 600), 0);
    assert.equal(dragScrollSpeed(601, 100, 600), 0);
    assert.equal(dragScrollSpeed(100, 100, 600), -1200);
    assert.equal(dragScrollSpeed(600, 100, 600), 1200);
    assert.equal(dragScrollSpeed(130, 100, 600), -720);
    assert.equal(dragScrollSpeed(570, 100, 600), 720);
    assert.ok(dragScrollSpeed(159, 100, 600) < 0 && dragScrollSpeed(159, 100, 600) > -260);
});

test('speed: in a low pane each zone is at most a third of it', () => {
    assert.equal(dragScrollSpeed(145, 100, 190), 0);
    assert.ok(dragScrollSpeed(110, 100, 190) < 0);
    assert.ok(dragScrollSpeed(180, 100, 190) > 0);
});

test('dragging near the bottom scrolls down until the pointer leaves the zone', async () => {
    const scroller = open();
    fire('dragover', 200, 590);
    await wait(120);
    const after = scroller.scrollTop;
    assert.ok(after > 1000, `scrolled down: ${after}`);
    fire('dragover', 200, 350);
    await wait(60);
    const rest = scroller.scrollTop;
    await wait(100);
    assert.equal(scroller.scrollTop, rest);
});

test('dragging near the top scrolls up, and the drop ends it', async () => {
    const scroller = open();
    scroller.scrollTop = 1000;
    fire('dragover', 200, 105);
    await wait(120);
    assert.ok(scroller.scrollTop < 1000, `scrolled up: ${scroller.scrollTop}`);
    fire('drop', 200, 105);
    await wait(40);
    const rest = scroller.scrollTop;
    await wait(100);
    assert.equal(scroller.scrollTop, rest);
});

test('a pointer beside the pane scrolls nothing', async () => {
    const scroller = open();
    scroller.scrollTop = 1000;
    fire('dragover', 500, 590);
    await wait(100);
    assert.equal(scroller.scrollTop, 1000);
    fire('dragend');
});

test('without further drag events the scrolling stops by itself', async () => {
    const scroller = open();
    scroller.scrollTop = 1000;
    fire('dragover', 200, 590);
    await wait(500);
    assert.ok(scroller.scrollTop > 1200, `still scrolling with a resting pointer: ${scroller.scrollTop}`);
    await wait(800);
    const rest = scroller.scrollTop;
    await wait(150);
    assert.equal(scroller.scrollTop, rest);
});

test('the document is marked while something is dragged', async () => {
    open();
    const root = document.documentElement;
    fire('dragstart');
    await wait(20);
    assert.ok(root.classList.contains('ne-dragging'));
    fire('dragend');
    assert.ok(!root.classList.contains('ne-dragging'));
    fire('dragstart');
    await wait(20);
    fire('drop');
    assert.ok(!root.classList.contains('ne-dragging'));
    fire('dragstart');
    await wait(20);
    fire('mousemove', 0, 0, 0);
    assert.ok(!root.classList.contains('ne-dragging'));
});
