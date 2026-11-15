import { describe, expect, test } from 'claude-code/testing'
import { enabled, fileText, fromEngine, fromFile, merge } from './usage.ts'

const NOW = 1_800_000_000

describe('ilha-limit usage rules', () => {
  test('engine limits become the file windows, spend_limit left out', async () => {
    const w = fromEngine([
      { kind: 'five_hour', percentUsed: 23.5, resetsAt: new Date((NOW + 3600) * 1000).toISOString() },
      { kind: 'seven_day', percentUsed: 41.2 },
      { kind: 'spend_limit', percentUsed: 5 },
    ])
    expect(w).toEqual({ five_hour: { used_percentage: 23.5, resets_at: NOW + 3600 }, seven_day: { used_percentage: 41.2 } })
    expect(fromEngine([])).toEqual({})
    expect(fromEngine(undefined)).toEqual({})
  })

  test('merge follows statusline_uso.py', async () => {
    const g = { five_hour: { used_percentage: 50, resets_at: NOW + 3600 } }
    expect(merge(g, { five_hour: { used_percentage: 20, resets_at: NOW - 3600 } }, NOW).accepted).toBe(false)
    expect(merge(g, { five_hour: { used_percentage: 40, resets_at: NOW + 3610 } }, NOW).accepted).toBe(false)
    const up = merge(g, { five_hour: { used_percentage: 55, resets_at: NOW + 3600 } }, NOW)
    expect(up.accepted && up.windows.five_hour?.used_percentage === 55).toBe(true)
    const fresh = merge(g, { five_hour: { used_percentage: 3, resets_at: NOW + 3600 + 18000 } }, NOW)
    expect(fresh.accepted && fresh.windows.five_hour?.used_percentage === 3).toBe(true)
    const both = merge(g, { seven_day: { used_percentage: 9, resets_at: NOW + 86400 } }, NOW)
    expect(Object.keys(both.windows).sort()).toEqual(['five_hour', 'seven_day'])
    const gone = merge({ five_hour: { used_percentage: 90, resets_at: NOW - 1 } }, { seven_day: { used_percentage: 9 } }, NOW)
    expect(Object.keys(gone.windows)).toEqual(['seven_day'])
  })

  test('file round trip and broken files', async () => {
    const text = fileText({ seven_day: { used_percentage: 12, resets_at: NOW + 86400 } }, NOW + 0.12345)
    expect(JSON.parse(text).coletado_em).toBe(NOW + 0.123)
    expect(fromFile(text)).toEqual({ seven_day: { used_percentage: 12, resets_at: NOW + 86400 } })
    expect(fromFile('not json')).toEqual({})
    expect(fromFile('﻿{"rate_limits": {"five_hour": {"used_percentage": true}}}')).toEqual({})
  })

  test('the island config can turn it off', async () => {
    expect(enabled(undefined)).toBe(true)
    expect(enabled('{\n  // note\n  "modulos": {"limite": {"ligado": false},},\n}')).toBe(false)
    expect(enabled('{"modulos": {"limite": {"ligado": true}}}')).toBe(true)
    expect(enabled('broken')).toBe(true)
  })
})
