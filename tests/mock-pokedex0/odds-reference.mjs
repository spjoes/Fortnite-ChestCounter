// The website's own odds math, to check the counter against it (tests only).
// getProbabilityAcrossRolls and formatShinyProbability are copied from Pokedex0-Website origin/main lib/shiny-odds.ts,
// unchanged except for the TypeScript types. Keep them in sync with the website.
//
//   echo '[[137500, 1200], [1, 0]]' | node tests/mock-pokedex0/odds-reference.mjs
// Prints [{ "p": <probability>, "text": "<formatShinyProbability(p)>" }, ...] (one per [denominator, tries]).

/** Exact chance of at least one success across a fixed number of rolls. */
export function getProbabilityAcrossRolls(perRollProbability, rolls) {
	if (
		!Number.isFinite(perRollProbability) ||
		perRollProbability <= 0 ||
		Number.isNaN(rolls) ||
		rolls <= 0
	) {
		return 0;
	}

	if (perRollProbability >= 1) {
		return 1;
	}

	if (rolls === Number.POSITIVE_INFINITY) {
		return 1;
	}

	const completedRolls = Math.floor(rolls);
	const probability = -Math.expm1(
		completedRolls * Math.log1p(-perRollProbability),
	);

	return Math.min(probability, 1 - Number.EPSILON);
}

export function formatShinyProbability(probability) {
	if (!Number.isFinite(probability) || probability <= 0) {
		return "0%";
	}

	if (probability >= 1) {
		return "100%";
	}

	const percent = probability * 100;
	if (percent < 0.0001) {
		return "<0.0001%";
	}

	if (percent > 99.99) {
		return ">99.99%";
	}

	return `${percent.toLocaleString("en-US", {
		maximumFractionDigits: percent < 0.1 ? 4 : 2,
	})}%`;
}

// Run as a command (not when imported by app-e2e.mjs): reads the pairs on stdin.
import { pathToFileURL } from "node:url";
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
	const chunks = [];
	for await (const c of process.stdin) chunks.push(c);
	const pairs = JSON.parse(Buffer.concat(chunks).toString("utf8"));
	// The website's custom odds: a fixed 1-in-N rate (probability_numerator 1 / probability_denominator N).
	const out = pairs.map(([denominator, tries]) => {
		const p = getProbabilityAcrossRolls(1 / denominator, tries);
		return { p, text: formatShinyProbability(p) };
	});
	process.stdout.write(JSON.stringify(out));
}
