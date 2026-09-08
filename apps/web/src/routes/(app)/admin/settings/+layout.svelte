<script lang="ts">
	import { page } from '$app/stores';

	let { children } = $props();

	// Route-based tabs, matching ServerShell: the URL is the state, so a tab
	// survives a refresh and can be linked to directly.
	const tabs = [
		{ href: '/admin/settings', label: 'General', exact: true },
		{ href: '/admin/settings/plugin-tokens', label: 'Plugin API' }
	];

	function isActive(href: string, exact = false) {
		return exact
			? $page.url.pathname === href
			: $page.url.pathname === href || $page.url.pathname.startsWith(href + '/');
	}
</script>

<div class="page-header">
	<div>
		<h1>Settings</h1>
		<p class="subtitle">Configure system settings and integrations</p>
	</div>
</div>

<nav class="tabs">
	{#each tabs as tab}
		<a href={tab.href} class="tab" class:active={isActive(tab.href, tab.exact)}>
			{tab.label}
		</a>
	{/each}
</nav>

<div class="tab-content">
	{@render children()}
</div>

<style>
	.page-header {
		margin-bottom: 16px;
	}

	.page-header h1 {
		margin: 0;
		font-size: 28px;
	}

	.subtitle {
		margin: 4px 0 0;
		color: #8890b1;
		font-size: 14px;
	}

	.tabs {
		display: flex;
		gap: 4px;
		border-bottom: 1px solid #2a2f47;
		overflow-x: auto;
		margin-bottom: 24px;
	}

	.tab {
		padding: 12px 20px;
		color: #8890b1;
		text-decoration: none;
		border-bottom: 2px solid transparent;
		transition: all 0.2s;
		font-size: 14px;
		font-weight: 500;
		white-space: nowrap;
	}

	.tab:hover {
		color: #aab2d3;
	}

	.tab.active {
		color: var(--mc-grass);
		border-bottom-color: var(--mc-grass);
	}
</style>
