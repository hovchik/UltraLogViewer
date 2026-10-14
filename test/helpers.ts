import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-test-'));

export function tmpFile(name: string, content: string | Buffer): string {
  const p = path.join(dir, `${Math.random().toString(36).slice(2)}-${name}`);
  fs.writeFileSync(p, content);
  return p;
}

export const LOG4J = [
  '2024-05-01 10:00:00,001 [main] INFO  com.acme.App - Starting app',
  '2024-05-01 10:00:01,002 [main] DEBUG com.acme.Db - Connecting to db',
  '2024-05-01 10:00:02,003 [worker-1] WARN  com.acme.Cache - Cache miss ratio high',
  '2024-05-01 10:00:03,004 [worker-2] ERROR com.acme.Db - Query failed',
  'java.sql.SQLException: timeout',
  '\tat com.acme.Db.query(Db.java:42)',
  '\tat com.acme.App.main(App.java:10)',
  '2024-05-01 10:00:04,005 [main] INFO  com.acme.App - Retrying',
  '2024-05-01 10:00:05,006 [main] FATAL com.acme.App - Giving up',
].join('\n') + '\n';
