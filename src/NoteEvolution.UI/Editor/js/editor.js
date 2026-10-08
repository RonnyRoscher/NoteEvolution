// The NoteEvolution editor: TipTap schema, editing commands, the uniqueKeys plugin, chip rendering and event
// forwarding to .NET (TipTapInterop). Everything the book file needs is decided in C#.
import { Editor, Extension, Node } from '@tiptap/core';
import Bold from '@tiptap/extension-bold';
import Dropcursor from '@tiptap/extension-dropcursor';
import HardBreak from '@tiptap/extension-hard-break';
import History from '@tiptap/extension-history';
import Italic from '@tiptap/extension-italic';
import Text from '@tiptap/extension-text';
import { Mapping } from '@tiptap/pm/transform';
import { EditorState, Plugin, PluginKey, TextSelection } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';

/** A new lower-case UUID; crypto.randomUUID needs a secure context, getRandomValues does not. */
function newKey() {
    if (typeof crypto.randomUUID === 'function') {
        return crypto.randomUUID();
    }
    const b = crypto.getRandomValues(new Uint8Array(16));
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    const h = Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
    return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

function parseSources(value) {
    try {
        const sources = JSON.parse(value ?? '[]');
        return Array.isArray(sources) ? sources : [];
    } catch {
        return [];
    }
}

// A key is never taken from parsed or pasted HTML (R31): a pasted copy may come from outside the shown document, and
// its key would make C# move the original block. Moves within the editor keep their keys through the drag slice.
const keyAttribute = {
    default: null,
    parseHTML: () => null,
    renderHTML: attrs => (attrs.key ? { 'data-key': attrs.key } : {}),
};

// textBlock comes first: it is the default node where the document needs one.
const Doc = Node.create({
    name: 'doc',
    topNode: true,
    content: '(textBlock | heading)+',
});

/**
 * A heading of the manuscript; only its title can be edited here, the structure commands and the outline add, move
 * and remove headings.
 */
const Heading = Node.create({
    name: 'heading',
    content: 'text*',
    marks: '',
    defining: true,
    // Text of a neighbouring block never joins into the title (Backspace, Delete, deleting a selection).
    isolating: true,
    addAttributes() {
        return {
            key: keyAttribute,
            level: {
                default: 1,
                parseHTML: el => Number(el.tagName.slice(1)) || 1,
                renderHTML: () => ({}),
            },
        };
    },
    parseHTML() {
        return [1, 2, 3, 4, 5, 6].map(level => ({ tag: `h${level}[data-ne-heading]` }));
    },
    renderHTML({ node, HTMLAttributes }) {
        const level = Math.min(6, Math.max(1, node.attrs.level));
        return [`h${level}`, { ...HTMLAttributes, 'data-ne-heading': '', class: 'ne-heading' }, 0];
    },
});

/** A paragraph of a text block: the first (depth 0) is the block's own text, the others its sub-bullets. */
const Para = Node.create({
    name: 'para',
    content: 'inline*',
    addAttributes() {
        return {
            key: keyAttribute,
            depth: {
                default: 0,
                parseHTML: el => Number(el.getAttribute('data-depth')) || 0,
                renderHTML: attrs => ({ 'data-depth': attrs.depth, style: `--depth: ${attrs.depth}` }),
            },
            isNote: {
                default: false,
                parseHTML: el => el.getAttribute('data-note') === 'true',
                renderHTML: attrs => (attrs.isNote ? { 'data-note': 'true' } : {}),
            },
            // The Markdown the paragraph was loaded with; C# keeps it when the text is unchanged.
            md: { default: null, rendered: false, parseHTML: () => null },
        };
    },
    parseHTML() {
        return [{ tag: 'p' }];
    },
    renderHTML({ HTMLAttributes }) {
        return ['p', { ...HTMLAttributes, class: 'ne-para' }, 0];
    },
});

/** A text block with its source chips below and a handle to drag it. */
class TextBlockView {
    constructor(node, options) {
        this.node = node;
        this.options = options;
        this.dom = document.createElement('div');
        this.dom.className = 'ne-block';
        const handle = document.createElement('div');
        handle.className = 'ne-block-handle';
        handle.contentEditable = 'false';
        this.contentDOM = document.createElement('div');
        this.contentDOM.className = 'ne-block-content';
        this.chips = document.createElement('div');
        this.chips.className = 'ne-chips';
        this.chips.contentEditable = 'false';
        this.dom.append(handle, this.contentDOM, this.chips);
        this.render();
    }

    update(node) {
        if (node.type !== this.node.type) {
            return false;
        }
        const sourcesChanged = JSON.stringify(node.attrs.sources) !== JSON.stringify(this.node.attrs.sources);
        this.node = node;
        this.render(sourcesChanged);
        return true;
    }

    render(sourcesChanged = true) {
        const { key, sources } = this.node.attrs;
        this.dom.setAttribute('data-key', key ?? '');
        this.dom.toggleAttribute('data-has-sources', (sources ?? []).length > 0);
        if (!sourcesChanged) {
            return;
        }
        this.chips.replaceChildren(...(sources ?? []).map(source => this.chip(source)));
    }

    chip(source) {
        const chip = document.createElement('span');
        chip.className = source.broken ? 'ne-chip broken' : 'ne-chip';
        const label = document.createElement('button');
        label.type = 'button';
        label.className = 'ne-chip-label';
        label.textContent = source.label;
        label.addEventListener('click', () => this.options.onChipClicked(source.id));
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'ne-chip-remove';
        remove.textContent = '×';
        remove.addEventListener('click', () => this.options.onChipRemoved(this.node.attrs.key, source.id));
        chip.append(label, remove);
        return chip;
    }

    stopEvent(event) {
        return this.chips.contains(event.target);
    }

    ignoreMutation(mutation) {
        return mutation.type !== 'selection' && !this.contentDOM.contains(mutation.target);
    }
}

const TextBlock = Node.create({
    name: 'textBlock',
    content: 'para+',
    draggable: true,
    addOptions() {
        return { onChipClicked: () => {}, onChipRemoved: () => {} };
    },
    addAttributes() {
        return {
            key: keyAttribute,
            // A pasted block becomes a copy split from the block it was copied from: new key and id, same sources.
            splitFrom: { default: null, rendered: false, parseHTML: el => el.getAttribute('data-key') },
            sources: {
                default: [],
                parseHTML: el => parseSources(el.getAttribute('data-sources')),
                renderHTML: attrs => ({ 'data-sources': JSON.stringify(attrs.sources ?? []) }),
            },
        };
    },
    parseHTML() {
        return [{ tag: 'div[data-ne-block]' }];
    },
    renderHTML({ HTMLAttributes }) {
        return ['div', { ...HTMLAttributes, 'data-ne-block': '' }, 0];
    },
    addNodeView() {
        return ({ node }) => new TextBlockView(node, this.options);
    },
});

/** An empty text block after the heading at depth 1 of $from, with the cursor in it. */
function insertBlockAfterHeading(tr, $from) {
    const at = $from.after(1);
    const schema = tr.doc.type.schema;
    tr.insert(at, schema.nodes.textBlock.create({ key: newKey() }, schema.nodes.para.create()));
    tr.setSelection(TextSelection.create(tr.doc, at + 2));
    tr.scrollIntoView();
}

/** Enter: a new paragraph in the block (a sub-bullet, at least depth 1). */
function newParagraph({ tr, dispatch }) {
    const { $from } = tr.selection;
    if ($from.parent.type.name === 'heading') {
        if (dispatch) {
            insertBlockAfterHeading(tr, $from);
        }
        return true;
    }
    if ($from.parent.type.name !== 'para') {
        return false;
    }
    if (dispatch) {
        if (!tr.selection.empty) {
            tr.deleteSelection();
        }
        const $pos = tr.selection.$from;
        const para = $pos.parent;
        if (para.type.name !== 'para') {
            return true;
        }
        const attrs = { key: null, depth: Math.max(1, para.attrs.depth), isNote: false, md: null };
        tr.split($pos.pos, 1, [{ type: para.type, attrs }]);
        tr.scrollIntoView();
    }
    return true;
}

/**
 * Ctrl+Enter: splits the text block at the cursor; the new part gets a new key, splitFrom and the sources. At the
 * block's end an empty block without splitFrom follows (at its start one is put before it).
 */
function splitTextBlock({ tr, dispatch }) {
    const { $from } = tr.selection;
    if ($from.parent.type.name === 'heading') {
        if (dispatch) {
            insertBlockAfterHeading(tr, $from);
        }
        return true;
    }
    if ($from.parent.type.name !== 'para') {
        return false;
    }
    if (dispatch) {
        if (!tr.selection.empty) {
            tr.deleteSelection();
        }
        const $pos = tr.selection.$from;
        if ($pos.parent.type.name !== 'para' || $pos.depth !== 2) {
            return true;
        }
        const block = $pos.node(1);
        const atEnd = $pos.parentOffset === $pos.parent.content.size && $pos.index(1) === block.childCount - 1;
        const atStart = $pos.parentOffset === 0 && $pos.index(1) === 0;
        if (atEnd || atStart) {
            const empty = block.type.create({ key: newKey() }, $pos.parent.type.create());
            const at = atEnd ? $pos.after(1) : $pos.before(1);
            tr.insert(at, empty);
            if (atEnd) {
                tr.setSelection(TextSelection.create(tr.doc, at + 2));
            }
        } else {
            tr.split($pos.pos, 2, [
                { type: block.type, attrs: { key: newKey(), splitFrom: block.attrs.key, sources: block.attrs.sources } },
                { type: $pos.parent.type, attrs: { key: null, depth: 0, isNote: false, md: null } },
            ]);
        }
        tr.scrollIntoView();
    }
    return true;
}

/** Calls change(para, index, previousDepth) for every selected paragraph except a block's first; returns attrs or null. */
function changeSelectedParagraphs(tr, change) {
    const { from, to } = tr.selection;
    tr.doc.nodesBetween(from, to, (node, pos) => {
        if (node.type.name !== 'textBlock') {
            return node.type.name === 'doc';
        }
        let previous = 0;
        node.forEach((para, offset, index) => {
            const start = pos + 1 + offset;
            const selected = start + para.nodeSize > from && start < to + (from === to ? 1 : 0);
            let depth = para.attrs.depth;
            if (selected && index > 0) {
                const attrs = change(para, previous);
                if (attrs) {
                    tr.setNodeMarkup(start, undefined, { ...para.attrs, ...attrs });
                    depth = attrs.depth ?? depth;
                }
            }
            previous = index === 0 ? 0 : depth;
        });
        return false;
    });
}

/** Tab / Shift+Tab: depth ±1, between 1 and the previous paragraph's depth + 1. */
const shiftDepth = delta => ({ tr, dispatch }) => {
    if (dispatch) {
        changeSelectedParagraphs(tr, (para, previous) => {
            const depth = Math.min(Math.max(para.attrs.depth + delta, 1), previous + 1);
            return depth === para.attrs.depth ? null : { depth };
        });
    }
    return true;
};

/** Ctrl+Shift+N: toggles #notiz on the selected sub-bullets (never on a block's own text). */
function toggleNote({ tr, dispatch }) {
    if (dispatch) {
        let value;
        changeSelectedParagraphs(tr, para => {
            value ??= !para.attrs.isNote;
            return { isNote: value };
        });
    }
    return true;
}

const inHeading = state => state.selection.$from.parent.type.name === 'heading';

/**
 * The editing shortcuts. The structure commands (spec 3) run in C#: their shortcuts call options.onSectionCommand
 * with the SectionCommand member name.
 */
const Keys = Extension.create({
    name: 'noteEvolutionKeys',
    priority: 1000,
    addOptions() {
        return { onSectionCommand: () => {} };
    },
    addKeyboardShortcuts() {
        const run = command => () => this.editor.commands.command(command);
        const section = name => () => {
            this.options.onSectionCommand(name);
            return true;
        };
        const inHeadingOr = (name, otherwise) => () => (inHeading(this.editor.state) ? section(name)() : otherwise());
        return {
            Enter: run(newParagraph),
            'Mod-Enter': run(splitTextBlock),
            'Alt-Enter': section('InsertAfter'),
            'Alt-Shift-Enter': section('InsertChild'),
            Tab: inHeadingOr('Indent', run(shiftDepth(1))),
            'Shift-Tab': inHeadingOr('Outdent', run(shiftDepth(-1))),
            Backspace: () => {
                const { selection } = this.editor.state;
                return selection.empty && selection.$from.parentOffset === 0 && inHeading(this.editor.state)
                    ? section('RemoveHeading')()
                    : false;
            },
            'Mod-Shift-n': run(toggleNote),
            'Mod-Shift-N': run(toggleNote),
        };
    },
});

/** The occurrences of keys in a document: [key, position, kind] for headings, text blocks and sub-bullets. */
function keyedNodes(doc) {
    const result = [];
    doc.forEach((node, offset) => {
        result.push([node.attrs.key, offset]);
        if (node.type.name === 'textBlock') {
            node.forEach((para, paraOffset, index) => {
                if (index > 0) {
                    result.push([para.attrs.key, offset + 1 + paraOffset]);
                }
            });
        }
    });
    return result;
}

/**
 * Gives every heading, text block and sub-bullet a unique key: a missing or duplicate key is replaced by a new one
 * (a duplicate text block also gets splitFrom = the original key). Of duplicates, the node where the old document's
 * node went keeps the key. Also keeps the paragraph shape: the first paragraph is the block's text (no key, depth 0,
 * no #notiz), every other one has depth 1 … previous depth + 1.
 */
function normalize(transactions, oldState, newState) {
    const mapping = new Mapping();
    transactions.forEach(t => mapping.appendMapping(t.mapping));
    const owner = new Map();
    for (const [key, pos] of keyedNodes(oldState.doc)) {
        if (key) {
            owner.set(key, mapping.map(pos, 1));
        }
    }
    // Each key stays at one place: where its old node went, else at its first occurrence.
    const keeper = new Map();
    for (const [key, pos] of keyedNodes(newState.doc)) {
        if (key && (!keeper.has(key) || owner.get(key) === pos)) {
            keeper.set(key, pos);
        }
    }
    const take = (key, pos) => (key && keeper.get(key) === pos ? key : newKey());
    const tr = newState.tr;
    newState.doc.forEach((node, offset) => {
        const key = take(node.attrs.key, offset);
        if (key !== node.attrs.key) {
            const splitFrom = node.type.name === 'textBlock' && node.attrs.key ? node.attrs.key : node.attrs.splitFrom;
            tr.setNodeMarkup(offset, undefined, node.type.name === 'textBlock' ? { ...node.attrs, key, splitFrom } : { ...node.attrs, key });
        }
        if (node.type.name !== 'textBlock') {
            return;
        }
        let previous = 0;
        node.forEach((para, paraOffset, index) => {
            const pos = offset + 1 + paraOffset;
            const attrs = index === 0
                ? { ...para.attrs, key: null, depth: 0, isNote: false }
                : { ...para.attrs, key: take(para.attrs.key, pos), depth: Math.min(Math.max(para.attrs.depth, 1), previous + 1) };
            previous = attrs.depth;
            if (attrs.key !== para.attrs.key || attrs.depth !== para.attrs.depth || attrs.isNote !== para.attrs.isNote) {
                tr.setNodeMarkup(pos, undefined, attrs);
            }
        });
    });
    return tr.docChanged ? tr : null;
}

const UniqueKeys = Extension.create({
    name: 'uniqueKeys',
    addProseMirrorPlugins() {
        return [
            new Plugin({
                key: new PluginKey('uniqueKeys'),
                appendTransaction: (transactions, oldState, newState) =>
                    transactions.some(t => t.docChanged) ? normalize(transactions, oldState, newState) : null,
            }),
        ];
    },
});

function headingList(doc) {
    const list = [];
    doc.forEach(node => {
        if (node.type.name === 'heading') {
            list.push(`${node.attrs.key}:${node.attrs.level}`);
        }
    });
    return list.join('|');
}

/**
 * Headings can be renamed here (an empty title is allowed), but not added, removed or moved by typing: the structure
 * commands and the outline do that.
 */
const FixedHeadings = Extension.create({
    name: 'fixedHeadings',
    addProseMirrorPlugins() {
        return [
            new Plugin({
                key: new PluginKey('fixedHeadings'),
                filterTransaction: (tr, state) => !tr.docChanged || headingList(tr.doc) === headingList(state.doc),
            }),
        ];
    },
});

/**
 * The element at the selection (spec 2): { kind, key, textBlockKey, offset, nodes }, or null outside every element.
 * kind is 'heading', 'textBlock' (the block's own text, depth 0) or 'detail' (a paragraph of depth >= 1); nodes are
 * the [position, node] pairs the element marks: a heading with its section (up to the next heading of the same or a
 * higher level), a text block, or a detail with the deeper paragraphs that follow it.
 */
function currentElement(state) {
    const { selection, doc } = state;
    const { $from } = selection;
    let top;
    let topPos;
    if ($from.depth >= 1) {
        top = $from.node(1);
        topPos = $from.before(1);
    } else if (selection.node) {
        // A dragged block (a node selection of a top-level node).
        top = selection.node;
        topPos = selection.from;
    } else {
        return null;
    }
    const offset = $from.parent.isTextblock ? $from.parentOffset : 0;
    if (top.type.name === 'heading') {
        const nodes = [[topPos, top]];
        for (let i = doc.resolve(topPos).index(0) + 1, pos = topPos + top.nodeSize; i < doc.childCount; i++) {
            const node = doc.child(i);
            if (node.type.name === 'heading' && node.attrs.level <= top.attrs.level) {
                break;
            }
            nodes.push([pos, node]);
            pos += node.nodeSize;
        }
        return { kind: 'heading', key: top.attrs.key, textBlockKey: null, offset, nodes };
    }
    const para = $from.depth >= 2 ? $from.node(2) : null;
    if (!para || para.attrs.depth < 1) {
        return { kind: 'textBlock', key: top.attrs.key, textBlockKey: top.attrs.key, offset, nodes: [[topPos, top]] };
    }
    const nodes = [];
    for (let i = $from.index(1), pos = $from.before(2); i < top.childCount; i++) {
        const node = top.child(i);
        if (nodes.length > 0 && node.attrs.depth <= para.attrs.depth) {
            break;
        }
        nodes.push([pos, node]);
        pos += node.nodeSize;
    }
    return { kind: 'detail', key: para.attrs.key, textBlockKey: top.attrs.key, offset, nodes };
}

/**
 * Marks the current element with the class ne-current on each of its nodes (ne-current-first / -last on the ends, so
 * the nodes draw one box); a detail's paragraphs get its depth as --current-depth, where its box starts.
 */
const CurrentElement = Extension.create({
    name: 'currentElement',
    addProseMirrorPlugins() {
        return [
            new Plugin({
                key: new PluginKey('currentElement'),
                props: {
                    decorations: state => {
                        const element = currentElement(state);
                        if (!element) {
                            return DecorationSet.empty;
                        }
                        const last = element.nodes.length - 1;
                        const depth = element.kind === 'detail' ? element.nodes[0][1].attrs.depth : null;
                        return DecorationSet.create(state.doc, element.nodes.map(([pos, node], i) => {
                            const classes = ['ne-current'];
                            if (i === 0) {
                                classes.push('ne-current-first');
                            }
                            if (i === last) {
                                classes.push('ne-current-last');
                            }
                            const attrs = { class: classes.join(' ') };
                            if (depth !== null) {
                                attrs.style = `--current-depth: ${depth}`;
                            }
                            return Decoration.node(pos, pos + node.nodeSize, attrs);
                        }));
                    },
                },
            }),
        ];
    },
});

/** The nearest scrolling ancestor of element (the document's scroller if none). */
function scrollParent(element) {
    for (let e = element.parentElement; e; e = e.parentElement) {
        if (/(auto|scroll)/.test(getComputedStyle(e).overflowY)) {
            return e;
        }
    }
    return document.scrollingElement ?? document.documentElement;
}

/** The part of the window that scroller shows, as { top, bottom } in client coordinates. */
function visibleArea(scroller) {
    if (scroller === document.scrollingElement || scroller === document.documentElement) {
        return { top: 0, bottom: window.innerHeight };
    }
    const rect = scroller.getBoundingClientRect();
    return { top: Math.max(rect.top, 0), bottom: Math.min(rect.bottom, window.innerHeight) };
}

/**
 * The key of the text block or heading after which a note dropped at the event's position goes, or null for the
 * start of the view.
 */
function dropTarget(view, event) {
    const doc = view.state.doc;
    let index = -1;
    doc.forEach((node, offset, i) => {
        const dom = view.nodeDOM(offset);
        if (dom && dom.getBoundingClientRect) {
            const rect = dom.getBoundingClientRect();
            if (event.clientY >= rect.top + rect.height / 2) {
                index = i;
            }
        }
    });
    return index >= 0 ? doc.child(index).attrs.key : null;
}

/**
 * Creates the editor in host. dotnet receives DocumentChanged(json), CursorChanged(kind, key, textBlockKey, offset),
 * SectionCommand(name), SectionBoxMoved(barTop, visible), ChipClicked(noteId), ChipRemoved(blockKey, noteId) and
 * NoteDropped(afterKey). options.barHeight is the height of the section bar C# shows at the marking box's bottom.
 */
export function createEditor(host, dotnet, options = {}) {
    const barHeight = options.barHeight ?? 0;
    const root = document.createElement('div');
    root.className = 'ne-editor-root mode-manuscript';
    host.appendChild(root);
    const call = (method, ...args) => dotnet.invokeMethodAsync(method, ...args).catch(error => console.error(error));
    let lastCursor;
    let lastBox;
    let boxFrame = 0;

    /** Reports the element at the cursor when it, or the offset in it, changed. */
    const reportCursor = state => {
        const element = currentElement(state);
        const cursor = element ? [element.kind, element.key, element.textBlockKey, element.offset] : [null, null, null, 0];
        const id = cursor.join('|');
        if (id !== lastCursor) {
            lastCursor = id;
            call('CursorChanged', ...cursor);
        }
    };

    /**
     * Reports where the section bar goes: at the marking box's bottom (relative to .ne-editor-pane), but at least
     * barHeight above the visible bottom of the scrolling pane; and whether the box is visible at all (never before a
     * cursor was reported for the shown book). The marked nodes follow each other, so the first one's top and the last
     * one's bottom are the box.
     */
    const reportBox = () => {
        boxFrame = 0;
        if (!root.isConnected) {
            return;
        }
        const pane = host.closest('.ne-editor-pane') ?? host;
        const area = visibleArea(scrollParent(host));
        const first = root.querySelector('.ne-current-first');
        const last = root.querySelector('.ne-current-last');
        const top = first && lastCursor !== undefined ? first.getBoundingClientRect().top : Infinity;
        const bottom = last && lastCursor !== undefined ? last.getBoundingClientRect().bottom : -Infinity;
        const visible = bottom > top && bottom > area.top && top < area.bottom;
        const barTop = visible ? Math.min(bottom, area.bottom - barHeight) - pane.getBoundingClientRect().top : 0;
        if (lastBox && lastBox.visible === visible && Math.abs(lastBox.barTop - barTop) < 1) {
            return;
        }
        lastBox = { barTop, visible };
        call('SectionBoxMoved', barTop, visible);
    };
    const scheduleBox = () => {
        boxFrame ||= requestAnimationFrame(reportBox);
    };

    const editor = new Editor({
        element: root,
        extensions: [
            Doc,
            Heading,
            TextBlock.configure({
                onChipClicked: noteId => call('ChipClicked', noteId),
                onChipRemoved: (blockKey, noteId) => call('ChipRemoved', blockKey, noteId),
            }),
            Para,
            Text,
            Bold,
            Italic,
            HardBreak.extend({
                addKeyboardShortcuts() {
                    return { 'Shift-Enter': () => this.editor.commands.setHardBreak() };
                },
            }),
            History,
            Dropcursor,
            Keys.configure({ onSectionCommand: name => call('SectionCommand', name) }),
            UniqueKeys,
            FixedHeadings,
            CurrentElement,
        ],
        content: { type: 'doc', content: [{ type: 'textBlock', attrs: { key: newKey() }, content: [{ type: 'para' }] }] },
        editorProps: {
            attributes: { class: 'ne-doc', spellcheck: 'true' },
            // The section bar covers the pane's bottom: the line being typed in or moved to stays above it.
            scrollThreshold: { top: 0, right: 0, bottom: barHeight + 8, left: 0 },
            scrollMargin: { top: 5, right: 5, bottom: barHeight + 8, left: 5 },
            handleDOMEvents: {
                drop: (view, event) => {
                    if (view.dragging) {
                        // A block or text moved within the editor.
                        return false;
                    }
                    event.preventDefault();
                    call('NoteDropped', dropTarget(view, event));
                    return true;
                },
            },
        },
        onUpdate: ({ editor: e }) => call('DocumentChanged', JSON.stringify(e.getJSON())),
        onSelectionUpdate: ({ editor: e }) => reportCursor(e.state),
        // A click on the place the selection already has changes no selection; after a new book it places the cursor.
        onFocus: ({ editor: e }) => reportCursor(e.state),
        onTransaction: scheduleBox,
    });

    // Scrolling (of any pane; the frame and the 1 px check keep that cheap) and resizing move the box too.
    document.addEventListener('scroll', scheduleBox, { capture: true, passive: true });
    window.addEventListener('resize', scheduleBox);
    const resizes = new ResizeObserver(scheduleBox);
    resizes.observe(root);

    return {
        /**
         * Shows a new document (no change event, fresh undo history). With keepCursor (the same book again) the cursor
         * stays near where it was and is reported, but only if one had been reported for the previous document.
         * Otherwise (another book, or no cursor placed yet) the cursor is not reported (for another book the selection
         * goes to the document's start): it is first reported when the user places it (a selection change, or focusing
         * the editor) or an element is revealed.
         */
        setDocument(json, showChips, keepCursor) {
            const doc = editor.schema.nodeFromJSON(JSON.parse(json));
            doc.check();
            const selection = keepCursor
                ? TextSelection.near(doc.resolve(Math.min(editor.state.selection.from, doc.content.size)))
                : TextSelection.atStart(doc);
            editor.view.updateState(EditorState.create({ doc, plugins: editor.state.plugins, selection }));
            root.classList.toggle('show-chips', showChips);
            // updateState raises no selection update, but the cursor may now be in another element.
            const placed = lastCursor !== undefined;
            lastCursor = undefined;
            if (keepCursor && placed) {
                reportCursor(editor.state);
            }
            scheduleBox();
        },
        /**
         * Puts the cursor at the start of the text of the heading, text block or detail with the key (null: the start
         * of the document) and scrolls it into view; an unknown key does nothing.
         */
        reveal(key) {
            const doc = editor.state.doc;
            let target = key ? null : { pos: 0, at: TextSelection.atStart(doc).from };
            doc.forEach((node, offset) => {
                if (target) {
                    return;
                }
                if (node.attrs.key === key) {
                    // A heading's title starts inside it, a text block's text inside its first paragraph.
                    target = { pos: offset, at: offset + (node.type.name === 'heading' ? 1 : 2) };
                } else if (node.type.name === 'textBlock') {
                    node.forEach((para, paraOffset, index) => {
                        if (!target && index > 0 && para.attrs.key === key) {
                            target = { pos: offset + 1 + paraOffset, at: offset + 2 + paraOffset };
                        }
                    });
                }
            });
            if (!target) {
                return;
            }
            editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(doc, target.at)));
            // The user types on right there; a text field that has the focus meanwhile (e.g. renaming an outline
            // heading after a double click) keeps it.
            const active = document.activeElement;
            if (!(active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement || active instanceof HTMLSelectElement)) {
                editor.view.focus();
            }
            // ProseMirror scrolls to the selection only while the editor has the focus, and focus() does not scroll.
            const dom = editor.view.nodeDOM(target.pos);
            if (dom instanceof Element) {
                const rect = dom.getBoundingClientRect();
                const area = visibleArea(scrollParent(host));
                if (rect.top < area.top || rect.bottom > area.bottom) {
                    dom.scrollIntoView({ block: 'start' });
                }
            }
        },
        destroy() {
            cancelAnimationFrame(boxFrame);
            resizes.disconnect();
            document.removeEventListener('scroll', scheduleBox, { capture: true });
            window.removeEventListener('resize', scheduleBox);
            editor.destroy();
            root.remove();
        },
    };
}
