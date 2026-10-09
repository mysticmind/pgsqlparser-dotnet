# Getting started

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

### Keeping deployments small

The package contains the native library for every platform above, which is about 30 MB once unpacked. A portable publish copies all of them into your output. When deployment size matters, for example in a container image or a serverless function, publish for the platform you run on and only that platform's library is included:

```shell
dotnet publish -r linux-x64
```

| Publish command | Output size | Native libraries included |
|---|---|---|
| `dotnet publish` | about 31 MB | all 8 |
| `dotnet publish -r linux-x64` | about 5 MB | 1 |

The sizes are for a small framework-dependent app. The runtime identifier must match where the app runs: use `linux-musl-x64` on Alpine, and the `arm64` variants on ARM64.

## Usage

All functions support both sync and async versions.

## Working with results

Every function returns a `Result<T>` that holds either a `Value` or an `Error`. Pick the style that suits your code:

```csharp
using PgSqlParser;

// Check IsSuccess: the compiler then knows Value (or Error) is not null
var result = Parser.Parse(query);
if (result.IsSuccess)
{
    Console.WriteLine(result.Value.Stmts.Count);
}
else
{
    Console.WriteLine(result.Error.Message);
}

// Throw on failure: PgSqlParserException carries the Error
var tree = Parser.Parse(query).GetValueOrThrow();

// Try pattern
if (Parser.Parse(query).TryGetValue(out var parsed, out var error))
{
    Console.WriteLine(parsed.Stmts.Count);
}

// Match and deconstruction
var text = Parser.Normalize(query).Match(value => value, e => e.Message);
var (normalized, normalizeError) = Parser.Normalize(query);
```

