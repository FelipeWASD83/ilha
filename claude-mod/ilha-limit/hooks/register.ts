import type { Register, EngineInterface } from 'claude-code'
import { enabled, fileText, fromEngine, fromFile, merge, type EngineLimit } from './usage.ts'

// Desktop app sessions never run the status line, so ilha/ferramentas/statusline_uso.py only ever saw the terminal.
// The engine measures the limits itself after every reply: this writes them to the same file the island watches.

async function islandFolder($: EngineInterface): Promise<string | undefined> {
  // the island keeps its data in %USERPROFILE%\.ilha (not AppData: the desktop app virtualizes AppData)
  const home = (await $.env.get('USERPROFILE')) || (await $.env.get('HOME'))
  return home ? `${home.replace(/[\\/]+$/, '')}/.ilha` : undefined
}

async function readOrNothing($: EngineInterface, path: string): Promise<string | undefined> {
  try {
    return (await $.fs.exists(path)) ? await $.fs.read(path) : undefined
  } catch {
    return undefined
  }
}

let lastWritten = ''

async function save($: EngineInterface, limits: readonly EngineLimit[] | undefined): Promise<void> {
  const incoming = fromEngine(limits)
  if (!incoming.five_hour && !incoming.seven_day) return
  const folder = await islandFolder($)
  if (!folder) return
  if (!enabled(await readOrNothing($, `${folder}/config.json`))) return
  const path = `${folder}/uso-claude.json`
  const now = (await $.clock.now()) / 1000
  const { windows, accepted } = merge(fromFile(await readOrNothing($, path)), incoming, now)
  if (!accepted) return
  const key = JSON.stringify(windows)
  // the same reading again (another unit of the measurement moved): no write, the island would only reread it
  if (key === lastWritten) return
  await $.fs.write(path, fileText(windows, now))
  lastWritten = key
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    const r = await next(e)
    try {
      await save($, (await $.session.usage()).rateLimits)
    } catch {
      // nothing here may get in the way of the session
    }
    return r
  })

  on('session.measure', async ($, e, next) => {
    if (e.changed.includes('rateLimits')) {
      try {
        await save($, e.rateLimits)
      } catch {
        // a failed write only means the island keeps the previous reading
      }
    }
    return next(e)
  })
}
