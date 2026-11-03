// A small, strict XML reader for the log4j/log4net events and Windows event log records the receivers convert. Node has
// no XML parser and these documents are small, so this builds a plain element tree. It never fetches anything: a
// DOCTYPE (and with it any entity definition) is refused, and only the five predefined entities and character
// references are expanded.

export interface XmlElement {
  /** The name as written, with its prefix ("log4j:event"). */
  name: string;
  /** The name without its prefix ("event"). */
  local: string;
  attrs: Record<string, string>;
  children: XmlElement[];
  /** The element's own text (CDATA included), without its children's text. */
  text: string;
}

const ENTITIES: Record<string, string> = { lt: '<', gt: '>', amp: '&', quot: '"', apos: "'" };

function decode(s: string): string {
  if (!s.includes('&')) return s;
  return s.replace(/&(#x[0-9a-fA-F]+|#\d+|\w+);/g, (m, e: string) => {
    if (e[0] === '#') {
      const n = e[1] === 'x' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
      if (!Number.isFinite(n) || n > 0x10ffff) throw new Error('bad character reference');
      return String.fromCodePoint(n);
    }
    const v = ENTITIES[e];
    if (v === undefined) throw new Error(`unknown entity ${m}`);
    return v;
  });
}

const local = (name: string) => name.slice(name.indexOf(':') + 1);
const NAME = /[^\s/>=]+/y;
const ATTR = /\s*([^\s/>=]+)\s*=\s*("([^"]*)"|'([^']*)')/y;

/** Parses one element (with optional leading whitespace, comments and an XML declaration). Throws when malformed. */
export function parseXml(xml: string): XmlElement {
  let i = 0;
  const stack: XmlElement[] = [];
  let root: XmlElement | null = null;
  const skipMisc = () => {
    for (;;) {
      while (i < xml.length && /\s/.test(xml[i])) i++;
      if (xml.startsWith('<?', i)) {
        const e = xml.indexOf('?>', i);
        if (e < 0) throw new Error('unclosed declaration');
        i = e + 2;
      } else if (xml.startsWith('<!--', i)) {
        const e = xml.indexOf('-->', i);
        if (e < 0) throw new Error('unclosed comment');
        i = e + 3;
      } else return;
    }
  };
  skipMisc();
  while (i < xml.length) {
    if (root && !stack.length) {
      skipMisc();
      if (i < xml.length) throw new Error('text after the root element');
      break;
    }
    const top = stack[stack.length - 1];
    if (xml[i] !== '<') {
      if (!top) throw new Error('text outside the root element');
      const e = xml.indexOf('<', i);
      if (e < 0) throw new Error('unclosed element');
      top.text += decode(xml.slice(i, e));
      i = e;
      continue;
    }
    if (xml.startsWith('<![CDATA[', i)) {
      const e = xml.indexOf(']]>', i);
      if (e < 0 || !top) throw new Error('bad CDATA');
      top.text += xml.slice(i + 9, e);
      i = e + 3;
      continue;
    }
    if (xml.startsWith('<!--', i) || xml.startsWith('<?', i)) {
      const end = xml.startsWith('<!--', i) ? '-->' : '?>';
      const e = xml.indexOf(end, i);
      if (e < 0) throw new Error('unclosed comment');
      i = e + end.length;
      continue;
    }
    if (xml.startsWith('<!', i)) throw new Error('DOCTYPE is not allowed');
    if (xml[i + 1] === '/') {
      NAME.lastIndex = i + 2;
      const m = NAME.exec(xml);
      if (!m || !top || m[0] !== top.name) throw new Error('mismatched end tag');
      i = NAME.lastIndex;
      while (/\s/.test(xml[i] ?? '')) i++;
      if (xml[i] !== '>') throw new Error('bad end tag');
      i++;
      stack.pop();
      continue;
    }
    NAME.lastIndex = i + 1;
    const m = NAME.exec(xml);
    if (!m) throw new Error('bad start tag');
    const el: XmlElement = { name: m[0], local: local(m[0]), attrs: {}, children: [], text: '' };
    i = NAME.lastIndex;
    for (;;) {
      ATTR.lastIndex = i;
      const a = ATTR.exec(xml);
      if (!a) break;
      if (a[1] in el.attrs) throw new Error('duplicate attribute');
      el.attrs[a[1]] = decode(a[3] ?? a[4]);
      i = ATTR.lastIndex;
    }
    while (/\s/.test(xml[i] ?? '')) i++;
    const selfClosing = xml[i] === '/';
    if (selfClosing) i++;
    if (xml[i] !== '>') throw new Error('bad start tag');
    i++;
    if (top) top.children.push(el);
    else if (root) throw new Error('two root elements');
    else root = el;
    if (!selfClosing) stack.push(el);
  }
  if (!root || stack.length) throw new Error('unclosed element');
  return root;
}

/** An attribute by its name without prefix. */
export function attr(e: XmlElement | undefined, name: string): string {
  if (!e) return '';
  if (name in e.attrs) return e.attrs[name];
  for (const k of Object.keys(e.attrs)) if (local(k) === name && !k.startsWith('xmlns')) return e.attrs[k];
  return '';
}

export const child = (e: XmlElement | undefined, name: string) => e?.children.find((c) => c.local === name);

/** All text inside the element, children included, in document order. */
export function allText(e: XmlElement): string {
  // own text is collected as one string, so for mixed content it comes first; the events here have no mixed content
  return e.text + e.children.map(allText).join('');
}
