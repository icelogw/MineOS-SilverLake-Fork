<script lang="ts">
	import type { Snippet } from 'svelte';

	type Props = {
		title: string;
		unit?: string;
		color?: string;
		points: number[];
		timestamps?: string[];
		maxValue?: number;
		minValue?: number;
		/**
		 * Optional control rendered in the card's own header. Lets a caller put a
		 * switch beside the title instead of floating one over the card, where it
		 * would sit on top of the min/max readout.
		 */
		action?: Snippet;
	};

	let {
		title,
		unit = '',
		color = '#7ae68d',
		points,
		timestamps = [],
		maxValue,
		minValue,
		action
	}: Props = $props();

	const normalized = $derived.by(() => {
		if (!points || points.length === 0) {
			return { path: '', latest: 0, min: 0, max: 1 };
		}

		const safePoints = points.map((value) => (Number.isFinite(value) ? value : 0));
		const rawMin = minValue ?? Math.min(...safePoints);
		const rawMax = maxValue ?? Math.max(...safePoints);
		const safeMin = Number.isFinite(rawMin) ? rawMin : 0;
		const safeMax = Number.isFinite(rawMax) ? rawMax : safeMin + 1;
		const span = safeMax - safeMin || 1;
		const width = 100;
		const height = 40;
		const step = safePoints.length > 1 ? width / (safePoints.length - 1) : width;

		const coords = safePoints.map((value, index) => {
			const clamped = Number.isFinite(value) ? value : safeMin;
			const x = index * step;
			const y = height - ((clamped - safeMin) / span) * height;
			return `${x.toFixed(2)},${y.toFixed(2)}`;
		});

		const latestValue = safePoints[safePoints.length - 1];

		return {
			path: coords.join(' '),
			latest: Number.isFinite(latestValue) ? latestValue : 0,
			min: safeMin,
			max: safeMax
		};
	});

	const timeLabels = $derived.by(() => {
		if (!timestamps || timestamps.length < 2) return [];
		const count = Math.min(5, timestamps.length);
		const labels: { label: string }[] = [];
		for (let i = 0; i < count; i++) {
			const idx = Math.round((i / (count - 1)) * (timestamps.length - 1));
			const date = new Date(timestamps[idx]);
			if (isNaN(date.getTime())) continue;
			labels.push({
				label: date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
			});
		}
		return labels;
	});

	function formatNumber(value: number | null | undefined, fallback = '0.0', decimals = 1) {
		if (value == null || !Number.isFinite(value)) {
			return fallback;
		}
		return value.toFixed(decimals);
	}
</script>

<div class="chart-card">
	<header>
		<div>
			<p class="title">{title}</p>
			<p class="value">
				{formatNumber(normalized.latest)}
				{#if unit}
					<span class="unit">{unit}</span>
				{/if}
			</p>
		</div>
		<div class="header-right">
			<div class="range">
				<span>{formatNumber(normalized.min)}</span>
				<span>{formatNumber(normalized.max)}</span>
			</div>
			{#if action}
				{@render action()}
			{/if}
		</div>
	</header>

	{#if points.length > 1}
		<svg viewBox="0 0 100 40" preserveAspectRatio="none">
			<polyline points={normalized.path} style={`stroke: ${color};`} />
		</svg>
		{#if timeLabels.length > 0}
			<div class="time-axis">
				{#each timeLabels as tick}
					<span class="time-label">{tick.label}</span>
				{/each}
			</div>
		{/if}
	{:else}
		<div class="placeholder">Collecting data...</div>
	{/if}
</div>

<style>
	.chart-card {
		background: #1a1e2f;
		border: 1px solid #2a2f47;
		border-radius: 16px;
		padding: 16px;
		display: flex;
		flex-direction: column;
		gap: 12px;
		min-height: 300px;
	}

	header {
		display: flex;
		justify-content: space-between;
		align-items: flex-start;
		gap: 12px;
	}

	.title {
		margin: 0;
		font-size: 12px;
		letter-spacing: 0.14em;
		text-transform: uppercase;
		color: #8a93ba;
	}

	.value {
		margin: 4px 0 0;
		font-size: 22px;
		font-weight: 600;
		color: #eef0f8;
	}

	.unit {
		margin-left: 6px;
		font-size: 12px;
		color: #8a93ba;
		font-weight: 500;
	}

	.header-right {
		display: flex;
		align-items: center;
		gap: 12px;
	}

	.range {
		display: flex;
		flex-direction: column;
		gap: 4px;
		font-size: 11px;
		color: #737aa3;
		text-align: right;
	}

	svg {
		width: 100%;
		/* Tall enough to actually read a trend. The viewBox stretches to fill, so
		   this is a straight trade of vertical space for detail. */
		height: 180px;
	}

	polyline {
		fill: none;
		stroke-width: 2.2;
		stroke-linecap: round;
		stroke-linejoin: round;
		/* The viewBox is stretched to the card with preserveAspectRatio="none", so
		   without this the stroke is scaled unevenly too — the taller the chart,
		   the more the line thickens vertically and thins horizontally. */
		vector-effect: non-scaling-stroke;
	}

	.time-axis {
		/* Evenly spaced rather than absolutely positioned. At 0/25/50/75/100% the
		   labels overlapped each other in a narrow card and the last one hung off
		   the right edge; the points are evenly spaced anyway after bucketing. */
		display: flex;
		justify-content: space-between;
		gap: 8px;
		height: 16px;
		margin-top: 2px;
		overflow: hidden;
	}

	.time-label {
		font-size: 10px;
		color: #737aa3;
		white-space: nowrap;
	}

	.placeholder {
		flex: 1;
		display: flex;
		align-items: center;
		justify-content: center;
		color: #8a93ba;
		font-size: 13px;
		border: 1px dashed #2a2f47;
		border-radius: 12px;
		padding: 12px;
	}
	</style>
