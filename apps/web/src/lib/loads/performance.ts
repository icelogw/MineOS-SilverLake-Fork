import * as api from '$lib/api/client';

type LoadEvent = {
	params: { name: string };
	fetch: typeof globalThis.fetch;
	url?: URL;
};

/** Windows offered by the Performance tab, in minutes. */
export const PERFORMANCE_RANGES = [
	{ id: '1h', label: '1 hour', minutes: 60 },
	{ id: '6h', label: '6 hours', minutes: 360 },
	{ id: '24h', label: '24 hours', minutes: 1440 },
	{ id: '7d', label: '7 days', minutes: 10080 },
	{ id: '30d', label: '30 days', minutes: 43200 }
] as const;

export const DEFAULT_PERFORMANCE_RANGE = '1h';

function resolveRange(url: URL | undefined) {
	const requested = url?.searchParams.get('range');
	return (
		PERFORMANCE_RANGES.find((r) => r.id === requested) ??
		PERFORMANCE_RANGES.find((r) => r.id === DEFAULT_PERFORMANCE_RANGE)!
	);
}

/**
 * Performance data for a server's Performance tab. Shared by /servers/[name]
 * and /proxies/[name] — both address the server by name, so the same load
 * serves both sections.
 */
export async function loadPerformance({ params, fetch, url }: LoadEvent) {
	// The window is in the URL, so a chosen range survives a refresh and can be
	// linked to — the same reason the server tabs are routes rather than state.
	const range = resolveRange(url);

	const [history, realtime, spark] = await Promise.all([
		api.getPerformanceHistory(fetch, params.name, range.minutes),
		api.getPerformanceRealtime(fetch, params.name),
		api.getSparkStatus(fetch, params.name)
	]);

	return {
		history,
		realtime,
		spark,
		range: range.id,
		ranges: PERFORMANCE_RANGES.map((r) => ({ id: r.id, label: r.label }))
	};
}
