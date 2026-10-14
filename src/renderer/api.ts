import type { UlvApi } from '../preload/preload';
import type { EngineEvent } from '../main/engine';
import type { Engine } from '../main/engine';

declare global {
  interface Window {
    ulv: UlvApi;
  }
}

type Methods = { [K in keyof Engine]: Engine[K] extends (...a: infer A) => infer R ? (...a: A) => Promise<Awaited<R>> : never };

/** Typed proxy for the engine running in the worker thread. */
export const engine = new Proxy({} as Methods, {
  get: (_t, method: string) => (...args: unknown[]) => window.ulv.call(method, ...args),
});

export type { EngineEvent };
export const ulv = () => window.ulv;
