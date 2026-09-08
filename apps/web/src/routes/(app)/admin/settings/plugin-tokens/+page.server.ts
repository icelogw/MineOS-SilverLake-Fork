import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch }) => {
	const [tokensRes, scopesRes, serversRes] = await Promise.all([
		fetch('/api/plugin-tokens'),
		fetch('/api/plugin-tokens/scopes'),
		// Used to offer real names when issuing. Not fatal if it fails — the form
		// falls back to free text.
		fetch('/api/servers')
	]);

	const servers = serversRes.ok ? await serversRes.json() : [];
	const list: { name: string; serverType?: string }[] = Array.isArray(servers) ? servers : [];

	// Split the same way the Proxies page does: serverType 'proxy' is a
	// Velocity/BungeeCord front, everything else is a game server. The
	// distinction matters here because only a proxy token can meaningfully
	// carry authority over backends.
	return {
		tokens: {
			data: tokensRes.ok ? await tokensRes.json() : [],
			error: tokensRes.ok ? null : `Failed to load plugin tokens (${tokensRes.status})`
		},
		scopes: scopesRes.ok ? await scopesRes.json() : [],
		proxies: list.filter((s) => s.serverType === 'proxy').map((s) => s.name).filter(Boolean),
		gameServers: list.filter((s) => s.serverType !== 'proxy').map((s) => s.name).filter(Boolean)
	};
};
