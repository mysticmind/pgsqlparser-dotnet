<img src="https://raw.githubusercontent.com/mysticmind/pgsqlparser-dotnet/main/assets/icon.png" alt="PgSqlParser icon: an elephant holding a parse tree" width="96">

# PgSqlParser [![Nuget Package](https://badgen.net/nuget/v/pgsqlparser)](https://www.nuget.org/packages/pgsqlparser/)

PostgreSQL SQL parser for .NET, built on [libpg_query](https://github.com/pganalyze/libpg_query), which uses the actual PostgreSQL server source to parse SQL and return the internal PostgreSQL parse tree.

With it you can:

- parse SQL to an AST, and deparse or format it back to SQL
- fingerprint and normalize queries, split scripts and tokenize
- classify statements and find the tables, columns and functions a query uses
- analyse the locks a statement takes and the columns a query returns
- rewrite queries and build parse trees in code
- parse PL/pgSQL

Native libraries are included for Linux (glibc and musl), macOS and Windows on x64 and ARM64, and the package is Native AOT compatible.

**Documentation: [mysticmind.github.io/pgsqlparser-dotnet](https://mysticmind.github.io/pgsqlparser-dotnet/)**

For background on why a query's parse tree is useful, see [pg_query 2.0: The easiest way to parse Postgres queries](https://pganalyze.com/blog/pg-query-2-0-postgres-query-parser).

## Installation

```shell
dotnet add package pgsqlparser
```

Note that the libpg_query libs for all OS'es are already packaged with the assembly.

| Platform | Runtime identifiers | Minimum version |
|---|---|---|
| Linux (glibc) | `linux-x64`, `linux-arm64` | glibc 2.17 |
| Linux (musl, for example Alpine) | `linux-musl-x64`, `linux-musl-arm64` | Alpine 3.17 |
| macOS | `osx-x64`, `osx-arm64` | macOS 10.15 (Intel), macOS 11.0 (Apple silicon) |
| Windows | `win-x64`, `win-arm64` | |

This version is built on libpg_query 18.1.0, which uses the PostgreSQL 18.6 parser. It targets .NET 8, .NET 9 and .NET 10, and is compatible with trimming and Native AOT.

The package contains the native library for every platform above. To include only the one you run on, publish with a runtime identifier, for example `dotnet publish -r linux-x64`. See [Keeping deployments small](https://mysticmind.github.io/pgsqlparser-dotnet/guide/getting-started#keeping-deployments-small).

## Quick start

Every function returns a `Result<T>` that holds either a `Value` or an `Error`, and has an async version.

```csharp
using PgSqlParser;

// Parse SQL into the PostgreSQL parse tree
var tree = Parser.Parse("SELECT id, name FROM users WHERE id = 42").GetValueOrThrow();
Console.WriteLine(tree.Stmts.Count);   // 1

// Find what a query touches
foreach (var table in tree.GetReferences().Tables)
{
    Console.WriteLine($"{table.Name}: {table.Role}");   // users: Read
}

// Format a query
Console.WriteLine(Parser.Format("select a, b from t where x = 1").GetValueOrThrow());
// SELECT a, b
// FROM t
// WHERE x = 1

// Fingerprint a query: the same query shape always gets the same id
Console.WriteLine(Parser.Fingerprint("SELECT 1").GetValueOrThrow());   // 50fde20626009aba

// Handle a failure without throwing
var result = Parser.Parse("SELECT FROM WHERE");
if (!result.IsSuccess)
{
    Console.WriteLine(result.Error.Message);
}
```

## Documentation

The full guide is at [mysticmind.github.io/pgsqlparser-dotnet](https://mysticmind.github.io/pgsqlparser-dotnet/):

| Topic | What it covers |
|---|---|
| [Getting started](https://mysticmind.github.io/pgsqlparser-dotnet/guide/getting-started) | Installation, platforms, deployment size and working with `Result<T>` |
| [Parse trees](https://mysticmind.github.io/pgsqlparser-dotnet/guide/parse-tree) | `Parse`, walking, editing, comparing and building trees, typed visitors |
| [Deparse, format and normalize](https://mysticmind.github.io/pgsqlparser-dotnet/guide/deparse-format) | `Deparse`, `Format`, `Normalize`, `NormalizeUtility` and `Fingerprint` |
| [Query analysis](https://mysticmind.github.io/pgsqlparser-dotnet/guide/analysis) | `Classify`, references, locks, `Summary`, `OperationSummary`, `ParameterRefs` and `IsUtilityStmt` |
| [Scan, tokenize and split](https://mysticmind.github.io/pgsqlparser-dotnet/guide/scan-split) | `Scan`, `Tokenize`, `SplitWithScanner` and `SplitWithParser` |
| [PL/pgSQL](https://mysticmind.github.io/pgsqlparser-dotnet/guide/plpgsql) | Parsing function bodies and the SQL inside them |
| [Errors and offsets](https://mysticmind.github.io/pgsqlparser-dotnet/guide/errors-offsets) | Parse errors, cursor positions and non-ASCII text |
| [What's new](https://mysticmind.github.io/pgsqlparser-dotnet/guide/whats-new) | Changes in 2.1 and 2.0, and upgrading from 1.x |

To work on the documentation, see the `docs` folder: `npm install` and `npm run dev` start it locally.

## License

PgSqlParser is licensed under the [MIT License](LICENSE).

Copyright (c) 2026, Babu Annamalai <babu.annamalai@gmail.com>

Refer to [libpg_query license](https://github.com/pganalyze/libpg_query?tab=readme-ov-file#license) for license details on libpg_query.

This project includes code derived from the [PostgreSQL project](http://www.postgresql.org/),
see [LICENSE.POSTGRESQL](LICENSE.POSTGRESQL) for details.
