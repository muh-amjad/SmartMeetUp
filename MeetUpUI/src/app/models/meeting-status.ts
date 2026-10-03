/**
 * True while a finished meeting's recording, transcript and analysis are still being produced.
 *
 * The meeting page has nothing complete to show until then — opening it early only showed empty
 * panels and "not analysed yet" errors — so lists don't open these meetings, and the page itself
 * waits. Failed meetings are deliberately not included: they open, so the host can retry.
 */
export function isMeetingProcessing(status: string | null | undefined): boolean {
  return status === 'Processing';
}

/** How often a page re-checks a processing meeting, so it opens on its own once ready. */
export const ProcessingRecheckMs = 15_000;
