# PgSqlParser [![Nuget Package](https://badgen.net/nuget/v/pgsqlparser)](https://www.nuget.org/packages/pgsqlparser/)

.NET version of [https://github.com/pganalyze/libpg_query](https://github.com/pganalyze/libpg_query). This .NET wrapper on libpg_query C library which uses the actual PostgreSQL server source to parse SQL queries and return the internal PostgreSQL parse tree.

You can find further background to why a query's parse tree is useful here: [https://pganalyze.com/blog/parse-postgresql-queries-in-ruby.html](https://pganalyze.com/blog/pg-query-2-0-postgres-query-parser)

## Installation

```csharp
dotnet add package pgsqlparser
```

Note that the libpg_query libs for all OS'es are already packaged with the assembly.

This version is built on libpg_query 18.1.0, which uses the PostgreSQL 18.6 parser. It targets .NET 8, .NET 9 and .NET 10.

## Usage

All functions support both sync and async versions.

### Normalize

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

### NormalizeUtility

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

### Parse

Parse SQL and return an Protobuf based AST

```csharp
using PgSqlParser;

var query = "SELECT 1; SELECT 2";
        
var result = Parser.Parse(query);

if (result.Error is null)
{
    Console.WriteLine(result.Value);
}

// result.Value is a ParseResult AST object and the serialized JSON output is as below
// { "version": 180006, "stmts": [ { "stmt": { "SelectStmt": { "targetList": [ { "ResTarget": { "val": { "A_Const": { "ival": { "ival": 1 }, "location": 7 } }, "location": 7 } } ], "limitOption": "LIMIT_OPTION_DEFAULT", "op": "SETOP_NONE" } } } ] }
```

`Parse` can also take `ParseOptions` as a list of flags i.e. `ParserOptions.DisableBackslashQuote | ParserOptions.DisableEscapeStringWarning`

### ParsePlpgsql

```csharp
using PgSqlParser;

var sql = """
CREATE OR REPLACE FUNCTION get_all_foo() RETURNS SETOF foo AS
$BODY$
DECLARE
r foo%rowtype;
BEGIN
FOR r IN
SELECT * FROM foo WHERE fooid > 0
  LOOP
      -- can do some processing here
      RETURN NEXT r; -- return current row of SELECT
END LOOP;
  RETURN;
END
$BODY$
LANGUAGE plpgsql;
""";

var result = Parser.ParsePlpgsql(sql);

if (result.Error is null)
{
    Console.WriteLine(result.Value);
}

// return JSON string
// [{"PLpgSQL_function":{"datums":[{"PLpgSQL_var":{"refname":"found","datatype":{"PLpgSQL_type":{"typname":"pg_catalog.\"boolean\""}}}},{"PLpgSQL_var":{"refname":"r","lineno":3,"datatype":{"PLpgSQL_type":{"typname":"foo%rowtype"}}}},{"PLpgSQL_row":{"refname":"(unnamed row)","lineno":5,"fields":[{"name":"r","varno":1}]}}],"action":{"PLpgSQL_stmt_block":{"lineno":4,"body":[{"PLpgSQL_stmt_fors":{"lineno":5,"var":{"PLpgSQL_row":{"refname":"(unnamed row)","lineno":5,"fields":[{"name":"r","varno":1}]}},"body":[{"PLpgSQL_stmt_return_next":{"lineno":9}}],"query":{"PLpgSQL_expr":{"query":"SELECT * FROM foo WHERE fooid \u003e 0","parseMode":0}}}},{"PLpgSQL_stmt_return":{"lineno":11}}]}}}}]
```

### Fingerprint

Generate a normalized hash (fingerprint) of a SQL statement — ignoring literals, whitespace, and minor variations

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

### IsUtilityStmt

Check whether each statement in a query is a utility statement (DDL and other commands that are not SELECT, INSERT, UPDATE, DELETE or MERGE)

```csharp
using PgSqlParser;

var query = "SELECT 1; SET fsync = off";
var result = Parser.IsUtilityStmt(query);

if (result.Error is null)
{
    Console.WriteLine(string.Join(", ", result.Value!));
}

// Output: False, True
```

### Summary

Summarize a query: the tables, aliases, CTE names, functions, filter columns and statement types it references. Pass a `truncateLimit` to also get a shortened version of the query text.

```csharp
using PgSqlParser;

var query = "SELECT lower(x.name) FROM public.test AS x WHERE x.a = 1";
var result = Parser.Summary(query);

if (result.Error is null)
{
    Console.WriteLine(result.Value);
}

// Return a `SummaryResult` and the respective JSON string is as below:
// { "tables": [ { "name": "public.test", "schemaName": "public", "tableName": "test", "context": "Select" } ], "aliases": { "x": "public.test" }, "functions": [ { "name": "lower", "functionName": "lower", "context": "Call" } ], "filterColumns": [ { "tableName": "x", "column": "a" } ], "statementTypes": [ "SelectStmt" ] }
```

### SplitWithScanner

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

### SplitWithParser

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

### Deparse

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

### Offsets and non-ASCII text

.NET strings are UTF-16 while libpg_query works on UTF-8 bytes, so the unit of an offset depends on where it comes from:

- `Scan` token `Start`/`End` and `SplitStmt.Location`/`Length` are UTF-16 offsets, ready to use with `string.Substring`.
- Parse tree locations (for example `RawStmt.StmtLocation` and node `Location`) and `DeparseComment.MatchLocation` are UTF-8 byte offsets. Use `Utf8OffsetMapper` to convert them:

```csharp
using PgSqlParser.Utils;

var mapper = new Utf8OffsetMapper(query);
var charOffset = mapper.ToCharOffset(node.Location);
```

It also converts the other way, for example to compare a position in the string (such as an editor caret) against parse tree locations:

```csharp
var byteOffset = mapper.ToByteOffset(caretIndex);
```

- `Error.CursorPos` is a 1-based position in Unicode code points, as PostgreSQL reports it. Use `Error.GetCursorCharOffset(query)` to get the matching string offset:

```csharp
var result = Parser.Parse(query);
if (result.Error is { } error)
{
    var charOffset = error.GetCursorCharOffset(query);
    if (charOffset >= 0)
    {
        Console.WriteLine($"{error.Message}: {query[charOffset..]}");
    }
}
```

### Parse Errors

If the SQL has some error while parsing then the result contains an Error object which provides details of the error.

```csharp
using PgSqlParser;

var query = "SELEC 1";
var result = Parser.Parse(query);
if (result.Error is not null)
{
    Console.WriteLine(result.Error.Message);
} 

// Output: syntax error at or near "SELEC"
```

## License

Copyright (c) 2025, Babu Annamalai <babu.annamalai@gmail.com>

Refer to [libpg_query license](https://github.com/pganalyze/libpg_query?tab=readme-ov-file#license) for license details on libpg_query.

This project includes code derived from the [PostgreSQL project](http://www.postgresql.org/),
see LICENSE.POSTGRESQL for details.
