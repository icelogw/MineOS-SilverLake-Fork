<script lang="ts">
	import { onDestroy, onMount } from 'svelte';
	import { page } from '$app/stores';
	import { invalidateAll } from '$app/navigation';
	import { modal } from '$lib/stores/modal';
	import type { PageData } from './$types';

	let { data }: { data: PageData } = $props();

	let actionLoading = $state<Record<string, boolean>>({});
	let copyTargets = $state<Record<string, string>>({});
	let searchQuery = $state('');
	let groupFilter = $state('all');
	let statusFilter = $state<'all' | 'downloaded' | 'missing'>('all');
	let sortOption = $state<'name' | 'group' | 'version'>('name');
	// Descending by default: landing on the newest builds is what you almost
	// always want from a jar library.
	let sortDirection = $state<'asc' | 'desc'>('desc');

	/**
	 * Numeric collation, so "1.9" sorts before "1.10" rather than after it.
	 * A plain localeCompare orders these as text and puts 1.10 first, which
	 * makes "highest version" name the wrong profile.
	 */
	const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

	/** Ascending means something different for a version than for a name. */
	const directionLabels = $derived(
		sortOption === 'version'
			? { asc: 'Lowest first', desc: 'Highest first' }
			: { asc: 'A to Z', desc: 'Z to A' }
	);
	let currentPage = $state(1);
	let pageSize = $state(8);

	let buildGroup = $state('spigot');
	let buildVersion = $state('');
	let buildError = $state('');
	let buildStatus = $state<'idle' | 'running' | 'completed' | 'failed'>('idle');
	let runId = $state<string | null>(null);
	let profileId = $state<string | null>(null);
	let runs = $state<any[]>([]);
	let runsLoading = $state(false);
	let logs = $state<string[]>([]);
	let logContainer: HTMLDivElement | null = null;
	let eventSource: EventSource | null = null;

	// Check for group query param on mount
	onMount(() => {
		const groupParam = $page.url.searchParams.get('group');
		if (groupParam) {
			groupFilter = groupParam;
			// Select the tab that contains this group, so a deep link does not land
			// on a page whose highlighted category disagrees with what is listed.
			categoryFilter = categoryForGroup(groupParam);
		}
		loadRuns();
		const lastRun = localStorage.getItem('mineos_buildtools_run');
		if (lastRun) {
			attachRun(lastRun);
		}
	});

	const profiles = $derived(data.profiles.data ?? []);
	const servers = $derived(data.servers.data ?? []);
	const downloadedCount = $derived.by(() => profiles.filter((profile) => profile.downloaded).length);
	const missingCount = $derived.by(() => profiles.length - downloadedCount);

	/**
	 * Categories for the profile list, in the shape the server Config page uses
	 * for its property sections.
	 *
	 * These bundle related groups rather than listing every raw one: bedrock and
	 * its preview channel are one thing to a reader, as are the two proxies. The
	 * flat chip row named each group separately, which meant six near-identical
	 * buttons and no indication that any of them belonged together.
	 */
	const PROFILE_CATEGORIES = [
		{ id: 'all', label: 'All', icon: '[A]', groups: null as string[] | null },
		{ id: 'vanilla', label: 'Vanilla', icon: '[V]', groups: ['vanilla'] },
		{ id: 'paper', label: 'Paper', icon: '[P]', groups: ['paper'] },
		{ id: 'bedrock', label: 'Bedrock', icon: '[B]', groups: ['bedrock-server', 'bedrock-server-preview'] },
		{ id: 'proxies', label: 'Proxies', icon: '[X]', groups: ['bungeecord', 'velocity'] },
		{ id: 'buildtools', label: 'BuildTools', icon: '[S]', groups: ['spigot', 'craftbukkit'] }
	];

	/** Which category a group belongs to; anything unrecognised falls to "other". */
	function categoryForGroup(group: string): string {
		for (const category of PROFILE_CATEGORIES) {
			if (category.groups?.includes(group)) return category.id;
		}
		return 'other';
	}

	let categoryFilter = $state('all');

	/**
	 * Which half of this page is on screen.
	 *
	 * The two were side by side, with BuildTools pinned to a fixed 360px column.
	 * That cost the profile grid a third of the width for a panel that is idle
	 * most of the time, and left a tall empty gap under it. They are separate
	 * tasks, so they get separate views.
	 */
	let activeView = $state<'profiles' | 'buildtools'>('profiles');

	/**
	 * Only categories that actually have profiles, with their counts. "Other"
	 * appears only when a group turns up that none of the categories claim — so a
	 * server type added upstream is still reachable rather than silently
	 * disappearing from the page.
	 */
	const visibleCategories = $derived.by(() => {
		const counts = new Map<string, number>();
		for (const profile of profiles) {
			const id = categoryForGroup(profile.group ?? '');
			counts.set(id, (counts.get(id) ?? 0) + 1);
		}

		const listed = PROFILE_CATEGORIES.filter(
			(category) => category.id === 'all' || (counts.get(category.id) ?? 0) > 0
		).map((category) => ({
			...category,
			count: category.id === 'all' ? profiles.length : (counts.get(category.id) ?? 0)
		}));

		const otherCount = counts.get('other') ?? 0;
		if (otherCount > 0) {
			listed.push({ id: 'other', label: 'Other', icon: '[?]', groups: null, count: otherCount });
		}

		return listed;
	});

	const filteredProfiles = $derived.by(() => {
		const query = searchQuery.trim().toLowerCase();
		const list = profiles.filter((profile) => {
			if (query) {
				const haystack = `${profile.id} ${profile.group} ${profile.version} ${profile.filename ?? ''}`.toLowerCase();
				if (!haystack.includes(query)) return false;
			}

			// An exact ?group= link still filters to that one group; the category
			// tabs filter to a set of them.
			if (groupFilter !== 'all' && profile.group !== groupFilter) {
				return false;
			}

			if (categoryFilter !== 'all' && categoryForGroup(profile.group ?? '') !== categoryFilter) {
				return false;
			}

			if (statusFilter === 'downloaded' && !profile.downloaded) return false;
			if (statusFilter === 'missing' && profile.downloaded) return false;

			return true;
		});

		return list.sort((a, b) => {
			let result: number;
			switch (sortOption) {
				case 'group':
					// Within a group, fall back to version - otherwise the run of
					// 96 vanilla profiles has no order of its own.
					result =
						collator.compare(a.group, b.group) || collator.compare(a.version, b.version);
					break;
				case 'version':
					result = collator.compare(a.version, b.version);
					break;
				default:
					result = collator.compare(a.id, b.id);
			}
			return sortDirection === 'desc' ? -result : result;
		});
	});

	const totalPages = $derived.by(() =>
		Math.max(1, Math.ceil(filteredProfiles.length / pageSize))
	);

	const pagedProfiles = $derived.by(() => {
		const start = (currentPage - 1) * pageSize;
		return filteredProfiles.slice(start, start + pageSize);
	});

	const paginationPages = $derived.by(() => {
		const pages: (number | string)[] = [];
		const max = totalPages;
		const current = currentPage;
		const window = 2;
		const start = Math.max(1, current - window);
		const end = Math.min(max, current + window);

		if (start > 1) pages.push(1);
		if (start > 2) pages.push('...');

		for (let i = start; i <= end; i += 1) {
			pages.push(i);
		}

		if (end < max - 1) pages.push('...');
		if (end < max) pages.push(max);

		return pages;
	});

	const rangeStart = $derived.by(() =>
		filteredProfiles.length === 0 ? 0 : (currentPage - 1) * pageSize + 1
	);
	const rangeEnd = $derived.by(() =>
		Math.min(currentPage * pageSize, filteredProfiles.length)
	);

	const commonVersions = [
		'latest',
		'1.21.4',
		'1.21.3',
		'1.21.1',
		'1.21',
		'1.20.6',
		'1.20.4',
		'1.20.2',
		'1.20.1',
		'1.20',
		'1.19.4',
		'1.19.3',
		'1.19.2',
		'1.19.1',
		'1.19',
		'1.18.2',
		'1.18.1',
		'1.17.1',
		'1.16.5'
	];

	async function handleDownload(profileId: string) {
		actionLoading[profileId] = true;
		try {
			const res = await fetch(`/api/host/profiles/${profileId}/download`, { method: 'POST' });
			if (!res.ok) {
				const error = await res.json().catch(() => ({ error: 'Failed to download profile' }));
				await modal.error(error.error || 'Failed to download profile');
			} else {
				await invalidateAll();
			}
		} finally {
			delete actionLoading[profileId];
			actionLoading = { ...actionLoading };
		}
	}

	async function handleCopy(profileId: string) {
		const target = copyTargets[profileId];
		if (!target) {
			await modal.alert('Select a server to copy this profile to.', 'Select Server');
			return;
		}

		actionLoading[profileId] = true;
		try {
			const res = await fetch(`/api/host/profiles/${profileId}/copy-to-server`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ serverName: target })
			});
			if (!res.ok) {
				const error = await res.json().catch(() => ({ error: 'Failed to copy profile' }));
				await modal.error(error.error || 'Failed to copy profile');
			} else {
				await invalidateAll();
			}
		} finally {
			delete actionLoading[profileId];
			actionLoading = { ...actionLoading };
		}
	}

	async function handleDelete(profileId: string) {
		const confirmed = await modal.confirm(`Delete BuildTools profile "${profileId}"?`, 'Delete Profile');
		if (!confirmed) {
			return;
		}

		actionLoading[profileId] = true;
		try {
			const res = await fetch(`/api/host/profiles/buildtools/${profileId}`, {
				method: 'DELETE'
			});
			if (!res.ok) {
				const error = await res.json().catch(() => ({ error: 'Failed to delete profile' }));
				await modal.error(error.error || 'Failed to delete profile');
			} else {
				await invalidateAll();
			}
		} finally {
			delete actionLoading[profileId];
			actionLoading = { ...actionLoading };
		}
	}

	$effect(() => {
		if (logContainer) {
			logContainer.scrollTop = logContainer.scrollHeight;
		}
	});

	async function startBuild() {
		if (!buildVersion.trim()) {
			buildError = 'Version is required';
			return;
		}

		buildError = '';
		buildStatus = 'running';
		logs = [];
		runId = null;
		profileId = null;

		try {
			const res = await fetch('/api/host/profiles/buildtools', {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ group: buildGroup, version: buildVersion.trim() })
			});

			const payload = await res.json().catch(() => null);
			if (!res.ok) {
				buildStatus = 'failed';
				buildError = payload?.error || 'Failed to start BuildTools';
				return;
			}

			runId = payload.runId;
			profileId = payload.profileId;
			buildStatus = payload.status ?? 'running';

			if (runId) {
				localStorage.setItem('mineos_buildtools_run', runId);
				openStream(runId);
				await loadRuns();
			}
		} catch (err) {
			buildStatus = 'failed';
			buildError = err instanceof Error ? err.message : 'Failed to start BuildTools';
		}
	}

	function openStream(id: string) {
		eventSource?.close();
		eventSource = new EventSource(`/api/host/profiles/buildtools/runs/${id}/stream`);

		eventSource.onmessage = (event) => {
			try {
				const entry = JSON.parse(event.data);
				if (entry?.message) {
					logs = [...logs, entry.message];
				}
				if (entry?.status) {
					buildStatus = entry.status;
					if (entry.status !== 'running') {
						eventSource?.close();
						eventSource = null;
					}
				}
			} catch {
				logs = [...logs, event.data];
			}
		};

		eventSource.onerror = () => {
			if (buildStatus === 'running') {
				buildError = 'Log stream disconnected. Check the server logs.';
				buildStatus = 'failed';
			}
			eventSource?.close();
			eventSource = null;
		};
	}

	function attachRun(id: string) {
		const run = runs.find((item) => item.runId === id);
		runId = id;
		logs = [];
		buildError = '';
		buildStatus = run?.status ?? 'running';
		buildGroup = run?.group ?? buildGroup;
		buildVersion = run?.version ?? buildVersion;
		profileId = run?.profileId ?? profileId;
		openStream(id);
		localStorage.setItem('mineos_buildtools_run', id);
	}

	async function loadRuns() {
		if (runsLoading) return;
		runsLoading = true;
		try {
			const res = await fetch('/api/host/profiles/buildtools/runs');
			if (res.ok) {
				runs = await res.json();
			}
		} finally {
			runsLoading = false;
		}
	}

	$effect(() => {
		searchQuery;
		groupFilter;
		statusFilter;
		sortOption;
		sortDirection;
		pageSize;
		currentPage = 1;
	});

	$effect(() => {
		if (currentPage > totalPages) currentPage = totalPages;
	});

	onDestroy(() => {
		eventSource?.close();
	});
