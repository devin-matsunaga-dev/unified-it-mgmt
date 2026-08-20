import type { IdentificationConfidence, IdentifierKind } from '../../api/assets'

/**
 * How the identification workflow reads on screen. Kept out of the components so both the field
 * screen and the desktop dialog say the same words about the same answer — a technician who learns
 * what "Low" means on a phone should not meet different wording on a laptop.
 */

const kindLabels: Record<IdentifierKind, string> = {
  SerialNumber: 'Serial number',
  ModelIdentifier: 'Model / product',
  AssetLabel: 'Our own label',
  Unknown: 'Unrecognised',
}

export function identifierKindLabel(kind: IdentifierKind) {
  return kindLabels[kind] ?? kind
}

const confidenceLabels: Record<IdentificationConfidence, string> = {
  High: 'High',
  Medium: 'Medium',
  Low: 'Low',
  Unknown: 'Not identified',
}

export function confidenceLabel(confidence: IdentificationConfidence) {
  return confidenceLabels[confidence] ?? confidence
}

/** Pill classes per DESIGN.md §3: green is settled, amber wants a look, red is unresolved. */
const confidenceTones: Record<IdentificationConfidence, string> = {
  High: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400',
  Medium: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400',
  Low: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400',
  Unknown: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400',
}

export function confidenceTone(confidence: IdentificationConfidence) {
  return confidenceTones[confidence] ?? confidenceTones.Unknown
}

/**
 * Whether an answer may fill the form on its own. Only an exact product match against an
 * authoritative record qualifies; everything else is shown for a person to accept or correct, which
 * is the rule that keeps a half-match from quietly becoming a CMDB record.
 */
export function canApplyWithoutReview(confidence: IdentificationConfidence) {
  return confidence === 'High'
}
