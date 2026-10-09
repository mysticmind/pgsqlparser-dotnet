import { defineConfig } from 'vitepress'

// The site is served from https://mysticmind.github.io/pgsqlparser-dotnet/.
// Set DOCS_BASE=/ when serving from a domain root.
const base = process.env.DOCS_BASE ?? '/pgsqlparser-dotnet/'

const guide = [
  { text: 'Getting started', link: '/guide/getting-started' },
  { text: 'Parse trees', link: '/guide/parse-tree' },
  { text: 'Deparse, format and normalize', link: '/guide/deparse-format' },
  { text: 'Query analysis', link: '/guide/analysis' },
  { text: 'Scan, tokenize and split', link: '/guide/scan-split' },
  { text: 'PL/pgSQL', link: '/guide/plpgsql' },
  { text: 'Errors and offsets', link: '/guide/errors-offsets' },
]

export default defineConfig({
  base,
  lang: 'en-US',
  title: 'PgSqlParser',
  description: 'PostgreSQL SQL parser for .NET, built on libpg_query, the real PostgreSQL parser.',
  cleanUrls: true,
  lastUpdated: false,
  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: `${base}mark-light.svg` }],
    ['meta', { property: 'og:title', content: 'PgSqlParser' }],
    ['meta', { property: 'og:description', content: 'PostgreSQL SQL parser for .NET, built on libpg_query, the real PostgreSQL parser.' }],
    ['meta', { property: 'og:image', content: `https://mysticmind.github.io${base}icon-512.png` }],
  ],
  themeConfig: {
    logo: { light: '/mark-light.svg', dark: '/mark-dark.svg', alt: 'PgSqlParser' },
    nav: [
      { text: 'Guide', link: '/guide/getting-started', activeMatch: '^/guide/(?!whats-new|license)' },
      { text: "What's new", link: '/guide/whats-new' },
      { text: 'NuGet', link: 'https://www.nuget.org/packages/pgsqlparser' },
    ],
    sidebar: [
      { text: 'Guide', items: guide },
      {
        text: 'Project',
        items: [
          { text: "What's new", link: '/guide/whats-new' },
          { text: 'License', link: '/guide/license' },
        ],
      },
    ],
    outline: { level: [2, 3] },
    search: { provider: 'local' },
    socialLinks: [{ icon: 'github', link: 'https://github.com/mysticmind/pgsqlparser-dotnet' }],
    footer: {
      message: 'Released under the MIT License.',
      copyright: 'Copyright (c) 2026 Babu Annamalai',
    },
  },
})
