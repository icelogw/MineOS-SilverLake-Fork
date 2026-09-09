import { redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';
import * as api from '$lib/api/client';

export const load: LayoutServerLoad = async ({ cookies, fetch, url }) => {
	const token = cookies.get('auth_token');

	if (!token) {
		throw redirect(303, '/login');
	}

	let user = null;
	try {
		const meResponse = await fetch('/api/auth/me');
		if (meResponse.ok) {
			user = await meResponse.json();
		} else if (meResponse.status === 401 || meResponse.status === 403) {
			throw redirect(303, '/login');
		}
	} catch {
		// Network/API error - redirect to login
		throw redirect(303, '/login');
	}

	if (!user) {
		// Could not get user info, force re-login
		throw redirect(303, '/login');
	}

	if (url.pathname.startsWith('/profiles/buildtools')) {
		console.info('[layout] buildtools load', url.pathname, url.search);
	}

	// Load servers and profiles for search, plus the build version the sidebar
	// and top bar both show. Joining the existing Promise.all costs no extra
	// round trip, and it replaces the client-side fetch TopBar used to make.
	const [servers, profiles, meta] = await Promise.all([
		api.getAllServers(fetch),
		api.getHostProfiles(fetch),
		api.getMeta(fetch)
	]);

	return {
		user,
		servers: servers.data ?? [],
		profiles: profiles.data ?? [],
		version: meta.data?.version ?? null
	};
};
