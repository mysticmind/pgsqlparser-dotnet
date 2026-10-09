// Generates the guide pages from the repository README, so the README stays the single source
// of the documentation. Run through "npm run dev" or "npm run build".
import { readFileSync, writeFileSync, mkdirSync, rmSync, copyFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const docs = join(dirname(fileURLToPath(import.meta.url)), '..')
const root = join(docs, '..')
const repo = 'https://github.com/mysticmind/pgsqlparser-dotnet/blob/main/'

// Each page takes the README sections with these headings, in this order.
export const pages = [
  { file: 'getting-started', title: 'Getting started', sections: ['Installation', 'Usage', 'Working with results'] },
  { file: 'parse-tree', title: 'Parse trees', sections: ['Parse', 'Navigating the parse tree'] },
  { file: 'deparse-format', title: 'Deparse, format and normalize', sections: ['Deparse', 'Format', 'Normalize', 'NormalizeUtility', 'Fingerprint'] },
  { file: 'analysis', title: 'Query analysis', sections: ['Classify', 'References', 'Locks', 'Summary', 'OperationSummary', 'ParameterRefs', 'IsUtilityStmt'] },
  { file: 'scan-split', title: 'Scan, tokenize and split', sections: ['Scan', 'Tokenize', 'SplitWithScanner', 'SplitWithParser'] },
  { file: 'plpgsql', title: 'PL/pgSQL', sections: ['ParsePlpgsql'] },
  { file: 'errors-offsets', title: 'Errors and offsets', sections: ['Parse Errors', 'Offsets and non-ASCII text'] },
  { file: 'whats-new', title: "What's new", sections: ["What's new in 2.1", "What's new in 2.0", 'Upgrading from 1.x'] },
  { file: 'license', title: 'License', sections: ['License'] },
]

// The same slugs GitHub and VitePress give to headings.
const slug = text => text.trim().toLowerCase().replace(/[^\p{L}\p{N}\s-]/gu, '').replace(/\s+/g, '-')

// Split the README into headings and the lines they own, ignoring "#" inside code fences.
const lines = readFileSync(join(root, 'README.md'), 'utf8').split('\n')
const headings = []
let fenced = false
lines.forEach((line, index) => {
  if (line.trimStart().startsWith('```')) fenced = !fenced
  const match = !fenced && /^(#{1,6})\s+(.*)$/.exec(line)
  if (match) headings.push({ level: match[1].length, text: match[2].trim(), index })
})

// A section runs from its heading up to the next heading of the same or a higher level.
// A section named on a page stops early at the next section that is named on any page,
// so "Usage" keeps only its introduction.
const named = new Set(pages.flatMap(page => page.sections))
function section(name) {
  const at = headings.findIndex(heading => heading.text === name)
  if (at < 0) throw new Error(`README has no section "${name}"; update docs/scripts/gen.mjs`)
  const start = headings[at]
  const next = headings.slice(at + 1).find(heading => heading.level <= start.level || named.has(heading.text))
  return { start, body: lines.slice(start.index, next ? next.index : lines.length) }
}

// Where every README anchor ends up on the site.
const anchors = new Map()
for (const page of pages) {
  for (const name of page.sections) {
    const { start, body } = section(name)
    let inFence = false
    for (const line of body) {
      if (line.trimStart().startsWith('```')) inFence = !inFence
      const match = !inFence && /^#{1,6}\s+(.*)$/.exec(line)
      if (match) anchors.set(slug(match[1]), `/guide/${page.file}#${slug(match[1])}`)
    }
  }
}

function render(page) {
  const out = [`# ${page.title}`, '']
  for (const name of page.sections) {
    const { start, body } = section(name)
    const shift = 2 - start.level   // each section's own heading becomes a second-level heading
    let inFence = false
    for (let line of body) {
      if (line.trimStart().startsWith('```')) inFence = !inFence
      if (!inFence) {
        const match = /^(#{1,6})(\s+.*)$/.exec(line)
        if (match) line = '#'.repeat(Math.max(2, match[1].length + shift)) + match[2]
        line = line
          .replace(/\]\(#([^)]+)\)/g, (whole, anchor) => anchors.has(anchor) ? `](${anchors.get(anchor)})` : whole)
          .replace(/\]\((LICENSE[^)]*)\)/g, `](${repo}$1)`)
      }
      out.push(line)
    }
    out.push('')
  }
  return out.join('\n').replace(/\n{3,}/g, '\n\n')
}

const guide = join(docs, 'guide')
rmSync(guide, { recursive: true, force: true })
mkdirSync(guide, { recursive: true })
for (const page of pages) writeFileSync(join(guide, `${page.file}.md`), render(page))

// The artwork lives in assets; the site uses the light and dark versions of it.
const artwork = { 'icon.svg': 'mark-light.svg', 'icon-dark.svg': 'mark-dark.svg', 'icon-512.png': 'icon-512.png' }
mkdirSync(join(docs, 'public'), { recursive: true })
for (const [from, to] of Object.entries(artwork)) copyFileSync(join(root, 'assets', from), join(docs, 'public', to))
console.log(`Generated ${pages.length} guide pages from README.md`)
