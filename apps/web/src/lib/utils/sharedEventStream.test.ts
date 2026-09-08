import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetSharedStreams, sharedStreamCount, subscribeShared } from './sharedEventStream';

/**
 * Same stand-in as eventStream.test.ts: EventSource is read off the global at
 * call time, so swapping it per-test needs no mocking library.
 */
class FakeEventSource {
	static instances: FakeEventSource[] = [];
	static reset() {
		FakeEventSource.instances = [];
	}

	url: string;
	closed = false;
	onmessage: ((event: { data: string }) => void) | null = null;
	onopen: (() => void) | null = null;
	onerror: ((event: Event) => void) | null = null;

	constructor(url: string) {
		this.url = url;
		FakeEventSource.instances.push(this);
	}

	close() {
		this.closed = true;
	}

	emitMessage(data: unknown) {
		this.onmessage?.({ data: JSON.stringify(data) });
	}
}

const openCount = () => FakeEventSource.instances.filter((i) => !i.closed).length;

beforeEach(() => {
	FakeEventSource.reset();
	vi.stubGlobal('EventSource', FakeEventSource);
	vi.useFakeTimers();
});

afterEach(() => {
	resetSharedStreams();
	vi.unstubAllGlobals();
	vi.useRealTimers();
});

describe('subscribeShared', () => {
	it('opens one connection for two subscribers to the same url', () => {
		const a: unknown[] = [];
		const b: unknown[] = [];

		subscribeShared('/api/heartbeat', { onMessage: (d) => a.push(d) });
		subscribeShared('/api/heartbeat', { onMessage: (d) => b.push(d) });

		// The whole point: the server pages held two connections to this exact
		// URL, which cost one of the browser's six per-origin slots for nothing.
		expect(openCount()).toBe(1);

		FakeEventSource.instances[0].emitMessage({ status: 'running' });

		expect(a).toEqual([{ status: 'running' }]);
		expect(b).toEqual([{ status: 'running' }]);
	});

	it('opens separate connections for different urls', () => {
		subscribeShared('/api/one', { onMessage: () => {} });
		subscribeShared('/api/two', { onMessage: () => {} });

		expect(openCount()).toBe(2);
		expect(sharedStreamCount()).toBe(2);
	});

	it('keeps the connection open while any subscriber remains', () => {
		const first = subscribeShared('/api/heartbeat', { onMessage: () => {} });
		subscribeShared('/api/heartbeat', { onMessage: () => {} });

		first();

		expect(openCount()).toBe(1);
	});

	it('closes the connection when the last subscriber leaves', () => {
		const first = subscribeShared('/api/heartbeat', { onMessage: () => {} });
		const second = subscribeShared('/api/heartbeat', { onMessage: () => {} });

		first();
		second();

		expect(openCount()).toBe(0);
		expect(sharedStreamCount()).toBe(0);
	});

	it('replays the last payload to a late subscriber', () => {
		subscribeShared('/api/heartbeat', { onMessage: () => {} });
		FakeEventSource.instances[0].emitMessage({ status: 'running' });

		const late: unknown[] = [];
		subscribeShared('/api/heartbeat', { onMessage: (d) => late.push(d) });

		// Without this a panel mounting mid-stream shows nothing until the next
		// push, which for a heartbeat is seconds of blank cards.
		expect(late).toEqual([{ status: 'running' }]);
		expect(openCount()).toBe(1);
	});

	it('unsubscribing twice does not close a connection others are using', () => {
		const first = subscribeShared('/api/heartbeat', { onMessage: () => {} });
		subscribeShared('/api/heartbeat', { onMessage: () => {} });

		first();
		first();

		expect(openCount()).toBe(1);
	});

	it('reopens after every subscriber has left', () => {
		const first = subscribeShared('/api/heartbeat', { onMessage: () => {} });
		first();
		expect(openCount()).toBe(0);

		const messages: unknown[] = [];
		subscribeShared('/api/heartbeat', { onMessage: (d) => messages.push(d) });
		expect(openCount()).toBe(1);

		FakeEventSource.instances.at(-1)!.emitMessage({ status: 'stopped' });
		expect(messages).toEqual([{ status: 'stopped' }]);
	});

	it('a subscriber unsubscribing during a message does not skip the others', () => {
		const seen: string[] = [];
		let dropSelf: (() => void) | null = null;

		dropSelf = subscribeShared('/api/heartbeat', {
			onMessage: () => {
				seen.push('first');
				dropSelf?.();
			}
		});
		subscribeShared('/api/heartbeat', { onMessage: () => seen.push('second') });

		FakeEventSource.instances[0].emitMessage({ tick: 1 });

		expect(seen).toEqual(['first', 'second']);
	});
});
