<script lang="ts">
	import { onMount } from 'svelte';
	import type { ServerPanelData } from './panelData';
	import type { loadPerformance } from '$lib/loads/performance';

	type PageData = Awaited<ReturnType<typeof loadPerformance>>;
	import type { PerformanceSample } from '$lib/api/types';
	import PerformanceChart from '$lib/components/PerformanceChart.svelte';

	let { data }: { data: PageData & ServerPanelData } = $props();

	let samples = $state<PerformanceSample[]>([]);
	let streamStatus = $state<'connecting' | 'live' | 'stopped'>('connecting');
	let streamSource: EventSource | null = null;
	let streamRetry: ReturnType<typeof setTimeout> | null = null;
	let sparkStatus = $state<typeof data.spark.data | null>(null);
	let sparkError = $state<string | null>(null);

	$effect(() => {
		// Initialize samples from data
		samples = data.history?.data?.length
			? data.history.data
			: data.realtime?.data
				? [data.realtime.data]
				: [];
		sparkStatus = data.spark?.data ?? null;
		sparkError = data.spark?.error ?? null;
	});

	const latest = $derived(samples[samples.length - 1] ?? data.realtime?.data ?? null);
	const cpuSeries = $derived(samples.map((sample) => sample.cpuPercent));
	const ramSeries = $derived(samples.map((sample) => sample.ramUsedMb));
	const tpsSeries = $derived(samples.map((sample) => sample.tps ?? 0));
	const playerSeries = $derived(samples.map((sample) => sample.playerCount));
	const timestampSeries = $derived(samples.map((sample) => sample.timestamp));

	function formatMemory(usedMb: number, totalMb: number) {
		const used = usedMb >= 1024 ? `${(usedMb / 1024).toFixed(1)} GB` : `${usedMb} MB`;
		const total = totalMb >= 1024 ? `${(totalMb / 1024).toFixed(1)} GB` : `${totalMb} MB`;
		return `${used} / ${total}`;
	}

	function formatTps(value: number | null) {
		if (value == null) return '--';
		return value.toFixed(2);
	}

	function formatNumber(value: number | null | undefined, fallback = '0.0', decimals = 1) {
		if (value == null || !Number.isFinite(value)) {
			return fallback;
		}
		return value.toFixed(decimals);
	}

	let tpsEnabled = $state(data.server?.config?.monitoring?.tpsEnabled ?? false);

	// A proxy has no tick loop, and monitoring.tpsCommand sends a game command
	// Velocity/BungeeCord do not implement — so TPS is not "0", it is meaningless.
	// CPU, memory and player count all still apply.
	const isProxy = $derived(data.server?.serverType === 'proxy');

	async function toggleTps() {
		if (!data.server?.config) return;

		const newValue = !tpsEnabled;

		// Fetch fresh config to avoid overwriting stale data
		const freshRes = await fetch(`/api/servers/${data.server.name}/server-config`);
		if (!freshRes.ok) return;
		const freshConfig = await freshRes.json();

		freshConfig.monitoring = freshConfig.monitoring ?? { tpsEnabled: false, tpsCommand: null };
		freshConfig.monitoring.tpsEnabled = newValue;

		const saveRes = await fetch(`/api/servers/${data.server.name}/server-config`, {
			method: 'PUT',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify(freshConfig)
		});

		if (saveRes.ok) {
			tpsEnabled = newValue;
		}
	}

	function connectStream() {
		if (!data.server) return;
		streamSource?.close();
		streamSource = new EventSource(`/api/servers/${encodeURIComponent(data.server.name)}/performance/streaming`);
		streamStatus = 'connecting';

		streamSource.onmessage = (event) => {
			try {
				const sample = JSON.parse(event.data) as PerformanceSample;
				samples = [...samples.slice(-300), sample];
				streamStatus = 'live';
			} catch {
				// ignore parse errors
			}
		};

		streamSource.onerror = () => {
			streamStatus = 'stopped';
			streamSource?.close();
			streamSource = null;
			if (streamRetry) {
				clearTimeout(streamRetry);
			}
			streamRetry = setTimeout(connectStream, 2000);
		};
	}

	onMount(() => {
		connectStream();

		return () => {
			streamSource?.close();
			if (streamRetry) {
				clearTimeout(streamRetry);
			}
		};
	});
