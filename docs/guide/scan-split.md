# Scan, tokenize and split

## Scan

Tokenize a query. Each token has its kind, its keyword kind and its `Start` and `End` offsets in the query string.

```csharp
using PgSqlParser;

var query = "SELECT 1";
var result = Parser.Scan(query);

if (result.Error is null)
{
    foreach (var token in result.Value!.Tokens)
    {
        Console.WriteLine($"{query[token.Start..token.End]}: {token.Token}");
    }
}

// result.Value is a ScanResult object and the serialized JSON output is as below
// { "version": 180006, "tokens": [ { "end": 6, "token": "SELECT", "keywordKind": "RESERVED_KEYWORD" }, { "start": 7, "end": 8, "token": "ICONST" } ] }
```

## Tokenize

Split a query into tokens classified for display, for syntax highlighting and similar uses. It works on SQL that does not parse, as long as it can be tokenized.

```csharp
using PgSqlParser;

var query = "SELECT name FROM users WHERE id = $1 -- by id";
foreach (var token in Parser.Tokenize(query).GetValueOrThrow())
{
    Console.WriteLine($"{query[token.Start..token.End]}: {token.Kind}");
}
// Output: SELECT: Keyword, name: Keyword, FROM: Keyword, users: Identifier, WHERE: Keyword, id: Identifier,
//         =: Operator, $1: Parameter, -- by id: Comment
```

`name` is a keyword to PostgreSQL, though an unreserved one; `token.KeywordKind` tells reserved keywords from those usable as names.

## SplitWithScanner

Split a SQL script containing multiple statements into an array of clean, standalone SQL statements using lexical (token-based) analysis.

```csharp
using PgSqlParser;

var query = "SELECT 1; SELECT 2";
var result = Parser.SplitWithScanner(query);

if (result.Error is null)
{
    Console.WriteLine(JsonSerializer.Serialize(result.Value));
}

// Return a `SplitResult` and the respective serialized JSON string is as below:
// {"Statements":[{"Location":0,"Length":8,"Text":"SELECT 1"},{"Location":9,"Length":9,"Text":" SELECT 2"}]}
```

## SplitWithParser

Split a SQL script containing multiple statements into an array of clean, standalone SQL statements using Postgres full parser

```csharp
using PgSqlParser;

var query = "SELECT 1; SELECT 2";
var result = Parser.SplitWithParser(query);

if (result.Error is null)
{
    Console.WriteLine(JsonSerializer.Serialize(result.Value));
}

// Return a `SplitResult` and the respective serialized JSON string is as below:
// {"Statements":[{"Location":0,"Length":8,"Text":"SELECT 1"},{"Location":10,"Length":8,"Text":"SELECT 2"}]}
```