</script>

<div class="page-header">
	<div class="header-copy">
		<h1>Profiles</h1>
		<p class="subtitle">Manage Minecraft server jars and BuildTools builds.</p>
	</div>

	<!-- On the header's own row rather than stacked under the subtitle: the
	     right side was left empty when the BuildTools button became a view tab,
	     and a third line of chips pushed the tabs further from the title. -->
	<div class="stat-row">
		<div class="stat-chip">
			<span class="stat-label">Total</span>
			<span class="stat-value">{profiles.length}</span>
		</div>
		<div class="stat-chip success">
			<span class="stat-label">Ready</span>
			<span class="stat-value">{downloadedCount}</span>
		</div>
		<div class="stat-chip warning">
			<span class="stat-label">Missing</span>
			<span class="stat-value">{missingCount}</span>
		</div>
	</div>
</div>

<!-- Top-level views. The old header button was an anchor jump to a panel that
     was already on screen; these actually switch what the page is showing. -->
<div class="view-tabs">
	<button
		type="button"
		class="view-tab"
		class:active={activeView === 'profiles'}
		onclick={() => (activeView = 'profiles')}
	>
		<span class="tab-icon">[P]</span>
		<span class="tab-label">Profiles</span>
		<span class="tab-badge">{profiles.length}</span>
	</button>
	<button
		type="button"
		class="view-tab"
		class:active={activeView === 'buildtools'}
		onclick={() => (activeView = 'buildtools')}
	>
		<span class="tab-icon">[B]</span>
		<span class="tab-label">BuildTools Station</span>
	</button>
