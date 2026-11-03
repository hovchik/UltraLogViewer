// Runs SQL queries over a document's table (see core/logSql.ts) off the engine thread, so a query that never ends can
// be given up. The table is the engine's shared-cache in-memory database, read through a read-only connection here.

import { DatabaseSync } from 'node:sqlite';
import { parentPort } from 'node:worker_threads';
import { runQuery, sqlFunctions, viewRowIn } from '../core/logSql';

const port = parentPort!;
let db: DatabaseSync | null = null;
let uri = '';
let view: Uint32Array | null = null;
let base = 0;

port.on('message', (m: { id: number; uri: string; sql: string; maxRows: number; view: Uint32Array | null; base: number }) => {
  if (m.uri !== uri) {
    db?.close();
    db = new DatabaseSync(m.uri, { readOnly: true });
    db.exec('PRAGMA temp_store = MEMORY');
    sqlFunctions(db, (b) => viewRowIn(view, base, b));
    uri = m.uri;
  }
  ({ view, base } = m);
  port.postMessage({ id: m.id, result: runQuery(db!, m.sql, m.maxRows) });
});