</script>

<div class="performance-page">
	<header class="page-header">
		<div>
			<h2>Performance</h2>
			<p class="subtitle">Live server performance with historical context</p>
		</div>
		<div class="header-controls">
			<div class="ranges" role="group" aria-label="Time range">
				{#each data.ranges ?? [] as option}
					<a
						class="range"
						class:active={(data.range ?? '1h') === option.id}
						href="?range={option.id}"
						data-sveltekit-noscroll
					>
						{option.label}
					</a>
				{/each}
			</div>
			<div class="stream-status" data-status={streamStatus}>
				<span class="dot"></span>
				{streamStatus === 'live' ? 'Live' : streamStatus === 'connecting' ? 'Connecting' : 'Paused'}
			</div>
		</div>
	</header>

	{#if latest && !latest.isRunning}
		<div class="warning-card">
			Server is offline. Historical data remains visible while live updates pause.
		</div>
	{/if}

	<section class="summary-grid">
		<div class="summary-card">
			<p>CPU</p>
			<strong>{formatNumber(latest?.cpuPercent)}%</strong>
		</div>
		<div class="summary-card">
			<p>Memory</p>
			<strong>
				{latest ? formatMemory(latest.ramUsedMb, latest.ramTotalMb) : '0 MB / 0 MB'}
			</strong>
		</div>
		{#if !isProxy}
			<div class="summary-card">
				<p>TPS</p>
				<strong>{latest ? formatTps(latest.tps) : '--'}</strong>
			</div>
		{/if}
		<div class="summary-card">
			<p>Players</p>
			<strong>{latest ? latest.playerCount : 0}</strong>
		</div>
	</section>

	<section class="charts-grid">
		<PerformanceChart title="CPU" unit="%" color="#7ae68d" points={cpuSeries} timestamps={timestampSeries} maxValue={100} />
		<PerformanceChart title="Memory" unit="MB" color="#7fb3ff" points={ramSeries} timestamps={timestampSeries} />
		{#if !isProxy}
			<div class="tps-chart-wrapper">
				{#if !tpsEnabled}
					<!-- The switch belongs with the thing it switches. Floating above a
					     bare notice it read as a stray control over empty background,
					     while every neighbouring chart was a self-contained card. -->
					<div class="tps-card">
						<div class="tps-header">
							<p class="tps-title">TPS</p>
							<label class="tps-toggle" title="Enable TPS monitoring">
								<input type="checkbox" checked={tpsEnabled} onchange={toggleTps} />
								<span class="toggle-slider"></span>
							</label>
						</div>
						<div class="tps-disabled-notice">TPS monitoring is disabled for this server</div>
					</div>
				{:else}
					{#snippet tpsSwitch()}
						<label class="tps-toggle" title="Disable TPS monitoring">
							<input type="checkbox" checked={tpsEnabled} onchange={toggleTps} />
							<span class="toggle-slider"></span>
						</label>
					{/snippet}
					<!-- Rendered inside the chart card's header rather than positioned
					     over it, where it covered the min/max readout. -->
					<PerformanceChart
						title="TPS"
						unit=""
						color="#f5c97a"
						points={tpsSeries}
						timestamps={timestampSeries}
						maxValue={20}
						minValue={0}
						action={tpsSwitch}
					/>
				{/if}
			</div>
		{/if}
		<PerformanceChart title="Players" unit="" color="#d98cff" points={playerSeries} timestamps={timestampSeries} minValue={0} />
	</section>

	{#if sparkStatus?.installed}
		<section class="spark-section">
			<div class="spark-header">
				<h3>Spark</h3>
				<span class="spark-pill">Installed</span>
			</div>
			<div class="spark-grid">
				<div class="spark-card">
					<span class="spark-label">Mode</span>
					<strong>{sparkStatus.mode ?? 'plugin'}</strong>
				</div>
				<div class="spark-card">
					<span class="spark-label">Jar</span>
					<strong>{sparkStatus.jarName ?? 'spark'}</strong>
				</div>
				<div class="spark-card">
					<span class="spark-label">Version</span>
					<strong>{sparkStatus.version ?? 'Unknown'}</strong>
				</div>
				<div class="spark-card">
					<span class="spark-label">Reports</span>
					<strong>{sparkStatus.reportCount}</strong>
				</div>
			</div>
			{#if sparkStatus.reports.length > 0}
				<div class="spark-reports">
					<p>Recent reports</p>
					<ul>
						{#each sparkStatus.reports as report}
							<li>{report}</li>
						{/each}
					</ul>
				</div>
			{/if}
		</section>
	{:else if sparkError}
		<p class="error-text">{sparkError}</p>
	{/if}

	{#if data.history?.error}
		<p class="error-text">{data.history.error}</p>
	{/if}
</div>

<style>
	.performance-page {
		display: flex;
		flex-direction: column;
		gap: 24px;
	}

	.page-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 24px;
		flex-wrap: wrap;
	}

	.header-controls {
		display: flex;
		align-items: center;
		gap: 12px;
		flex-wrap: wrap;
	}

	.ranges {
		display: flex;
		gap: 4px;
		background: rgba(20, 24, 39, 0.6);
		border: 1px solid rgba(42, 47, 71, 0.8);
		border-radius: 999px;
		padding: 3px;
	}

	.range {
		padding: 6px 14px;
		border-radius: 999px;
		font-size: 13px;
		color: #8890b1;
		text-decoration: none;
		white-space: nowrap;
		transition: all 0.15s;
	}

	.range:hover {
		color: #c9d1f2;
	}

	.range.active {
		background: rgba(106, 176, 76, 0.15);
		color: #7ae68d;
		font-weight: 500;
	}

	.page-header h2 {
		margin: 0 0 8px;
		font-size: 24px;
		font-weight: 600;
		color: #eef0f8;
	}

	.subtitle {
		margin: 0;
		color: #9aa2c5;
		font-size: 14px;
	}

	.stream-status {
		display: flex;
		align-items: center;
		gap: 8px;
		padding: 8px 12px;
		border-radius: 999px;
		font-size: 12px;
		text-transform: uppercase;
		letter-spacing: 0.12em;
		border: 1px solid #2a2f47;
		color: #9aa2c5;
	}

	.stream-status .dot {
		width: 8px;
		height: 8px;
		border-radius: 999px;
		background: #8890b1;
	}

	.stream-status[data-status='live'] {
		border-color: rgba(106, 176, 76, 0.5);
		color: #b7f5a2;
	}

	.stream-status[data-status='live'] .dot {
		background: #7ae68d;
	}

	.stream-status[data-status='connecting'] {
		border-color: rgba(111, 181, 255, 0.5);
		color: #a6d5fa;
	}

	.stream-status[data-status='connecting'] .dot {
		background: #7fb3ff;
	}

	.warning-card {
		background: rgba(234, 85, 83, 0.15);
		border: 1px solid rgba(234, 85, 83, 0.4);
		color: #ffb0ad;
		padding: 12px 16px;
		border-radius: 12px;
		font-size: 14px;
	}

	.summary-grid {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
		gap: 16px;
	}

	.summary-card {
		background: #141827;
		border-radius: 14px;
		border: 1px solid #2a2f47;
		padding: 16px;
		display: flex;
		flex-direction: column;
		gap: 8px;
	}

	.summary-card p {
		margin: 0;
		color: #8a93ba;
		font-size: 12px;
		text-transform: uppercase;
		letter-spacing: 0.12em;
	}

	.summary-card strong {
		font-size: 18px;
		color: #eef0f8;
	}

	.charts-grid {
		display: grid;
		/* Wider tracks: four charts across meant each was too narrow to read a
		   trend in, and cramped the time axis into overlapping labels. Two up on a
		   normal window, still collapsing to one on a narrow one. */
		grid-template-columns: repeat(auto-fit, minmax(440px, 1fr));
		gap: 16px;
	}

	.tps-chart-wrapper {
		display: flex;
		flex-direction: column;
	}

	/* Matches PerformanceChart's own card, so a disabled TPS panel sits in the
	   grid the same way its neighbours do instead of as a loose notice. */
	.tps-card {
		background: #1a1e2f;
		border: 1px solid #2a2f47;
		border-radius: 16px;
		padding: 16px;
		display: flex;
		flex-direction: column;
		gap: 12px;
		min-height: 300px;
	}

	.tps-header {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: 12px;
	}

	/* Same treatment as a chart card's title. */
	.tps-title {
		margin: 0;
		font-size: 12px;
		letter-spacing: 0.14em;
		text-transform: uppercase;
		color: #8a93ba;
	}

	.tps-disabled-notice {
		flex: 1;
		display: flex;
		align-items: center;
		justify-content: center;
		text-align: center;
		color: #8890b1;
		font-style: italic;
		border: 1px dashed #2a2f47;
		border-radius: 12px;
		padding: 12px;
	}

	.tps-toggle {
		position: relative;
		display: inline-block;
		width: 36px;
		height: 20px;
		flex-shrink: 0;
	}

	.tps-toggle input { opacity: 0; width: 0; height: 0; }

	.toggle-slider {
		position: absolute;
		inset: 0;
		background: #2a2f47;
		border-radius: 20px;
		cursor: pointer;
		transition: background 0.2s;
	}

	.toggle-slider::before {
		content: '';
		position: absolute;
		height: 14px;
		width: 14px;
		left: 3px;
		bottom: 3px;
		background: #8890b1;
		border-radius: 50%;
		transition: transform 0.2s, background 0.2s;
	}

	.tps-toggle input:checked + .toggle-slider {
		background: rgba(106, 176, 76, 0.3);
	}

	.tps-toggle input:checked + .toggle-slider::before {
		transform: translateX(16px);
		background: var(--mc-grass);
	}

	.error-text {
		color: #ff9f9f;
		margin: 0;
	}

	.spark-section {
		background: #141827;
		border-radius: 16px;
		border: 1px solid #2a2f47;
		padding: 20px;
		display: flex;
		flex-direction: column;
		gap: 16px;
	}

	.spark-header {
		display: flex;
		align-items: center;
		gap: 12px;
		justify-content: space-between;
	}

	.spark-header h3 {
		margin: 0;
		font-size: 18px;
		color: #eef0f8;
	}

	.spark-pill {
		background: rgba(106, 176, 76, 0.2);
		color: #b7f5a2;
		border: 1px solid rgba(106, 176, 76, 0.4);
		border-radius: 999px;
		font-size: 11px;
		padding: 4px 10px;
		text-transform: uppercase;
		letter-spacing: 0.08em;
	}

	.spark-grid {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
		gap: 12px;
	}

	.spark-card {
		background: rgba(20, 24, 39, 0.8);
		border-radius: 12px;
		padding: 12px;
		border: 1px solid rgba(42, 47, 71, 0.8);
		display: flex;
		flex-direction: column;
		gap: 6px;
	}

	.spark-label {
		font-size: 11px;
		color: #8a93ba;
		text-transform: uppercase;
		letter-spacing: 0.08em;
	}

	.spark-reports p {
		margin: 0 0 8px;
		color: #9aa2c5;
		font-size: 13px;
	}

	.spark-reports ul {
		margin: 0;
		padding-left: 18px;
		color: #c4cff5;
		font-size: 13px;
		line-height: 1.6;
	}
</style>
