# Deparse, format and normalize

## Deparse

Deparse AST back into a query string

```csharp
using PgSqlParser;

var parseJson = """
{ "version": 180006, "stmts": [ { "stmt": { "SelectStmt": { "targetList": [ { "ResTarget": { "val": { "A_Const": { "ival": { "ival": 1 }, "location": 7 } }, "location": 7 } } ], "limitOption": "LIMIT_OPTION_DEFAULT", "op": "SETOP_NONE" } } } ] }
""";

var parseResult = ParseResult.Parser.ParseJson(parseJson); ;
var result = Parser.Deparse(parseResult);

if (result.Error is null)
{
    Console.WriteLine(result.Value);
}

// Output: SELECT 1
```

Pass `DeparseOptions` to pretty print the output:

```csharp
var parseResult = Parser.Parse("SELECT a, b FROM t WHERE x = 1 AND y = 2").Value!;
var result = Parser.Deparse(parseResult, new DeparseOptions { PrettyPrint = true });

// Output:
// SELECT a, b
// FROM t
// WHERE
//     x = 1
//     AND y = 2
```

Comments are not part of the parse tree. To keep them, extract them with `DeparseComments` and pass them back in:

```csharp
var query = "SELECT 1 /* one */";
var parseResult = Parser.Parse(query).Value!;
var comments = Parser.DeparseComments(query).Value!;
var result = Parser.Deparse(parseResult, new DeparseOptions { Comments = comments });

// Output: SELECT 1 /* one */
```

## Format

Format a query across indented lines, keeping its comments. Each statement of a script is formatted on its own.

```csharp
using PgSqlParser;

var formatted = Parser.Format("select a, b from t -- pick\n where x = 1; delete from t where a = 1").GetValueOrThrow();
Console.WriteLine(formatted);
// Output:
// SELECT a, b
// FROM t
// WHERE
//     -- pick
//     x = 1;
//
// DELETE FROM t
// WHERE a = 1;
```

`FormatOptions` sets the indent size, the line length, comma placement, a trailing newline, and whether comments are kept.

## Normalize

Transform DML query (SELECT, INSERT, UPDATE, DELETE) into a canonical form by replacing literal values (constants) with placeholders ($1, $2)

```csharp
using PgSqlParser;

var query = "SELECT 1";
var result = Parser.Normalize(query);

if (result.Error is null)
{
    Console.WriteLine(result.Value);
}

// Normalized value: SELECT $1

```

## NormalizeUtility

Transform DDL and other utility commands (CREATE TABLE, ALTER TABLE, VACUUM, and ANALYZE et.al.) into a canonical form by replacing literal values (constants) with placeholders ($1, $2)

```csharp
using PgSqlParser;

var query = "CREATE ROLE postgres PASSWORD 'xyz'";
var result = Parser.NormalizeUtility(query);

if (result.Error is null)
{
    Console.WriteLine(result.Value);
}

// Normalized Utility value: CREATE ROLE postgres PASSWORD $1
```

## Fingerprint

Generate a normalized hash (fingerprint) of a SQL statement, ignoring literals, whitespace, and minor variations

```csharp
using PgSqlParser;

var query = "SELECT 1";
var result = Parser.Fingerprint(query);

if (result.Error is null)
{
    Console.WriteLine(result.Value);
}

/// output: 50fde20626009aba
```

Fingerprints follow the PostgreSQL 18 query ID rules by default: in SELECT/DML statements a table alias replaces the relation name, and schema names are ignored. Pass `FingerprintOptions` to change this, for example to get the same fingerprints as PostgreSQL 17 and earlier:

```csharp
var result = Parser.Fingerprint(query, ParserOptions.Default, FingerprintOptions.RangeVarPg17Compat);
```

