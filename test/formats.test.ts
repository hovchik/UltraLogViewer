import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { detectFormat, detectFormatInfo, FORMAT_IDS, FORMAT_LABELS, QuickInfo } from '../src/core/formats';
import { LogDocument } from '../src/core/document';
import { Level } from '../src/core/types';
import { tmpFile } from './helpers';

/**
 * Detection, entry grouping, level, time and fields for every sample in test/fixtures/format-samples.json, which the
 * .NET suite reads too. It holds samples of the existing formats as well, so a new format that steals them fails.
 */
interface Sample {
  name: string;
  id: string;
  lines: string[];
  entries?: number;
  levels?: string[];
  times?: (string | null)[];
  fields?: Record<string, string | number>[];
}

const samples: Sample[] = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'format-samples.json'), 'utf8'));
const LABELS = ['', 'TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR', 'FATAL'];

describe('format samples', () => {
  it.each(samples.map((s) => [s.name, s] as const))('%s', (_, s) => {
    const f = detectFormat(s.lines, { year: 2024 });
    expect(f.id).toBe(s.id);
    // Group lines into entries the way indexing does: a line that does not start an entry continues the last one.
    const entries: { text: string; q: QuickInfo }[] = [];
    for (const line of s.lines) {
      const q = f.scan ? f.scan(line) : f.multiline && !f.isEntryStart(line) ? null : f.quick(line);
      if (q || !entries.length) entries.push({ text: line, q: q ?? { level: Level.None, time: null } });
      else entries[entries.length - 1].text += '\n' + line;
    }
    if (s.entries !== undefined) expect(entries.length).toBe(s.entries);
    if (s.levels) expect(entries.map((e) => LABELS[e.q.level])).toEqual(s.levels);
    s.times?.forEach((t, i) => expect([i, entries[i].q.time ?? null]).toEqual([i, t === null ? null : Date.parse(t)]));
    for (const exp of s.fields ?? []) {
      const i = exp.entry as number;
      const parsed = f.parse(entries[i].text);
      for (const [k, v] of Object.entries(exp)) if (k !== 'entry') expect([i, k, parsed[k]]).toEqual([i, k, v]);
    }
  });

  it('can choose every new format by hand', () => {
    for (const id of ['logfmt', 'postgres', 'mysql-error', 'mysql-general', 'mysql-slow', 'mongodb', 'rust', 'macos', 'laravel']) {
      expect(detectFormatInfo(['hello'], { year: 2024, force: id })).toMatchObject({ auto: false, format: { id } });
      expect(FORMAT_IDS).toContain(id);
      expect(FORMAT_LABELS[id]).toBeTruthy();
    }
    // Without its "# Query_time:" lines a slow log is not guessed.
    expect(detectFormat(['# Time: 2024-05-01T10:00:05.123456Z', 'SELECT 1;'], { year: 2024 }).id).not.toBe('mysql-slow');
  });

  it('indexes a slow query log as multi-line entries and filters it with typed queries', async () => {
    const s = samples.find((x) => x.name === 'MySQL slow query log')!;
    const doc = new LogDocument(1, 'slow');
    await doc.addSource(tmpFile('slow.log', s.lines.join('\n') + '\n'), {});
    expect(doc.info().sources[0].formatId).toBe('mysql-slow');
    expect(doc.info().viewCount).toBe(5);
    const count = async (query: string) => {
      await doc.applyFilter({ levels: [true, true, true, true, true, true, true], rules: [], query });
      return doc.info().viewCount;
    };
    // The query time is in seconds, so "1s" compares with "2.500123".
    expect(await count('query_time>1s')).toBe(1);
    expect(await count('rows_examined<10000 and exists(query_time)')).toBe(1);
    expect(await count('db?=shop')).toBe(5); // the header entries have no db, the second query has an empty one
    expect(await count('since:2024-05-01T10:00:06Z')).toBe(1);
    doc.close();
  });
});
