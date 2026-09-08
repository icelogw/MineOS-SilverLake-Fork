import { proxyJson } from '$lib/server/proxyJson';
import type { RequestHandler } from './$types';

export const GET: RequestHandler = async (event) => {
	return proxyJson(event, '/api/v1/plugin-tokens');
};

export const POST: RequestHandler = async (event) => {
	const body = await event.request.json();
	return proxyJson(event, '/api/v1/plugin-tokens', {
		method: 'POST',
		body: JSON.stringify(body)
	});
};
