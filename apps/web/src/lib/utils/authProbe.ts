/**
 * Asks the server whether this session is still valid, for use when a stream
 * drops and we need to tell "your session expired" apart from "the connection
 * blipped".
 *
 * Why this exists rather than a bare `fetch('/api/auth/me')`:
 *
 * The probe is fired from an EventSource `onerror`, and a stream errors during
 * ordinary navigation as the old page is torn down. By the time it runs, the
 * page being navigated to has already opened its own streams. A browser allows
 * only six concurrent HTTP/1.1 connections per origin and an SSE stream holds
 * one for as long as it lives, so the probe cannot get a socket — and with no
 * timeout it waits forever, leaving a dead request behind on every visit. Those
 * accumulate until nothing on the page can load and only a refresh recovers it.
 *
 * A timeout bounds it: the worst case becomes a probe that gives up and treats
 * the outcome as unknown, rather than one that never finishes.
 */
export type AuthProbeResult = 'authenticated' | 'unauthenticated' | 'unknown';

/** Long enough for a healthy server, short enough to never strand a socket. */
const PROBE_TIMEOUT_MS = 5000;

export async function probeAuth(timeoutMs = PROBE_TIMEOUT_MS): Promise<AuthProbeResult> {
	// AbortSignal.timeout is not in every browser this may run in, so fall back
	// to a controller rather than losing the timeout entirely.
	const controller = new AbortController();
	const timer = setTimeout(() => controller.abort(), timeoutMs);

	try {
		const res = await fetch('/api/auth/me', { signal: controller.signal });
		if (res.status === 401 || res.status === 403) {
			return 'unauthenticated';
		}
		return res.ok ? 'authenticated' : 'unknown';
	} catch {
		// Aborted, offline, or the request never got a socket. Not evidence the
		// session is gone, so never redirect on this.
		return 'unknown';
	} finally {
		clearTimeout(timer);
	}
}
