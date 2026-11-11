// Images in a DOM (jsdom): an image node shows the picture C# loads (cached by path) with its caption, an error text
// instead where there is none, a double click asks to change the caption, and a pasted image is handed to C#.
// Run with `npm test` in the folder above.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';

const dom = new JSDOM('<!doctype html><html><body></body></html>', { pretendToBeVisual: true });
const { window } = dom;
for (const name of ['window', 'document', 'navigator', 'Node', 'Element', 'HTMLElement', 'HTMLInputElement',
    'HTMLTextAreaElement', 'HTMLSelectElement', 'MutationObserver', 'getComputedStyle', 'requestAnimationFrame',
    'cancelAnimationFrame', 'DOMParser', 'getSelection', 'KeyboardEvent', 'FileReader', 'Blob']) {
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
const { createEditor, clearImageCache } = await import('../editor.js');

const A = '6650a1c2-1b7e-4c1d-9a0f-2b3c4d5e6f70';

const tick = () => new Promise(resolve => setTimeout(resolve, 5));

const image = (path, alt = '', extra = {}) => ({
    type: 'image',
    attrs: { path, alt, suffix: '', remote: false, error: null, ...extra },
});

/** The document of setUp with another caption on its image: shown, it makes a new node view. */
const another = doc => JSON.parse(JSON.stringify(doc).replace('"alt":""', '"alt":"neu"'));

/**
 * An editor with the text block A "Vor [image] nach"; calls holds what it reported. LoadImage answers with
 * load(path) (default: a data URL named after the path).
 */
function setUp(img, load = path => ({ src: `data:image/png;base64,${path}`, error: null }), keepCache = false) {
    if (!keepCache) {
        clearImageCache();
    }
    const host = document.createElement('div');
    document.body.appendChild(host);
    const calls = [];
    const dotnet = {
        invokeMethodAsync: (method, ...args) => {
            calls.push({ method, args });
            return Promise.resolve(method === 'LoadImage' ? load(args[0]) : undefined);
        },
    };
    const api = createEditor(host, dotnet, { barHeight: 0 });
    const show = document => api.setDocument(JSON.stringify(document), false, false);
    const doc = {
        type: 'doc',
        content: [{
            type: 'textBlock',
            attrs: { key: A },
            content: [{
                type: 'para',
                attrs: { key: null, md: '' },
                content: [{ type: 'text', text: 'Vor ' }, img, { type: 'text', text: ' nach' }],
            }],
        }],
    };
    show(doc);
    return {
        host,
        calls,
        doc,
        show,
        editor: host.querySelector('.ne-doc').editor,
        loads: () => calls.filter(c => c.method === 'LoadImage').map(c => c.args),
        done: () => {
            api.destroy();
            host.remove();
        },
    };
}

test('Ein Bild-Knoten rendert figure.ne-image im Block, LoadImage einmal, src aus dem Ergebnis', async () => {
    const s = setUp(image('../assets/a.png', 'Titel'));
    await tick();

    const figure = s.host.querySelector('.ne-doc figure.ne-image');
    assert.ok(figure);
    assert.equal(figure.closest('[data-key]').getAttribute('data-key'), A);
    assert.equal(figure.querySelector('img').getAttribute('src'), 'data:image/png;base64,../assets/a.png');
    assert.deepEqual(s.loads(), [['../assets/a.png']]);
    s.done();
});

test('Derselbe Pfad wird nicht erneut geladen (Cache pro Pfad)', async () => {
    const s = setUp(image('../assets/a.png'));
    await tick();
    s.show(s.doc);
    await tick();

    assert.equal(s.host.querySelectorAll('figure.ne-image img').length, 1);
    assert.deepEqual(s.loads(), [['../assets/a.png']]);
    s.done();

    const other = setUp(image('../assets/a.png'), undefined, true);
    await tick();
    assert.deepEqual(other.loads(), []);
    assert.equal(other.host.querySelector('figure.ne-image img').getAttribute('src'), 'data:image/png;base64,../assets/a.png');
    other.done();
});

test('Ein Ladefehler zeigt den Text in .ne-image-missing und kein img', async () => {
    const s = setUp(image('p.png'), () => ({ src: null, error: 'Bild nicht gefunden: p' }));
    await tick();

    const missing = s.host.querySelector('figure.ne-image .ne-image-missing');
    assert.equal(missing.textContent, 'Bild nicht gefunden: p');
    assert.equal(s.host.querySelector('.ne-doc img'), null);
    s.done();
});

test('Ein entferntes Bild zeigt seinen Text und ruft LoadImage nie', async () => {
    const s = setUp(image('https://x/y.png', '', { remote: true, error: 'Externes Bild: https://x/y.png' }));
    await tick();

    assert.equal(s.host.querySelector('.ne-image-missing').textContent, 'Externes Bild: https://x/y.png');
    assert.equal(s.host.querySelector('.ne-doc img'), null);
    assert.deepEqual(s.loads(), []);
    s.done();
});

test('Die Beschriftung steht in figcaption, bei leerem alt gibt es keine', async () => {
    const s = setUp(image('a.png', 'Meine Beschriftung'));
    await tick();
    assert.equal(s.host.querySelector('figure.ne-image figcaption').textContent, 'Meine Beschriftung');
    s.done();

    const none = setUp(image('b.png', ''));
    await tick();
    assert.ok(none.host.querySelector('figure.ne-image'));
    assert.equal(none.host.querySelector('figcaption'), null);
    none.done();
});

test('Doppelklick auf das Bild meldet ImageEdit(Blockschluessel, Offset)', async () => {
    const s = setUp(image('a.png', 'x'));
    await tick();

    s.host.querySelector('figure.ne-image').dispatchEvent(new window.MouseEvent('dblclick', { bubbles: true, cancelable: true }));
    await tick();

    // The image is at offset 4 of the block's own text ("Vor ").
    assert.deepEqual(s.calls.filter(c => c.method === 'ImageEdit').map(c => c.args), [[A, 4]]);
    s.done();
});

function paste(view, items, text = '') {
    const event = new window.Event('paste', { bubbles: true, cancelable: true });
    Object.assign(event, { clipboardData: { items, getData: type => (type === 'text/plain' ? text : ''), types: [] } });
    view.dom.dispatchEvent(event);
    return event;
}

test('Ein eingefuegtes Bild meldet ImagePasted(base64, mime), ohne das Dokument zu aendern', async () => {
    const s = setUp(image('a.png'));
    await tick();
    const before = JSON.stringify(s.editor.getJSON());
    s.calls.length = 0;

    const blob = new window.Blob([new Uint8Array([1, 2, 3])], { type: 'image/png' });
    const event = paste(s.editor.view, [{ kind: 'file', type: 'image/png', getAsFile: () => blob }]);
    await new Promise(resolve => setTimeout(resolve, 50));

    assert.ok(event.defaultPrevented);
    assert.deepEqual(s.calls.filter(c => c.method === 'ImagePasted').map(c => c.args), [['AQID', 'image/png']]);
    assert.equal(JSON.stringify(s.editor.getJSON()), before);
    assert.equal(s.calls.filter(c => c.method === 'DocumentChanged').length, 0);
    s.done();
});

test('Ein Einfuegen nur mit Text meldet kein ImagePasted', async () => {
    const s = setUp(image('a.png'));
    await tick();

    const event = paste(s.editor.view, [{ kind: 'string', type: 'text/plain', getAsFile: () => null }], 'abc');
    await tick();

    assert.equal(s.calls.filter(c => c.method === 'ImagePasted').length, 0);
    // ProseMirror's own text paste runs (it prevents the default itself): the text is in the document.
    assert.ok(event.defaultPrevented);
    assert.ok(s.editor.state.doc.textContent.includes('abc'));
    s.done();
});

test('Text und Bild zusammen in der Zwischenablage: der Text wird eingefuegt, kein ImagePasted', async () => {
    const s = setUp(image('a.png'));
    await tick();

    const blob = new window.Blob([new Uint8Array([1])], { type: 'image/png' });
    const event = paste(s.editor.view, [
        { kind: 'string', type: 'text/plain', getAsFile: () => null },
        { kind: 'file', type: 'image/png', getAsFile: () => blob },
    ], 'abc');
    await new Promise(resolve => setTimeout(resolve, 50));

    assert.equal(s.calls.filter(c => c.method === 'ImagePasted').length, 0);
    // ProseMirror's own text paste runs (it prevents the default itself): the text is in the document.
    assert.ok(event.defaultPrevented);
    assert.ok(s.editor.state.doc.textContent.includes('abc'));
    s.done();
});

test('Ein abgelehnter LoadImage-Aufruf zeigt den Fehler und wird beim naechsten Dokument erneut gefragt', async () => {
    const s = setUp(image('p.png'), () => Promise.reject(new Error('boom')));
    await tick();
    assert.equal(s.host.querySelector('.ne-image-missing').textContent, 'boom');

    s.show(another(s.doc));
    await tick();

    assert.deepEqual(s.loads(), [['p.png'], ['p.png']]);
    s.done();
});

test('Ein synchroner Fehler von load wird zum Fehlertext', async () => {
    const s = setUp(image('p.png'), () => {
        throw new Error('sync');
    });
    await tick();

    assert.equal(s.host.querySelector('.ne-image-missing').textContent, 'sync');
    s.done();
});

test('Ein Ergebnis ohne src wird beim naechsten Dokument erneut gefragt, ein Erfolg nicht', async () => {
    const s = setUp(image('p.png'), () => ({ src: null, error: 'fehlt' }));
    await tick();
    s.show(another(s.doc));
    await tick();
    assert.equal(s.loads().length, 2);
    s.done();
});

test('Gleichzeitige Anfragen desselben Pfads teilen sich einen Aufruf', async () => {
    const s = setUp(image('p.png'));
    s.show(s.doc);
    await tick();
    assert.equal(s.loads().length, 1);
    s.done();
});

test('Feindlicher Text in alt, path und error bleibt Text', async () => {
    const hostile = '<img src=x onerror=alert(1)>';
    const s = setUp(image(hostile, hostile));
    await tick();
    const figure = s.host.querySelector('figure.ne-image');
    assert.equal(figure.querySelector('figcaption').textContent, hostile);
    assert.deepEqual([...figure.querySelectorAll('*')].map(e => e.tagName).sort(), ['FIGCAPTION', 'IMG']);
    s.done();

    const remote = setUp(image(hostile, hostile, { remote: true, error: hostile }));
    await tick();
    const fig = remote.host.querySelector('figure.ne-image');
    assert.equal(fig.querySelector('.ne-image-missing').textContent, hostile);
    assert.deepEqual([...fig.querySelectorAll('*')].map(e => e.tagName).sort(), ['FIGCAPTION', 'SPAN']);
    remote.done();
});
