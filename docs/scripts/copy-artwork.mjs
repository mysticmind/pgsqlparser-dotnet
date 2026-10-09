// Copies the artwork from assets into the site, so the package icon and the site share one source.
// Run through "npm run dev" or "npm run build".
import { mkdirSync, copyFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const docs = join(dirname(fileURLToPath(import.meta.url)), '..')
const root = join(docs, '..')

const artwork = { 'icon.svg': 'mark-light.svg', 'icon-dark.svg': 'mark-dark.svg', 'icon-512.png': 'icon-512.png', 'favicon.svg': 'favicon.svg' }
mkdirSync(join(docs, 'public'), { recursive: true })
for (const [from, to] of Object.entries(artwork)) copyFileSync(join(root, 'assets', from), join(docs, 'public', to))
