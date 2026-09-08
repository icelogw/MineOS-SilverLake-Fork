<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import CopyButton from '$lib/components/CopyButton.svelte';
	import type { PageData } from './$types';

	let { data }: { data: PageData } = $props();

	type Scope = { scope: string; description: string };
	type Token = {
		id: number;
		name: string;
		serverName: string;
		tokenPrefix: string;
		scopes: string[];
		allowProxyBackends: boolean;
		createdAt: string;
		expiresAt: string | null;
		lastUsedAt: string | null;
		revoked: boolean;
	};

	const scopes = $derived((data.scopes ?? []) as Scope[]);
	const tokens = $derived((data.tokens?.data ?? []) as Token[]);

	const proxies = $derived((data.proxies ?? []) as string[]);
	const gameServers = $derived((data.gameServers ?? []) as string[]);
	const hasAnyTarget = $derived(proxies.length + gameServers.length > 0);

	let name = $state('');
	let serverName = $state('');
	let selected = $state<string[]>([]);
	let allowProxyBackends = $state(false);

	/**
	 * Proxy authority only means anything on a proxy. When the selection is a
	 * plain game server we say so and force the toggle off, rather than letting
	 * an operator grant an authority that would silently resolve to nothing.
	 */
	const selectionIsProxy = $derived(proxies.includes(serverName));
	const proxyToggleApplies = $derived(!hasAnyTarget || selectionIsProxy);

	$effect(() => {
		if (!proxyToggleApplies && allowProxyBackends) {
			allowProxyBackends = false;
		}
	});

	/**
	 * The backends the chosen proxy currently fronts. Shown before issuing so the
	 * operator can see exactly which servers the token will reach, rather than
	 * granting "and its backends" and hoping that means what they think.
	 */
	let proxyBackends = $state<string[]>([]);
	let loadingBackends = $state(false);

	$effect(() => {
		const name = serverName;
		const isProxy = selectionIsProxy;

		if (!isProxy || !name) {
			proxyBackends = [];
			return;
		}

		let cancelled = false;
		loadingBackends = true;

		fetch(`/api/servers/${encodeURIComponent(name)}/forwarding/backends`)
			.then((res) => (res.ok ? res.json() : null))
			.then((body) => {
				if (cancelled) return;
				proxyBackends = (body?.backends ?? [])
					.map((b: { serverName?: string }) => b.serverName)
					.filter(Boolean);
			})
			.catch(() => {
				// A proxy with no readable config fronts nothing we can show; the
				// token still resolves its reach at request time.
				if (!cancelled) proxyBackends = [];
			})
			.finally(() => {
				if (!cancelled) loadingBackends = false;
			});

		return () => {
			cancelled = true;
		};
	});

	type ExpiryPreset = 'never' | '30d' | '90d' | '1y' | 'custom';

	const expiryPresets: { id: ExpiryPreset; label: string; days?: number }[] = [
		{ id: 'never', label: 'Never' },
		{ id: '30d', label: '30 days', days: 30 },
		{ id: '90d', label: '90 days', days: 90 },
		{ id: '1y', label: '1 year', days: 365 },
		{ id: 'custom', label: 'Pick a date…' }
	];

	let expiryPreset = $state<ExpiryPreset>('never');
	/** Only used when the preset is 'custom'. */
	let expiresAt = $state('');

	/** The expiry to send, as an ISO string, or null for no expiry. */
	function resolveExpiry(): string | null {
		if (expiryPreset === 'never') return null;

		if (expiryPreset === 'custom') {
			if (!expiresAt) return null;
			const picked = new Date(expiresAt);
			return Number.isNaN(picked.getTime()) ? null : picked.toISOString();
		}

		const days = expiryPresets.find((p) => p.id === expiryPreset)?.days;
		if (!days) return null;

		const when = new Date();
		when.setDate(when.getDate() + days);
		return when.toISOString();
	}

	const expirySummary = $derived.by(() => {
		if (expiryPreset === 'never') return 'This token will never expire.';
		if (expiryPreset === 'custom') {
			return expiresAt
				? `Stops working on ${new Date(expiresAt).toLocaleString()}.`
				: 'Pick a date, or it will never expire.';
		}
		const iso = resolveExpiry();
		return iso ? `Stops working on ${new Date(iso).toLocaleDateString()}.` : '';
	});

	let issuing = $state(false);
	let error = $state<string | null>(null);
	// The secret is returned exactly once. Held here until the operator dismisses
	// it, because there is no way to show it again.
	let issuedSecret = $state<string | null>(null);
	let issuedFor = $state<string | null>(null);

	function toggleScope(scope: string) {
		selected = selected.includes(scope)
			? selected.filter((s) => s !== scope)
			: [...selected, scope];
	}

	function describe(scope: string) {
		return scopes.find((s) => s.scope === scope)?.description ?? scope;
	}

	function formatDate(value: string | null) {
		if (!value) return 'never';
		const date = new Date(value);
		return Number.isNaN(date.getTime()) ? 'unknown' : date.toLocaleString();
	}

	function isExpired(token: Token) {
		return token.expiresAt != null && new Date(token.expiresAt).getTime() <= Date.now();
	}

	function statusOf(token: Token) {
		if (token.revoked) return { label: 'revoked', kind: 'dead' };
		if (isExpired(token)) return { label: 'expired', kind: 'dead' };
		return { label: 'active', kind: 'live' };
	}

	async function issue(event: SubmitEvent) {
		event.preventDefault();
		error = null;

		if (!name.trim() || !serverName.trim()) {
			error = 'Name and server are both required.';
			return;
		}
		if (selected.length === 0) {
			error = 'Pick at least one thing this token is allowed to do.';
			return;
		}

		issuing = true;
		try {
			const response = await fetch('/api/plugin-tokens', {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({
					name: name.trim(),
					serverName: serverName.trim(),
					scopes: selected,
					allowProxyBackends,
					expiresAt: resolveExpiry()
				})
			});

			const body = await response.json().catch(() => null);

			if (!response.ok) {
				error = body?.error ?? `Could not issue token (${response.status}).`;
				return;
			}

			issuedSecret = body.secret;
			issuedFor = body.token?.name ?? name.trim();

			name = '';
			serverName = '';
			selected = [];
			allowProxyBackends = false;
			expiryPreset = 'never';
			expiresAt = '';

			await invalidateAll();
		} catch (e) {
			error = e instanceof Error ? e.message : 'Could not issue token.';
		} finally {
			issuing = false;
		}
	}

	async function revoke(token: Token) {
		if (!confirm(`Revoke "${token.name}"? Any plugin using it stops working immediately.`)) {
			return;
		}

		error = null;
		const response = await fetch(`/api/plugin-tokens/${token.id}`, { method: 'DELETE' });

		if (!response.ok) {
			error = `Could not revoke token (${response.status}).`;
			return;
		}

		await invalidateAll();
	}
