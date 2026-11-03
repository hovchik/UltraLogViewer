// Exploration tabs and split panes: several documents over one set of indexed files.
import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import { LogDocument } from '../src/core/document';
import { Engine, EngineEvent } from '../src/main/engine';
import { FilterSpec } from '../src/core/types';
import { LOG4J, tmpFile } from './helpers';

const query = (q: string): FilterSpec => ({ levels: [true, true, true, true, true, true, true], rules: [], query: q });
const messages = (d: LogDocument) => d.getRows(0, 1000).map((r) => (r.fields.message ?? '').split('\n')[0]);

describe('shared sources', () => {
  it('lets two views share one FileSource with their own filters', async () => {
    const a = new LogDocument(1, 'a');
    await a.addSource(tmpFile('s.log', LOG4J), {});
    const b = a.duplicate(2);
    expect(b.sources[0]).toBe(a.sources[0]);
    expect(b.set).toBe(a.set);
    expect(a.set.refCount).toBe(2);
    expect(b.title).toBe('a');
    expect(b.viewCount()).toBe(6); // no indexing: the entries are there at once

    await a.applyFilter(query('level:error'));
    await b.applyFilter(query('logger:App'));
    expect(messages(a)).toEqual(['Query failed']);
    expect(messages(b)).toEqual(['Starting app', 'Retrying', 'Giving up']);
    expect(b.info()).toMatchObject({ id: 2, total: 6, viewCount: 3 });
    b.ignoreFilter = true;
    expect(a.viewCount()).toBe(1);
    expect(b.viewCount()).toBe(6);
  });

  it('reads the file once on tail refresh and updates every view', async () => {
    const p = tmpFile('t.log', '2024-01-01 00:00:01 INFO ok\n2024-01-01 00:00:02 ERROR bad\n');
    const a = new LogDocument(1, 't');
    await a.addSource(p, {});
    const b = a.duplicate(2);
    await a.applyFilter(query('level:error'));
    await b.applyFilter(query('ok'));
    fs.appendFileSync(p, '2024-01-01 00:00:03 ERROR worse\n2024-01-01 00:00:04 INFO ok again\n');

    // One poll of the files; both documents take the change up from it.
    expect(await a.set.refresh()).not.toBeNull();
    expect(await a.set.refresh()).toBeNull();
    expect(await a.sync()).toBe(true);
    expect(await b.sync()).toBe(true);
    expect(await b.sync()).toBe(false);
    expect(messages(a)).toEqual(['bad', 'worse']);
    expect(messages(b)).toEqual(['ok', 'ok again']);
    // A quiet poll changes nothing: no view filters again.
    expect(await a.set.refresh()).toBeNull();
    expect(await a.sync()).toBe(false);
    expect(await b.sync()).toBe(false);

    // refresh() polls and syncs; the other view sees what that poll found.
    fs.appendFileSync(p, '2024-01-01 00:00:05 ERROR ok worst\n');
    expect(await b.refresh()).toBe(true);
    expect(await a.refresh()).toBe(true);
    expect(messages(a)).toEqual(['bad', 'worse', 'ok worst']);
    expect(messages(b)).toEqual(['ok', 'ok again', 'ok worst']);

    // Rotation: both start over on the new file.
    fs.writeFileSync(p, '2024-01-01 00:01:00 ERROR new\n');
    expect(await a.refresh()).toBe(true);
    expect(await b.sync()).toBe(true);
    expect(messages(a)).toEqual(['new']);
    expect(messages(b)).toEqual([]);
    a.close();
    b.close();
  });

  it('keeps the other views working when one is closed', async () => {
    const p = tmpFile('c.log', '2024-01-01 00:00:01 INFO one\n');
    const a = new LogDocument(1, 'c');
    await a.addSource(p, {});
    const b = a.duplicate(2);
    const c = b.duplicate(3);
    expect(a.set.refCount).toBe(3);

    a.close();
    a.close(); // twice is harmless
    expect(b.set.refCount).toBe(2);
    expect(b.sources[0].closed).toBe(false);
    fs.appendFileSync(p, '2024-01-01 00:00:02 WARN two\n');
    expect(await b.refresh()).toBe(true);
    expect(await c.sync()).toBe(true);
    expect(messages(b)).toEqual(['one', 'two']);
    await c.applyFilter(query('level:warn'));
    expect(messages(c)).toEqual(['two']);

    c.close();
    expect(b.sources[0].closed).toBe(false);
    expect(messages(b)).toEqual(['one', 'two']);
    const src = b.sources[0];
    b.close();
    expect(src.closed).toBe(true);
    expect(b.set.closed).toBe(true);
    expect(() => b.duplicate(4)).toThrow();
  });

  it('shares the merge of a merged view, and re-parsing reaches every view', async () => {
    const x = tmpFile('x.log', '2024-01-01 00:00:01 INFO a1\n2024-01-01 00:00:03 INFO a3\n');
    const y = tmpFile('y.log', '2024-01-01 00:00:02 ERROR b2\n2024-01-01 00:00:04 INFO b4\n');
    const a = new LogDocument(1, 'm');
    await a.addSources([x, y], () => ({}));
    const b = a.duplicate(2);
    expect(b.merged).toBe(true);
    expect(messages(b)).toEqual(['a1', 'b2', 'a3', 'b4']);
    await a.applyFilter(query('level:error'));
    await b.applyFilter(query('a'));
    expect(messages(a)).toEqual(['b2']);
    expect(messages(b)).toEqual(['a1', 'a3']);

    fs.appendFileSync(y, '2024-01-01 00:00:05 ERROR b5 a\n');
    expect(await a.refresh()).toBe(true);
    expect(await b.sync()).toBe(true);
    expect(messages(a)).toEqual(['b2', 'b5 a']);
    expect(messages(b)).toEqual(['a1', 'a3', 'b5 a']);
    expect(b.getRows(2, 1)[0].key).toBe('1:2');

    // Plain text: the message is now the whole line, in both views, which filter again.
    await a.reformat('plain');
    expect(b.sources[0].info().format).toBe('Plain text');
    expect(await b.sync()).toBe(true);
    expect(messages(a)).toEqual(['2024-01-01 00:00:02 ERROR b2', '2024-01-01 00:00:05 ERROR b5 a']);
    expect(b.viewCount()).toBe(3);
    a.close();
    b.close();
  });

  it('does not let a running filter in one view race the tail', async () => {
    const lines = Array.from({ length: 20_000 }, (_, i) => `2024-01-01 00:00:${String(i % 60).padStart(2, '0')} ${i % 7 === 0 ? 'ERROR' : 'INFO'} line ${i}\n`).join('');
    const p = tmpFile('r.log', lines);
    const a = new LogDocument(1, 'r');
    await a.addSource(p, {});
    const b = a.duplicate(2);
    b.scanBatch = 64;
    b.partialViewMs = 0;
    const filter = b.applyFilter(query('line'));
    fs.appendFileSync(p, '2024-01-01 00:01:00 ERROR line tail\n');
    await Promise.all([filter, a.refresh()]);
    await b.sync();
    expect(b.viewCount()).toBe(20_001);
    expect(b.getRows(20_000, 1)[0].fields.message).toBe('line tail');
    await a.applyFilter(query('level:error'));
    expect(a.viewCount()).toBe(Math.ceil(20_000 / 7) + 1);
    a.close();
    b.close();
  });

  it('gives a document duplicated while the files are polled the merge that poll built', async () => {
    const x = tmpFile('dx.log', '2024-01-01 00:00:01 INFO a1\n');
    const y = tmpFile('dy.log', '2024-01-01 00:00:02 INFO b2\n');
    const a = new LogDocument(1, 'd');
    await a.addSources([x, y], () => ({}));
    fs.appendFileSync(y, '2024-01-01 00:00:03 INFO b3\n');
    // Hold the poll inside its read of the file, and duplicate meanwhile (Ctrl+Shift+T during a tail refresh).
    const src = a.sources[1];
    const read = src.refresh.bind(src);
    let entered!: () => void;
    let release!: () => void;
    const inRead = new Promise<void>((r) => (entered = r));
    const held = new Promise<void>((r) => (release = r));
    src.refresh = async () => {
      entered();
      await held;
      return read();
    };
    const poll = a.set.refresh();
    await inRead;
    const b = a.duplicate(2);
    release();
    await poll;
    expect(await a.sync()).toBe(true);
    expect(await b.sync()).toBe(true); // b missed the poll's notice, but sees that the files changed since it was made
    expect(messages(b)).toEqual(['a1', 'b2', 'b3']);
    expect(b.viewCount()).toBe(3);
    expect(await b.sync()).toBe(false);
    a.close();
    b.close();
  });

  it('groups the timeline again after another view re-parsed the files', async () => {
    const a = new LogDocument(1, 'g');
    await a.addSource(tmpFile('g.log', LOG4J), {});
    const b = a.duplicate(2);
    const before = (await b.histogramBy('logger', 10))!;
    expect(before.keys).toContain('com.acme.App');
    await a.reformat('plain');
    expect(await b.sync()).toBe(true);
    // unfiltered, so the view itself did not change; the fields did
    const after = await b.histogramBy('logger', 10); // plain text has no timestamps: no timeline at all
    expect(after).not.toBe(before);
    expect(after).toBeNull();
    a.close();
    b.close();
  });

  it('duplicates in the engine, polls each file once and closes files with the last document', async () => {
    const p = tmpFile('e.log', '2024-01-01 00:00:01 INFO one\n');
    const events: EngineEvent[] = [];
    const engine = new Engine((e) => events.push(e));
    try {
      const id = engine.open([p]);
      await new Promise<void>((res) => {
        const t = setInterval(() => events.some((e) => e.type === 'doc-updated' && e.docId === id && !e.info.indexing) && (clearInterval(t), res()), 5);
      });
      const dup = engine.duplicate(id);
      expect(engine.info(dup)).toMatchObject({ total: 1, title: engine.info(id).title });
      await engine.setFilter(dup, query('level:warn'));
      fs.appendFileSync(p, '2024-01-01 00:00:02 WARN two\n');
      events.length = 0;
      await (engine as unknown as { poll(): Promise<void> }).poll();
      const updated = events.filter((e): e is Extract<EngineEvent, { type: 'doc-updated' }> => e.type === 'doc-updated');
      expect(updated.map((e) => [e.docId, e.info.total, e.info.viewCount]).sort()).toEqual([[dup, 2, 1], [id, 2, 2]].sort());
      engine.close(id);
      expect(engine.rows(dup, 0, 5).map((r) => r.fields.message)).toEqual(['two']);
    } finally {
      engine.dispose();
    }
  });
});
