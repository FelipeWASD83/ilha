// Pure part of the mod: the same rules as ilha/ferramentas/statusline_uso.py, so both writers agree on the file.
// File format (what the island reads): {"coletado_em": <s>, "rate_limits": {"five_hour": {...}, "seven_day": {...}}}

export const WINDOWS = ['five_hour', 'seven_day'] as const
export type WindowId = (typeof WINDOWS)[number]

export type Window = { used_percentage: number; resets_at?: number }
export type Windows = Partial<Record<WindowId, Window>>

// the same window can come with its reset a few seconds apart between replies
export const RESET_TOLERANCE = 600

export type EngineLimit = { kind: string; percentUsed: number; resetsAt?: string }

function num(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined
}

/** The engine's rate limits as the file's windows (only five_hour and seven_day; spend_limit stays out). */
export function fromEngine(limits: readonly EngineLimit[] | undefined): Windows {
  const out: Windows = {}
  for (const l of limits ?? []) {
    if (!(WINDOWS as readonly string[]).includes(l.kind)) continue
    const pct = num(l.percentUsed)
    if (pct === undefined) continue
    const w: Window = { used_percentage: pct }
    if (l.resetsAt) {
      const ms = Date.parse(l.resetsAt)
      if (Number.isFinite(ms) && ms > 0) w.resets_at = Math.round(ms / 1000)
    }
    out[l.kind as WindowId] = w
  }
  return out
}

/** The windows stored in the file ({} when missing, broken or in another format). */
export function fromFile(text: string | undefined): Windows {
  if (!text) return {}
  let d: unknown
  try {
    d = JSON.parse(text.replace(/^﻿/, ''))
  } catch {
    return {}
  }
  const rl = d && typeof d === 'object' ? (d as Record<string, unknown>).rate_limits : undefined
  if (!rl || typeof rl !== 'object') return {}
  const out: Windows = {}
  for (const id of WINDOWS) {
    const j = (rl as Record<string, unknown>)[id]
    if (!j || typeof j !== 'object') continue
    const pct = num((j as Record<string, unknown>).used_percentage)
    if (pct === undefined) continue
    const w: Window = { used_percentage: pct }
    const r = num((j as Record<string, unknown>).resets_at)
    if (r !== undefined && r > 0) w.resets_at = r
    out[id] = w
  }
  return out
}

function expired(w: Window, now: number): boolean {
  return w.resets_at !== undefined && now >= w.resets_at
}

/** Is the new reading worth more than the stored one? (an idle session can send a window from hours ago) */
function newer(n: Window, old: Window): boolean {
  if (n.resets_at === undefined || old.resets_at === undefined) return true
  if (n.resets_at > old.resets_at + RESET_TOLERANCE) return true
  if (n.resets_at < old.resets_at - RESET_TOLERANCE) return false
  // same window: usage only goes up inside it
  return n.used_percentage >= old.used_percentage
}

/** The windows to write and whether any new one was accepted (nothing accepted = leave the file alone). */
export function merge(stored: Windows, incoming: Windows, now: number): { windows: Windows; accepted: boolean } {
  const windows: Windows = {}
  let accepted = false
  for (const id of WINDOWS) {
    let old = stored[id]
    if (old && expired(old, now)) old = undefined
    const n = incoming[id]
    if (!n) {
      if (old) windows[id] = old
      continue
    }
    if (!old || newer(n, old)) {
      windows[id] = n
      accepted = true
    } else windows[id] = old
  }
  return { windows, accepted }
}

export function fileText(windows: Windows, now: number): string {
  return JSON.stringify({ coletado_em: Math.round(now * 1000) / 1000, rate_limits: windows }, null, 2)
}

/**
 * modulos.limite.ligado of the island's config.json (true when missing or unreadable). The island accepts line
 * comments and trailing commas, so they are stripped before parsing.
 */
export function enabled(configText: string | undefined): boolean {
  if (!configText) return true
  const clean = configText
    .replace(/^﻿/, '')
    .replace(/^\s*\/\/.*$/gm, '')
    .replace(/,(\s*[}\]])/g, '$1')
  try {
    const d = JSON.parse(clean) as { modulos?: { limite?: { ligado?: unknown } } }
    return d?.modulos?.limite?.ligado !== false
  } catch {
    return true
  }
}
