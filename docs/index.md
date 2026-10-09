---
layout: home

hero:
  name: PgSqlParser
  text: The real PostgreSQL parser, for .NET
  tagline: Parse, deparse, format, analyse and rewrite PostgreSQL SQL with the parser the server itself uses, through libpg_query.
  image:
    light: /mark-light.svg
    dark: /mark-dark.svg
    alt: PgSqlParser icon
  actions:
    - theme: brand
      text: Get started
      link: /guide/getting-started
    - theme: alt
      text: What's new
      link: /guide/whats-new
    - theme: alt
      text: View on GitHub
      link: https://github.com/mysticmind/pgsqlparser-dotnet

features:
  - title: Parse and deparse
    details: Turn SQL into the PostgreSQL parse tree, walk it, edit it, and turn it back into SQL, plain or pretty printed.
    link: /guide/parse-tree
  - title: Format and normalize
    details: Format SQL, replace constants with placeholders, and fingerprint queries so that the same query shape always gets the same id.
    link: /guide/deparse-format
  - title: Query analysis
    details: Classify statements, find the tables, columns and functions a query uses, and see which locks a statement takes.
    link: /guide/analysis
  - title: Scan, tokenize and split
    details: Get the tokens of a query with their positions, and split a script into statements.
    link: /guide/scan-split
  - title: PL/pgSQL
    details: Parse function bodies and reach the SQL statements inside them.
    link: /guide/plpgsql
  - title: Runs everywhere .NET does
    details: Native libraries for Linux (glibc and musl), macOS and Windows on x64 and ARM64. Targets .NET 8 and later and is Native AOT compatible.
    link: /guide/getting-started
---

## Install

```shell
dotnet add package pgsqlparser
```

## A first query

```csharp
using PgSqlParser;

var tree = Parser.Parse("SELECT id, name FROM users WHERE id = $1").GetValueOrThrow();
Console.WriteLine(tree.Stmts.Count);
```
