import { proxyJson } from '$lib/server/proxyJson';
import type { RequestHandler } from './$types';

export const DELETE: RequestHandler = async (event) => {
	return proxyJson(event, `/api/v1/plugin-tokens/${encodeURIComponent(event.params.id)}`, {
		method: 'DELETE'
	});
};