</div>

<div class="profiles-shell">
	{#if activeView === 'profiles'}
	<section class="library-panel">
		<div class="library-toolbar">
			<div class="search-field">
				<label for="profile-search">Search profiles</label>
				<input
					id="profile-search"
					type="text"
					bind:value={searchQuery}
					placeholder="Search profiles..."
				/>
			</div>
			<div class="toolbar-row">
				<!-- Same shape as the server Config page's property sections, so the
				     two read as the same idea. -->
				<div class="section-tabs">
					{#each visibleCategories as category}
						<button
							type="button"
							class="section-tab"
							class:active={categoryFilter === category.id}
							onclick={() => {
								categoryFilter = category.id;
								// Clear any exact-group filter a ?group= link set, or the
								// two would fight and show nothing.
								groupFilter = 'all';
								currentPage = 1;
							}}
						>
							<span class="tab-icon">{category.icon}</span>
							<span class="tab-label">{category.label}</span>
							<span class="tab-badge">{category.count}</span>
						</button>
					{/each}
				</div>
			</div>
			<div class="toolbar-row split">
				<div class="toggle-group">
					<button class:active={statusFilter === 'all'} onclick={() => (statusFilter = 'all')}>
						All
					</button>
					<button
						class:active={statusFilter === 'downloaded'}
						onclick={() => (statusFilter = 'downloaded')}
					>
						Ready
					</button>
					<button class:active={statusFilter === 'missing'} onclick={() => (statusFilter = 'missing')}>
						Missing
					</button>
				</div>
				<div class="select-row">
					<label>
						<span>Sort</span>
						<select bind:value={sortOption}>
							<option value="name">Name</option>
							<option value="group">Group</option>
							<option value="version">Version</option>
						</select>
					</label>
					<label>
						<span>Order</span>
						<select bind:value={sortDirection}>
							<option value="desc">{directionLabels.desc}</option>
							<option value="asc">{directionLabels.asc}</option>
						</select>
					</label>
					<label>
						<span>Page size</span>
						<select
							value={pageSize}
							onchange={(event) => {
								pageSize = Number((event.currentTarget as HTMLSelectElement).value);
							}}
						>
							<option value="8">8</option>
							<option value="12">12</option>
							<option value="20">20</option>
							<option value="32">32</option>
						</select>
					</label>
				</div>
			</div>
		</div>

		<div class="library-meta">
			<span class="muted">
				Showing {rangeStart}-{rangeEnd} of {filteredProfiles.length} profiles
			</span>
			<div class="pagination">
				<button
					class="page-btn"
					onclick={() => (currentPage = Math.max(1, currentPage - 1))}
					disabled={currentPage === 1}
				>
					Prev
				</button>
				{#each paginationPages as page}
					{#if page === '...'}
						<span class="page-ellipsis">...</span>
					{:else}
						<button
							class="page-btn"
							class:active={page === currentPage}
							onclick={() => (currentPage = page as number)}
						>
							{page}
						</button>
					{/if}
				{/each}
				<button
					class="page-btn"
					onclick={() => (currentPage = Math.min(totalPages, currentPage + 1))}
					disabled={currentPage === totalPages}
				>
					Next
				</button>
			</div>
		</div>

		{#if data.profiles.error}
			<div class="error-box">
				<p>Failed to load profiles: {data.profiles.error}</p>
			</div>
		{:else if pagedProfiles.length > 0}
			<div class="profiles-grid">
				{#each pagedProfiles as profile}
					<!-- The name gets a line to itself so it never wraps, and the
					     badge pairs with the meta line rather than the title. Beside
					     the title a long status label forced the name to wrap; alone
					     on its own row it read as an orphan. Two small elements
					     sharing one row balances instead. -->
					<div class="profile-card">
						<div class="card-header">
							<h3>{profile.id}</h3>
							<div class="card-sub">
								<p class="meta">{profile.group} {profile.version}</p>
								<span class="badge" class:badge-ready={profile.downloaded}>
									{profile.downloaded ? 'Ready' : 'Not downloaded'}
								</span>
							</div>
						</div>
						<!-- Filename and actions are one block, pinned together to the
						     bottom of the card. Separately, the actions took the slack
						     and left the filename floating in the middle. -->
						<div class="card-footer">
							{#if profile.filename}
								<p class="file">{profile.filename}</p>
							{/if}
							<div class="card-actions">
								{#if !profile.downloaded}
									<button
										class="btn-action btn-primary"
										onclick={() => handleDownload(profile.id)}
										disabled={actionLoading[profile.id]}
									>
										Download
									</button>
								{:else}
									<div class="copy-group">
										<select
											value={copyTargets[profile.id] ?? ''}
											onchange={(event) => {
												const value = (event.currentTarget as HTMLSelectElement).value;
												copyTargets[profile.id] = value;
												copyTargets = { ...copyTargets };
											}}
										>
											<option value="">Select server</option>
											{#each servers as server}
												<option value={server.name}>{server.name}</option>
											{/each}
										</select>
										<button
											class="btn-action"
											onclick={() => handleCopy(profile.id)}
											disabled={actionLoading[profile.id] || !copyTargets[profile.id]}
										>
											Copy to server
										</button>
									</div>
								{/if}
								{#if profile.type === 'buildtools'}
									<button
										class="btn-action btn-danger"
										onclick={() => handleDelete(profile.id)}
										disabled={actionLoading[profile.id]}
									>
										Delete
									</button>
								{/if}
							</div>
						</div>
					</div>
				{/each}
			</div>
		{:else}
			<div class="empty-state">
				<h2>No profiles match your filters</h2>
				<p>Try adjusting the filters or download a new profile.</p>
			</div>
		{/if}
	</section>
	{/if}

	{#if activeView === 'buildtools'}
	<aside class="buildtools-panel" id="buildtools">
		<div class="buildtools-header">
			<div>
				<h2>BuildTools Station</h2>
				<p>Compile Spigot or CraftBukkit builds and queue them into your library.</p>
			</div>
			<a class="btn-ghost" href="#buildtools-console">Console</a>
		</div>

		<form
			class="buildtools-form"
			onsubmit={(event) => {
				event.preventDefault();
				if (buildStatus !== 'running') {
					startBuild();
				}
			}}
		>
			<label>
				Group
				<select bind:value={buildGroup} disabled={buildStatus === 'running'}>
					<option value="spigot">Spigot</option>
				</select>
			</label>
			<label>
				Version
				<select bind:value={buildVersion} disabled={buildStatus === 'running'}>
					<option value="">Select a version...</option>
					{#each commonVersions as version}
						<option value={version}>{version}</option>
					{/each}
				</select>
			</label>
			<button class="btn-primary" type="submit" disabled={buildStatus === 'running'}>
				{buildStatus === 'running' ? 'Building...' : 'Run BuildTools'}
			</button>
		</form>

		{#if buildError}
			<p class="error">{buildError}</p>
		{/if}

		{#if runId}
			<div class="buildtools-status">
				<div>
					<p>Run ID</p>
					<span>{runId}</span>
				</div>
				<div>
					<p>Status</p>
					<span class:status-running={buildStatus === 'running'} class:status-success={buildStatus === 'completed'} class:status-failed={buildStatus === 'failed'}>
						{buildStatus}
					</span>
				</div>
				{#if profileId}
					<div>
						<p>Profile</p>
						<span>{profileId}</span>
					</div>
				{/if}
				<button class="btn-secondary" onclick={() => runId && attachRun(runId)}>
					Open console
				</button>
			</div>
		{/if}

		<div class="run-list">
			<div class="run-list-header">
				<h3>Recent Builds</h3>
				<button class="btn-secondary" onclick={loadRuns} disabled={runsLoading}>
					{runsLoading ? 'Refreshing...' : 'Refresh'}
				</button>
			</div>
			{#if runs.length === 0}
				<p class="run-empty">No BuildTools runs yet.</p>
			{:else}
				<ul>
					{#each runs.slice(0, 5) as run}
						<li>
							<div>
								<div class="run-title">{run.profileId ?? run.runId}</div>
								<div class="run-meta">{run.group} {run.version}</div>
							</div>
							<div class="run-actions">
								<span
									class="run-status"
									class:status-success={run.status === 'completed'}
									class:status-failed={run.status === 'failed'}
								>
									{run.status}
								</span>
								<button class="btn-secondary" onclick={() => attachRun(run.runId)}>
									Open
								</button>
							</div>
						</li>
					{/each}
				</ul>
			{/if}
		</div>

		<div class="console-panel" id="buildtools-console">
			<div class="console-header">
				<h3>Build Console</h3>
				{#if runId}
					<button class="btn-secondary" onclick={() => runId && openStream(runId)}>Reconnect</button>
				{/if}
			</div>
			<div class="log-output" bind:this={logContainer}>
				{#if logs.length === 0}
					<p class="log-placeholder">Select a build run to see output.</p>
				{:else}
					{#each logs as line}
						<div class="log-line">{line}</div>
					{/each}
				{/if}
			</div>
		</div>
	</aside>
	{/if}
</div>

<style>
	.page-header {
		display: flex;
		justify-content: space-between;
		/* Centred, not flex-start: the chips are one short row against a
		   two-line title block, so top-aligning them left them floating. */
		align-items: center;
		margin-bottom: 28px;
		gap: 20px;
	}

	h1 {
		margin: 0 0 8px;
		font-size: 32px;
		font-weight: 600;
	}

	.subtitle {
		/* No bottom margin any more: the stat row it used to clear has moved
		   out of the copy block and onto the header's own row. */
		margin: 0;
		color: #aab2d3;
		font-size: 15px;
	}

	.stat-row {
		display: flex;
		flex-wrap: wrap;
		justify-content: flex-end;
		gap: 10px;
		/* Never squeezed by a long title; wraps within itself instead. */
		flex-shrink: 0;
	}

	.stat-chip {
		background: rgba(20, 24, 39, 0.8);
		border: 1px solid rgba(42, 47, 71, 0.8);
		border-radius: 999px;
		padding: 6px 12px;
		display: inline-flex;
		align-items: center;
		gap: 8px;
		font-size: 12px;
		color: #c7cbe0;
	}

	.stat-chip.success {
		background: rgba(106, 176, 76, 0.18);
		border-color: rgba(106, 176, 76, 0.35);
		color: #b7f5a2;
	}

	.stat-chip.warning {
		background: rgba(255, 200, 87, 0.14);
		border-color: rgba(255, 200, 87, 0.35);
		color: #f4c08e;
	}

	.stat-label {
		text-transform: uppercase;
		letter-spacing: 0.06em;
		font-size: 10px;
		color: #9aa2c5;
	}

	.stat-value {
		font-weight: 600;
		font-size: 14px;
		color: #eef0f8;
	}

	.btn-ghost {
		background: rgba(88, 101, 242, 0.12);
		border: 1px solid rgba(88, 101, 242, 0.3);
		color: #c7cbe0;
		border-radius: 10px;
		padding: 10px 16px;
		font-size: 13px;
		text-decoration: none;
		display: inline-flex;
		align-items: center;
		gap: 8px;
	}

	/* One view at a time, full width. Previously a fixed 360px sidebar sat beside
	   the grid whether or not anyone was compiling, which squeezed the profile
	   cards into two columns and left a tall gap under the console. */
	.profiles-shell {
		display: grid;
		grid-template-columns: minmax(0, 1fr);
		gap: 24px;
		align-items: start;
	}

	/* Tabs for the page's two views, a step above the category tabs inside the
	   library so the hierarchy is obvious. */
	.view-tabs {
		display: flex;
		gap: 8px;
		margin-bottom: 20px;
		border-bottom: 1px solid #2a2f47;
	}

	.view-tab {
		display: flex;
		align-items: center;
		gap: 8px;
		padding: 12px 20px;
		background: transparent;
		border: none;
		border-bottom: 2px solid transparent;
		color: #8890b1;
		font-family: inherit;
		font-size: 15px;
		font-weight: 500;
		white-space: nowrap;
		cursor: pointer;
		transition: color 0.15s, border-color 0.15s;
	}

	.view-tab:hover {
		color: #c9d1f2;
	}

	.view-tab.active {
		color: var(--mc-grass, #6ab04c);
		border-bottom-color: var(--mc-grass, #6ab04c);
	}

	.library-panel,
	.buildtools-panel {
		background: #1a1e2f;
		border-radius: 18px;
		padding: 20px;
		box-shadow: 0 20px 40px rgba(0, 0, 0, 0.35);
		border: 1px solid rgba(42, 47, 71, 0.9);
		min-width: 0;
	}

	.library-toolbar {
		display: flex;
		flex-direction: column;
		gap: 16px;
		padding-bottom: 20px;
		border-bottom: 1px solid rgba(42, 47, 71, 0.6);
		min-width: 0;
	}

	.search-field {
		min-width: 0;
		width: 100%;
		display: flex;
		flex-direction: column;
	}

	.search-field label {
		font-size: 12px;
		color: #aab2d3;
		margin-bottom: 6px;
		display: block;
	}

	.search-field input {
		width: 100%;
		max-width: 100%;
		min-width: 0;
		box-sizing: border-box;
	}

	.toolbar-row {
		display: flex;
		flex-wrap: wrap;
		gap: 12px;
	}

	.toolbar-row.split {
		justify-content: space-between;
		align-items: center;
	}

	.select-row {
		display: flex;
		gap: 12px;
		flex-wrap: wrap;
	}

	.select-row label {
		display: flex;
		flex-direction: column;
		gap: 6px;
		font-size: 12px;
		color: #aab2d3;
	}

	label {
		display: flex;
		flex-direction: column;
		gap: 6px;
		font-size: 13px;
		color: #aab2d3;
	}

	input,
	select {
		background: #141827;
		border: 1px solid #2a2f47;
		border-radius: 10px;
		padding: 10px 12px;
		color: #eef0f8;
		font-family: inherit;
		font-size: 14px;
	}

	/* Category tabs, matching the server Config page's section tabs so the two
	   pages present grouping the same way. */
	.section-tabs {
		display: flex;
		gap: 8px;
		padding: 4px;
		background: #141827;
		border-radius: 12px;
		overflow-x: auto;
		-webkit-overflow-scrolling: touch;
		scroll-snap-type: x proximity;
	}

	.section-tab {
		display: flex;
		align-items: center;
		gap: 8px;
		padding: 10px 16px;
		background: transparent;
		border: none;
		border-radius: 8px;
		color: #9aa2c5;
		font-family: inherit;
		font-size: 14px;
		font-weight: 500;
		white-space: nowrap;
		cursor: pointer;
		scroll-snap-align: start;
		transition: background 0.15s, color 0.15s;
	}

	.section-tab:hover {
		color: #eef0f8;
	}

	.section-tab.active {
		background: linear-gradient(135deg, #5865f2 0%, #4752c4 100%);
		color: white;
		box-shadow: 0 4px 12px rgba(88, 101, 242, 0.3);
	}

	.tab-icon {
		font-size: 14px;
		opacity: 0.9;
	}

	.tab-badge {
		background: rgba(255, 255, 255, 0.12);
		padding: 2px 8px;
		border-radius: 10px;
		font-size: 12px;
	}

	.section-tab.active .tab-badge {
		background: rgba(255, 255, 255, 0.25);
	}

	.toggle-group {
		display: flex;
		gap: 6px;
		background: #141827;
		border-radius: 10px;
		padding: 4px;
	}

	.toggle-group button {
		background: transparent;
		border: none;
		color: #8890b1;
		padding: 6px 12px;
		border-radius: 8px;
		cursor: pointer;
		font-size: 12px;
	}

	.toggle-group button.active {
		background: rgba(106, 176, 76, 0.2);
		color: #eef0f8;
	}

	.library-meta {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 12px;
		padding: 16px 0;
		flex-wrap: wrap;
	}

	.pagination {
		display: flex;
		align-items: center;
		gap: 6px;
		flex-wrap: wrap;
	}

	.page-btn {
		background: #2b2f45;
		color: #d4d9f1;
		border: none;
		border-radius: 8px;
		padding: 6px 10px;
		font-size: 12px;
		cursor: pointer;
	}

	.page-btn.active {
		background: rgba(106, 176, 76, 0.25);
		color: #eef0f8;
	}

	.page-btn:disabled {
		opacity: 0.5;
		cursor: not-allowed;
	}

	.page-ellipsis {
		color: #6f789b;
		font-size: 12px;
		padding: 0 6px;
	}

	.muted {
		color: #8e96bb;
		font-size: 12px;
	}

	.btn-primary {
		background: var(--mc-grass);
		color: #fff;
		border: none;
		border-radius: 10px;
		padding: 12px 20px;
		font-size: 14px;
		font-weight: 600;
		cursor: pointer;
		display: inline-flex;
		align-items: center;
		justify-content: center;
		text-decoration: none;
	}

	.btn-primary:disabled {
		opacity: 0.6;
		cursor: not-allowed;
	}

	.btn-secondary {
		background: #2b2f45;
		color: #d4d9f1;
		border: none;
		border-radius: 8px;
		padding: 8px 12px;
		font-size: 12px;
		cursor: pointer;
		text-decoration: none;
		display: inline-flex;
		align-items: center;
		justify-content: center;
	}

	.error-box {
		background: rgba(255, 92, 92, 0.1);
		border: 1px solid rgba(255, 92, 92, 0.3);
		border-radius: 12px;
		padding: 16px 20px;
		color: #ff9f9f;
	}

	.error {
		color: #ff9f9f;
		margin-top: 8px;
		font-size: 13px;
	}

	.profiles-grid {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
		gap: 20px;
	}

	.profile-card {
		background: #141827;
		border-radius: 16px;
		padding: 22px;
		display: flex;
		flex-direction: column;
		gap: 14px;
		border: 1px solid rgba(42, 47, 71, 0.8);
		box-shadow: inset 0 0 0 1px rgba(106, 176, 76, 0.05);
	}

	.card-header {
		display: flex;
		flex-direction: column;
		gap: 6px;
	}

	.card-header h3 {
		margin: 0;
		font-size: 19px;
		line-height: 1.3;
		/* A profile name is one long hyphenated token with no natural break
		   point, so cap it to the card rather than let it push the column. */
		overflow-wrap: anywhere;
	}

	/* The meta line and the status badge share a row: both are small, and
	   pairing them keeps the card to four bands instead of five. */
	.card-sub {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 10px;
	}

	.meta {
		margin: 0;
		font-size: 12px;
		color: #9aa2c5;
	}

	/* Filename and actions travel together at the bottom of the card. */
	.card-footer {
		display: flex;
		flex-direction: column;
		/* The slack in a stretched grid card collects above this block rather
		   than between the filename and the button. */
		margin-top: auto;
	}

	.file {
		margin: 0;
		font-size: 12px;
		color: #c9d1d9;
		/* Recessed rather than outlined: with a border it read as a disabled
		   text input sitting in the middle of the card. */
		background: rgba(10, 13, 22, 0.55);
		/* Square along the bottom so it seats directly on the controls below
		   and the two read as one block. */
		border-radius: 8px 8px 0 0;
		padding: 9px 11px;
		font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
		white-space: nowrap;
		overflow: hidden;
		text-overflow: ellipsis;
	}

	/* Only when a filename is actually rendered above them - the adjacent
	   sibling selector means a card without one keeps fully rounded controls. */
	.file + .card-actions > .btn-action {
		border-top-left-radius: 0;
		border-top-right-radius: 0;
	}

	/* The select and its button seat under the filename as a single bar, so
	   only the two outer bottom corners stay rounded. */
	.file + .card-actions .copy-group select {
		border-radius: 0 0 0 8px;
	}

	.file + .card-actions .copy-group .btn-action {
		border-radius: 0 0 8px 0;
	}

	.badge {
		padding: 4px 10px;
		border-radius: 999px;
		font-size: 12px;
		background: rgba(255, 159, 159, 0.15);
		color: #ff9f9f;
		/* Never broken across two lines, and never squeezed by the meta text. */
		white-space: nowrap;
		flex-shrink: 0;
	}

	.badge-ready {
		background: rgba(106, 176, 76, 0.2);
		color: #b7f5a2;
	}

	.card-actions {
		display: flex;
		flex-wrap: wrap;
		gap: 8px;
		/* stretch, not center: the button has no border and 1px less padding
		   than the select, so centring left it visibly shorter with slivers of
		   card showing above and below it. */
		align-items: stretch;
		/* Grid cards already stretch to a shared row height, which left a gap
		   under the button on the shorter ones. Pinning the actions to the
		   bottom puts that slack above them, so buttons line up across a row. */
		margin-top: auto;
	}

	/* Scoped to the card - .btn-primary is shared with the BuildTools form's
	   submit button, which should stay its natural width. A lone Download
	   button looked stranded in the corner of a card this wide. */
	.card-actions .btn-primary {
		flex: 1;
	}

	/* No gap and no wrapping: the two controls butt together into one bar,
	   the same shape the lone Download button makes on an undownloaded card.
	   The select's darker background is what marks the seam, so neither needs
	   a divider. */
	.copy-group {
		display: flex;
		gap: 0;
		flex: 1;
		flex-wrap: nowrap;
	}

	.copy-group select {
		flex: 1;
		/* 0, not a floor of 140px: on the narrowest cards that floor pushed
		   the button past the card edge. */
		min-width: 0;
		border-top-right-radius: 0;
		border-bottom-right-radius: 0;
	}

	.copy-group .btn-action {
		border-top-left-radius: 0;
		border-bottom-left-radius: 0;
		white-space: nowrap;
	}

	.btn-action {
		background: #2b2f45;
		color: #d4d9f1;
		border: none;
		border-radius: 6px;
		padding: 9px 14px;
		font-size: 13px;
		cursor: pointer;
	}

	.btn-action:disabled {
		opacity: 0.6;
		cursor: not-allowed;
	}

	.btn-danger {
		background: rgba(255, 92, 92, 0.15);
		color: #ff9f9f;
	}

	.empty-state {
		text-align: center;
		padding: 60px 20px;
		color: #8e96bb;
		background: #141827;
		border-radius: 16px;
		border: 1px dashed rgba(106, 176, 76, 0.2);
	}

	.buildtools-panel {
		display: flex;
		flex-direction: column;
		gap: 16px;
		background: linear-gradient(160deg, rgba(20, 24, 39, 0.95), rgba(18, 21, 33, 0.95));
		/* No longer sticky: it is the whole view now, not a companion column
		   that had to stay in sight while the profile list scrolled past it. */
	}

	.buildtools-header {
		display: flex;
		justify-content: space-between;
		align-items: flex-start;
		gap: 12px;
	}

	.buildtools-header h2 {
		margin: 0 0 4px;
		font-size: 18px;
	}

	.buildtools-header p {
		margin: 0;
		color: #9aa2c5;
		font-size: 13px;
	}

	.buildtools-form {
		display: flex;
		flex-direction: column;
		gap: 12px;
		padding: 12px;
		background: rgba(20, 24, 39, 0.6);
		border-radius: 12px;
		border: 1px solid rgba(42, 47, 71, 0.6);
	}

	.buildtools-status {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
		gap: 10px;
		background: rgba(20, 24, 39, 0.7);
		border-radius: 12px;
		padding: 12px;
		border: 1px solid rgba(42, 47, 71, 0.6);
	}

	.buildtools-status p {
		margin: 0;
		font-size: 11px;
		color: #8e96bb;
		text-transform: uppercase;
		letter-spacing: 0.06em;
	}

	.buildtools-status span {
		font-size: 13px;
		color: #eef0f8;
		font-weight: 600;
	}

	.status-running {
		color: #f0c674;
	}

	.status-success {
		color: #b7f5a2;
	}

	.status-failed {
		color: #ff9f9f;
	}

	.run-list {
		display: flex;
		flex-direction: column;
		gap: 12px;
	}

	.run-list-header {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: 8px;
	}

	.run-list ul {
		list-style: none;
		padding: 0;
		margin: 0;
		display: grid;
		gap: 10px;
	}

	.run-list li {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: 12px;
		background: #141827;
		border-radius: 12px;
		padding: 12px;
		border: 1px solid rgba(42, 47, 71, 0.8);
	}

	.run-title {
		font-weight: 600;
		color: #eef0f8;
		font-size: 13px;
	}

	.run-meta {
		font-size: 12px;
		color: #9aa2c5;
	}

	.run-actions {
		display: flex;
		align-items: center;
		gap: 8px;
	}

	.run-status {
		font-size: 12px;
		color: #f0c674;
	}

	.run-empty {
		margin: 0;
		color: #9aa2c5;
		font-size: 13px;
	}

	.console-panel {
		display: flex;
		flex-direction: column;
		gap: 10px;
		background: #0f121e;
		border-radius: 14px;
		padding: 14px;
		border: 1px solid rgba(42, 47, 71, 0.8);
	}

	.console-header {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: 8px;
	}

	.console-header h3 {
		margin: 0;
		font-size: 14px;
		color: #c9d1d9;
	}

	.log-output {
		background: #0b0e18;
		border: 1px solid rgba(42, 47, 71, 0.6);
		border-radius: 12px;
		padding: 12px;
		height: 260px;
		overflow-y: auto;
		font-family: "Cascadia Code", "Fira Code", "Consolas", monospace;
		font-size: 12px;
		color: #d4d9f1;
	}

	.log-line {
		white-space: pre-wrap;
		word-break: break-word;
	}

	.log-placeholder {
		margin: 0;
		color: #6f789b;
	}

	.link-action {
		color: #a5b4fc;
		font-size: 12px;
		text-decoration: none;
		align-self: center;
	}

	@media (max-width: 1080px) {
		.profiles-shell {
			grid-template-columns: 1fr;
		}

		.buildtools-panel {
			position: static;
		}
	}

	@media (max-width: 720px) {
		.page-header {
			flex-direction: column;
			align-items: flex-start;
		}

		/* Stacked under the title again at this width, so the chips line up
		   with it rather than hugging the right edge. */
		.stat-row {
			justify-content: flex-start;
		}

		.toolbar-row.split {
			flex-direction: column;
			align-items: flex-start;
		}
	}
</style>
