// Generates a realistic sample log for trying the viewer and for benchmarks.
// Usage: node scripts/gen-sample.mjs [out.log] [megabytes] [--live]
import * as fs from 'node:fs';

const out = process.argv[2] ?? 'sample.log';
const mb = Number(process.argv[3] ?? 20);
const live = process.argv.includes('--live');
const loggers = ['com.acme.api.OrderController', 'com.acme.db.ConnectionPool', 'com.acme.cache.RedisCache', 'com.acme.auth.TokenService', 'com.acme.jobs.Scheduler'];
const threads = ['main', 'http-nio-8080-exec-1', 'http-nio-8080-exec-2', 'scheduler-1', 'worker-3'];
const msgs = [
  (i) => `GET /api/orders/${i % 997} completed in ${i % 450} ms`,
  (i) => `Acquired connection ${i % 32} from pool (active=${i % 20}, idle=${i % 12})`,
  (i) => `Cache ${i % 3 ? 'hit' : 'miss'} for key order:${i % 5000}`,
  (i) => `Token refreshed for user u${i % 800}`,
  (i) => `Job cleanup-${i % 7} finished, removed ${i % 100} rows`,
  (i) => `Payload {"orderId":${i},"items":[{"sku":"A-${i % 90}","qty":${(i % 4) + 1}}],"total":${(i % 1000) / 10}}`,
];
const pick = (a, i) => a[i % a.length];
let t = Date.UTC(2024, 4, 1, 8, 0, 0);
let i = 0;
const pad = (n, w = 2) => String(n).padStart(w, '0');
const ts = (ms) => {
  const d = new Date(ms);
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())},${pad(d.getUTCMilliseconds(), 3)}`;
};
let seed = 42;
const rnd = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);
const warnMsgs = [
  (i) => `Slow query on orders took ${800 + (i % 900)} ms`,
  (i) => `Cache miss ratio ${(40 + (i % 50))}% above threshold`,
  (i) => `Retrying request ${i % 1000} (attempt ${(i % 3) + 1})`,
];
const errMsgs = [
  (i) => `Failed to process order ${i % 997}: connection reset`,
  (i) => `Request to payment-service timed out after 30000 ms (req ${i})`,
  (i) => `Token validation failed for user u${i % 800}`,
];
function entry() {
  i++;
  t += Math.floor(rnd() * 120);
  const r = rnd();
  const lvl = r < 0.002 ? 'FATAL' : r < 0.03 ? 'ERROR' : r < 0.09 ? 'WARN' : r < 0.6 ? 'INFO' : r < 0.9 ? 'DEBUG' : 'TRACE';
  const msg = lvl === 'ERROR' || lvl === 'FATAL' ? pick(errMsgs, i) : lvl === 'WARN' ? pick(warnMsgs, i) : pick(msgs, Math.floor(rnd() * msgs.length));
  let s = `${ts(t)} [${pick(threads, Math.floor(rnd() * 5))}] ${lvl.padEnd(5)} ${pick(loggers, Math.floor(rnd() * 5))} - ${msg(i)}\n`;
  if (lvl === 'ERROR' && rnd() < 0.5) {
    s += `java.lang.IllegalStateException: Connection reset (attempt ${i % 5})\n\tat com.acme.db.ConnectionPool.borrow(ConnectionPool.java:${100 + (i % 50)})\n\tat com.acme.api.OrderController.get(OrderController.java:42)\n\tat java.base/java.lang.Thread.run(Thread.java:1583)\n`;
  }
  return s;
}

if (live) {
  console.log(`Appending to ${out} every 200ms (Ctrl+C to stop)`);
  setInterval(() => fs.appendFileSync(out, entry() + entry()), 200);
} else {
  const fd = fs.openSync(out, 'w');
  const target = mb * 1024 * 1024;
  let written = 0;
  while (written < target) {
    let chunk = '';
    for (let k = 0; k < 2000; k++) chunk += entry();
    fs.writeSync(fd, chunk);
    written += Buffer.byteLength(chunk);
  }
  fs.closeSync(fd);
  console.log(`Wrote ${(written / 1048576).toFixed(1)} MB, ${i} entries to ${out}`);
}
