import { createEventStream, type EventStreamHandle } from './eventStream';

/**
 * Shares one SSE connection between every component that wants the same URL.
 *
 * Browsers allow only six concurrent HTTP/1.1 connections per origin, and an SSE
 * stream holds one open for as long as the page lives. The server pages were
 * opening six at once — two of them the *same* heartbeat URL, from the shell and
 * the overview panel independently — which used the entire budget and left later
 * requests queued forever: pages, API calls and even JavaScript stopped loading.
 *
 * Subscribers here are reference counted. The first opens the connection, the
 * last to leave closes it, and everyone in between shares the one socket.
 */

interface SharedEntry {
	handle: EventStreamHandle | null;
	subscribers: Set<(data: unknown) => void>;
	giveUpHandlers: Set<() => void>;
	/** Last payload seen, replayed to late subscribers so they are not blank. */
	last?: unknown;
}

const streams = new Map<string, SharedEntry>();

export interface SharedStreamOptions<T> {
	onMessage: (data: T) => void;
	/**
	 * Called when the underlying stream gives up reconnecting. Shared by all
	 * subscribers, since the connection they share is the thing that failed.
	 */
	onGiveUp?: () => void;
}

/**
 * Subscribes to the stream at <paramref name="url" />, opening it if nobody else
 * has. Returns an unsubscribe function; call it on component teardown.
 */
export function subscribeShared<T>(url: string, options: SharedStreamOptions<T>): () => void {
	const { onMessage, onGiveUp } = options;
	const listener = onMessage as (data: unknown) => void;

	let entry = streams.get(url);

	if (!entry) {
		entry = {
			handle: null,
			subscribers: new Set(),
			giveUpHandlers: new Set()
		};
		streams.set(url, entry);

		const created = entry;
		created.handle = createEventStream<unknown>({
			url,
			onMessage: (data) => {
				created.last = data;
				// Copied before iterating: a subscriber may unsubscribe in response
				// to a message, and mutating the set mid-iteration would skip others.
				for (const fn of [...created.subscribers]) {
					fn(data);
				}
			},
			onClose: () => {
				for (const fn of [...created.giveUpHandlers]) {
					fn();
				}
			},
			reconnect: {}
		});
	}

	entry.subscribers.add(listener);
	if (onGiveUp) {
		entry.giveUpHandlers.add(onGiveUp);
	}

	// A component mounting after the stream is already running would otherwise
	// show nothing until the next server push, which for a heartbeat is seconds.
	if (entry.last !== undefined) {
		listener(entry.last);
	}

	let released = false;

	return () => {
		if (released) return;
		released = true;

		const current = streams.get(url);
		if (!current) return;

		current.subscribers.delete(listener);
		if (onGiveUp) {
			current.giveUpHandlers.delete(onGiveUp);
		}

		if (current.subscribers.size === 0) {
			current.handle?.close();
			streams.delete(url);
		}
	};
}

/** Open shared streams, for tests and diagnostics. */
export function sharedStreamCount(): number {
	return streams.size;
}

/** Test seam: drops every shared stream. */
export function resetSharedStreams(): void {
	for (const entry of streams.values()) {
		entry.handle?.close();
	}
	streams.clear();
}