</script>

<div class="intro">
	<h2>Plugin API</h2>
	<p>
		Lets a Minecraft plugin talk to this panel — read server status, start and stop servers, run
		console commands, and report events. A proxy plugin can restart the backends behind it.
	</p>
	<p class="warn">
		These tokens are <strong>not</strong> the panel API key. The panel API key is full admin access;
		a plugin token is limited to one server — or, on a proxy, that proxy and the servers behind it —
		and only what you switch on below. Give plugins these, never the API key.
	</p>
</div>

{#if issuedSecret}
	<div class="secret-card">
		<h3>Token issued{issuedFor ? ` — ${issuedFor}` : ''}</h3>
		<p>
			Copy this now. It is stored hashed, so <strong>this is the only time it can be shown</strong>.
			If you lose it, revoke the token and issue another.
		</p>
		<div class="secret-row">
			<code>{issuedSecret}</code>
			<CopyButton value={issuedSecret} title="Copy token" size="sm" />
		</div>
		<button class="btn-ghost" onclick={() => (issuedSecret = null)}>I have copied it</button>
	</div>
{/if}

{#if error}
	<div class="error-banner">{error}</div>
{/if}

{#if data.tokens?.error}
	<div class="error-banner">{data.tokens.error}</div>
{/if}

<section class="panel">
	<h3>Issue a token</h3>

	<form onsubmit={issue}>
		<div class="field-row">
			<label class="field wide">
				<span>Token name</span>
				<input bind:value={name} placeholder="hub velocity plugin" />
				<small>So you can recognise it later.</small>
			</label>

			<label class="field">
				<span>Runs on</span>
				{#if hasAnyTarget}
					<select bind:value={serverName}>
						<option value="" disabled>Choose a proxy or server…</option>
						{#if proxies.length > 0}
							<optgroup label="Proxies">
								{#each proxies as proxy}
									<option value={proxy}>{proxy}</option>
								{/each}
							</optgroup>
						{/if}
						{#if gameServers.length > 0}
							<optgroup label="Servers">
								{#each gameServers as server}
									<option value={server}>{server}</option>
								{/each}
							</optgroup>
						{/if}
					</select>
				{:else}
					<!-- Nothing exists yet. A token may still be issued ahead of its
					     server, so fall back to free text rather than blocking. -->
					<input bind:value={serverName} placeholder="hub" />
				{/if}
				<small>The proxy or server this plugin runs on.</small>
			</label>
		</div>

		<div class="expiry">
			<span class="expiry-label">Expires</span>
			<div class="chips">
				{#each expiryPresets as preset}
					<button
						type="button"
						class="chip"
						class:active={expiryPreset === preset.id}
						onclick={() => (expiryPreset = preset.id)}
					>
						{preset.label}
					</button>
				{/each}
			</div>
			{#if expiryPreset === 'custom'}
				<input type="datetime-local" class="custom-date" bind:value={expiresAt} />
			{/if}
			<small>{expirySummary}</small>
		</div>

		<fieldset class="scopes">
			<legend>What this token can do</legend>

			<div class="card-grid">
				{#each scopes as scope}
					{@const on = selected.includes(scope.scope)}
					<div class="property-card">
						<div class="property-header">
							<div class="property-info">
								<span class="property-label"><code>{scope.scope}</code></span>
								<span class="property-hint">{scope.description}</span>
							</div>
						</div>
						<button
							type="button"
							class="toggle-btn"
							class:active={on}
							aria-pressed={on}
							onclick={() => toggleScope(scope.scope)}
						>
							<span class="toggle-track"><span class="toggle-thumb"></span></span>
							<span class="toggle-label">{on ? 'Granted' : 'Not granted'}</span>
						</button>
					</div>
				{/each}
			</div>
		</fieldset>

		<div class="property-card proxy-card" class:inapplicable={!proxyToggleApplies}>
			<div class="property-header">
				<div class="property-info">
					<span class="property-label">Also allow the servers behind this proxy</span>
					<span class="property-hint">
						{#if proxyToggleApplies}
							The token can act on every server behind this proxy, not just the proxy itself.
							The list is read from the proxy's own config each time, so attaching a server
							there grants access and detaching one removes it — nothing to update here.
						{:else if serverName}
							Only applies to a proxy. <strong>{serverName}</strong> is a game server, so this
							token reaches that server and nothing else.
						{:else}
							Only applies to a proxy. Pick one above to enable this.
						{/if}
					</span>

					{#if proxyToggleApplies && selectionIsProxy}
						<span class="reach">
							{#if loadingBackends}
								<span class="reach-label">Checking what this proxy fronts…</span>
							{:else if proxyBackends.length > 0}
								<span class="reach-label">
									This token would reach {proxyBackends.length + 1} servers:
								</span>
								<span class="reach-list">
									<code>{serverName}</code><span class="reach-self">(the proxy)</span>
									{#each proxyBackends as backend}
										<code>{backend}</code>
									{/each}
								</span>
							{:else}
								<span class="reach-label">
									This proxy currently fronts no backends, so the token would reach only
									<code>{serverName}</code>. It picks up backends automatically as you attach them.
								</span>
							{/if}
						</span>
					{/if}
				</div>
			</div>
			<button
				type="button"
				class="toggle-btn"
				class:active={allowProxyBackends}
				aria-pressed={allowProxyBackends}
				disabled={!proxyToggleApplies}
				onclick={() => (allowProxyBackends = !allowProxyBackends)}
			>
				<span class="toggle-track"><span class="toggle-thumb"></span></span>
				<span class="toggle-label">
					{proxyToggleApplies ? (allowProxyBackends ? 'Enabled' : 'Disabled') : 'Not applicable'}
				</span>
			</button>
		</div>

		<button type="submit" class="btn-primary" disabled={issuing}>
			{issuing ? 'Issuing…' : 'Issue token'}
		</button>
	</form>
</section>

<section class="panel">
	<h3>Issued tokens</h3>

	{#if tokens.length === 0}
		<p class="empty">No plugin tokens yet.</p>
	{:else}
		<div class="token-list">
			{#each tokens as token}
				{@const status = statusOf(token)}
				<div class="token" class:dead={status.kind === 'dead'}>
					<div class="token-head">
						<div>
							<strong>{token.name}</strong>
							<span class="badge {status.kind}">{status.label}</span>
							{#if token.allowProxyBackends}
								<span class="badge proxy">+ proxy backends</span>
							{/if}
						</div>
						{#if !token.revoked}
							<button class="btn-danger" onclick={() => revoke(token)}>Revoke</button>
						{/if}
					</div>

					<div class="token-meta">
						<span>server <code>{token.serverName}</code></span>
						<span><code>{token.tokenPrefix}…</code></span>
						<span>last used {formatDate(token.lastUsedAt)}</span>
						{#if token.expiresAt}
							<span>expires {formatDate(token.expiresAt)}</span>
						{/if}
					</div>

					<ul class="token-scopes">
						{#each token.scopes as scope}
							<li><code>{scope}</code> <span class="scope-desc">{describe(scope)}</span></li>
						{/each}
					</ul>
				</div>
			{/each}
		</div>
	{/if}
</section>

<style>
	.intro h2 {
		margin: 0 0 8px;
		font-size: 20px;
	}

	.intro p {
		margin: 0 0 8px;
		color: #8890b1;
		font-size: 14px;
		line-height: 1.6;
		max-width: 80ch;
	}

	.intro .warn {
		color: #d7b16b;
	}

	.panel {
		background: #161a2b;
		border: 1px solid #2a2f47;
		border-radius: 8px;
		padding: 20px;
		margin-top: 24px;
	}

	.panel h3 {
		margin: 0 0 16px;
		font-size: 16px;
	}

	.field-row {
		display: flex;
		flex-wrap: wrap;
		gap: 16px;
	}

	.field {
		display: flex;
		flex-direction: column;
		gap: 6px;
		flex: 1 1 220px;
		min-width: 0;
	}

	.field > span {
		font-size: 13px;
		font-weight: 500;
	}

	.field.wide {
		flex: 2 1 320px;
	}

	.field input,
	.field select {
		background: #0f1220;
		border: 1px solid #2a2f47;
		border-radius: 6px;
		padding: 9px 11px;
		color: inherit;
		font: inherit;
		min-width: 0;
		/* Without this the browser paints the open dropdown list and the native
		   arrow in its light default, which is unreadable over this panel. */
		color-scheme: dark;
	}

	/* Replace the native arrow with one that matches the rest of the panel, and
	   keep room for it so a long server name never runs underneath. */
	.field select {
		appearance: none;
		background-image: linear-gradient(45deg, transparent 50%, #8890b1 50%),
			linear-gradient(135deg, #8890b1 50%, transparent 50%);
		background-position:
			calc(100% - 18px) calc(50% + 2px),
			calc(100% - 13px) calc(50% + 2px);
		background-size:
			5px 5px,
			5px 5px;
		background-repeat: no-repeat;
		padding-right: 34px;
		cursor: pointer;
	}

	.field select:hover {
		border-color: rgba(106, 176, 76, 0.4);
	}

	.field input:focus,
	.field select:focus {
		outline: none;
		border-color: var(--mc-grass);
	}

	/* Expiry: presets cover almost every real choice, so the raw date input only
	   appears when someone genuinely wants an arbitrary moment. */
	.expiry {
		margin-top: 20px;
		display: flex;
		flex-direction: column;
		gap: 8px;
		align-items: flex-start;
	}

	.expiry-label {
		font-size: 13px;
		font-weight: 500;
	}

	.chips {
		display: flex;
		flex-wrap: wrap;
		gap: 8px;
	}

	.chip {
		background: rgba(20, 24, 39, 0.6);
		border: 1px solid rgba(42, 47, 71, 0.8);
		border-radius: 999px;
		padding: 7px 16px;
		color: #8890b1;
		font: inherit;
		font-size: 13px;
		cursor: pointer;
		transition: all 0.15s;
	}

	.chip:hover {
		border-color: rgba(88, 101, 242, 0.4);
		color: #c9d1f2;
	}

	.chip.active {
		background: rgba(106, 176, 76, 0.15);
		border-color: rgba(106, 176, 76, 0.5);
		color: #7ae68d;
		font-weight: 500;
	}

	.custom-date {
		background: #0f1220;
		border: 1px solid #2a2f47;
		border-radius: 6px;
		padding: 9px 11px;
		color: inherit;
		font: inherit;
		color-scheme: dark;
	}

	.custom-date:focus {
		outline: none;
		border-color: var(--mc-grass);
	}

	small {
		color: #8890b1;
		font-size: 12px;
	}

	.scopes {
		border: 1px solid #2a2f47;
		border-radius: 6px;
		padding: 14px;
		margin: 20px 0 0;
		display: flex;
		flex-direction: column;
		gap: 8px;
	}

	.scopes legend {
		padding: 0 6px;
		font-size: 13px;
		font-weight: 500;
	}

	/* Cards and toggles match the server config page so a scope reads the same
	   way as any other on/off setting in the panel. */
	.card-grid {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
		gap: 12px;
	}

	.property-card {
		background: rgba(20, 24, 39, 0.6);
		border-radius: 12px;
		padding: 16px;
		border: 1px solid rgba(42, 47, 71, 0.6);
		transition: border-color 0.2s;
		display: flex;
		flex-direction: column;
	}

	.property-card:hover {
		border-color: rgba(88, 101, 242, 0.3);
	}

	.proxy-card {
		margin-top: 16px;
	}

	.proxy-card.inapplicable {
		opacity: 0.6;
	}

	.toggle-btn:disabled {
		cursor: not-allowed;
	}

	.property-header {
		margin-bottom: 12px;
		/* Pushes the toggle to the card's bottom edge so a row of cards with
		   different description lengths still lines its switches up. */
		flex: 1;
	}

	.property-info {
		display: flex;
		flex-direction: column;
		gap: 4px;
	}

	.property-label {
		display: block;
		font-size: 14px;
		font-weight: 600;
		color: #eef0f8;
	}

	.property-hint {
		display: block;
		font-size: 12px;
		color: #6a7192;
		line-height: 1.5;
	}

	.reach {
		display: block;
		margin-top: 10px;
		padding-top: 10px;
		border-top: 1px solid rgba(42, 47, 71, 0.8);
	}

	.reach-label {
		display: block;
		font-size: 12px;
		color: #8890b1;
		line-height: 1.5;
	}

	.reach-list {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: 6px;
		margin-top: 6px;
	}

	.reach-self {
		font-size: 11px;
		color: #6a7192;
	}

	.toggle-btn {
		display: flex;
		align-items: center;
		gap: 12px;
		padding: 8px 12px;
		background: rgba(255, 92, 92, 0.1);
		border: 1px solid rgba(255, 92, 92, 0.2);
		border-radius: 8px;
		cursor: pointer;
		transition: all 0.2s;
		width: 100%;
	}

	.toggle-btn.active {
		background: rgba(106, 176, 76, 0.15);
		border-color: rgba(106, 176, 76, 0.3);
	}

	.toggle-track {
		position: relative;
		width: 44px;
		height: 24px;
		background: rgba(255, 92, 92, 0.3);
		border-radius: 12px;
		transition: background 0.2s;
		flex-shrink: 0;
	}

	.toggle-btn.active .toggle-track {
		background: rgba(106, 176, 76, 0.5);
	}

	.toggle-thumb {
		position: absolute;
		top: 3px;
		left: 3px;
		width: 18px;
		height: 18px;
		background: #ff9f9f;
		border-radius: 50%;
		transition: all 0.2s;
		box-shadow: 0 2px 4px rgba(0, 0, 0, 0.3);
	}

	.toggle-btn.active .toggle-thumb {
		left: 23px;
		background: #7ae68d;
	}

	.toggle-label {
		font-size: 13px;
		font-weight: 500;
		color: #ff9f9f;
	}

	.toggle-btn.active .toggle-label {
		color: #7ae68d;
	}

	.scope-body {
		display: flex;
		flex-direction: column;
		gap: 3px;
		min-width: 0;
	}

	.scope-desc {
		color: #8890b1;
		font-size: 13px;
		line-height: 1.5;
	}

	code {
		background: #0f1220;
		border-radius: 4px;
		padding: 1px 6px;
		font-size: 12px;
	}

	.btn-primary {
		margin-top: 20px;
		background: var(--mc-grass);
		border: none;
		border-radius: 6px;
		padding: 10px 20px;
		color: #0f1220;
		font-weight: 600;
		cursor: pointer;
	}

	.btn-primary:disabled {
		opacity: 0.6;
		cursor: not-allowed;
	}

	.btn-danger {
		background: transparent;
		border: 1px solid #6b2b2b;
		color: #d78b8b;
		border-radius: 6px;
		padding: 5px 12px;
		cursor: pointer;
		font-size: 13px;
	}

	.btn-danger:hover {
		background: #2a1616;
	}

	.btn-ghost {
		background: transparent;
		border: 1px solid #2a2f47;
		color: inherit;
		border-radius: 6px;
		padding: 7px 14px;
		cursor: pointer;
		margin-top: 12px;
		font-size: 13px;
	}

	.secret-card {
		background: #172415;
		border: 1px solid var(--mc-grass);
		border-radius: 8px;
		padding: 18px;
		margin-top: 20px;
	}

	.secret-card h3 {
		margin: 0 0 8px;
		font-size: 16px;
	}

	.secret-card p {
		margin: 0 0 12px;
		font-size: 14px;
		color: #b8c0dd;
		max-width: 80ch;
	}

	.secret-row {
		display: flex;
		gap: 10px;
		align-items: center;
		flex-wrap: wrap;
	}

	.secret-row code {
		font-size: 14px;
		padding: 10px 12px;
		word-break: break-all;
		flex: 1 1 320px;
	}

	.error-banner {
		background: #2a1616;
		border: 1px solid #6b2b2b;
		color: #d78b8b;
		border-radius: 6px;
		padding: 11px 14px;
		margin-top: 16px;
		font-size: 14px;
	}

	.empty {
		color: #8890b1;
		font-size: 14px;
		margin: 0;
	}

	.token-list {
		display: flex;
		flex-direction: column;
		gap: 12px;
	}

	.token {
		border: 1px solid #2a2f47;
		border-radius: 6px;
		padding: 14px;
	}

	.token.dead {
		opacity: 0.55;
	}

	.token-head {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 12px;
		flex-wrap: wrap;
	}

	.badge {
		font-size: 11px;
		padding: 2px 8px;
		border-radius: 10px;
		margin-left: 8px;
		text-transform: uppercase;
		letter-spacing: 0.04em;
	}

	.badge.live {
		background: #1e3a1e;
		color: #8fd47f;
	}

	.badge.dead {
		background: #2a1616;
		color: #d78b8b;
	}

	.badge.proxy {
		background: #1b2740;
		color: #8fb4e8;
	}

	.token-meta {
		display: flex;
		gap: 16px;
		flex-wrap: wrap;
		margin-top: 8px;
		color: #8890b1;
		font-size: 12px;
	}

	.token-scopes {
		list-style: none;
		margin: 12px 0 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: 5px;
	}

	.token-scopes li {
		font-size: 13px;
	}

	@media (max-width: 640px) {
		.field-row {
			flex-direction: column;
		}
	}
</style>
