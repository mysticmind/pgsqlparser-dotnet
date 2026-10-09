# PgSqlParser [![Nuget Package](https://badgen.net/nuget/v/pgsqlparser)](https://www.nuget.org/packages/pgsqlparser/)

.NET version of [https://github.com/pganalyze/libpg_query](https://github.com/pganalyze/libpg_query). This .NET wrapper on libpg_query C library which uses the actual PostgreSQL server source to parse SQL queries and return the internal PostgreSQL parse tree.

You can find further background to why a query's parse tree is useful here: [https://pganalyze.com/blog/parse-postgresql-queries-in-ruby.html](https://pganalyze.com/blog/pg-query-2-0-postgres-query-parser)

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

### Working with results

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

Parse SQL and return a Protobuf based AST

```csharp
using PgSqlParser;

var query = "SELECT 1; SELECT 2";
        
var result = Parser.Parse(query);

if (result.Error is null)
{
    Console.WriteLine(result.Value);
}

// result.Value is a ParseResult AST object and the serialized JSON output is as below
// { "version": 180006, "stmts": [ { "stmt": { "SelectStmt": { "targetList": [ { "ResTarget": { "val": { "A_Const": { "ival": { "ival": 1 }, "location": 7 } }, "location": 7 } } ], "limitOption": "LIMIT_OPTION_DEFAULT", "op": "SETOP_NONE" } }, "stmt_len": 8 }, { "stmt": { "SelectStmt": { "targetList": [ { "ResTarget": { "val": { "A_Const": { "ival": { "ival": 2 }, "location": 17 } }, "location": 17 } } ], "limitOption": "LIMIT_OPTION_DEFAULT", "op": "SETOP_NONE" } }, "stmt_location": 10 } ] }
```

`Parse` can also take `ParserOptions` as a list of flags i.e. `ParserOptions.DisableBackslashQuote | ParserOptions.DisableEscapeStringWarning`

### Navigating the parse tree

The parse tree has more than 270 node types, and reaching a node by hand means spelling out the whole path to it. These helpers avoid that:

```csharp
using PgSqlParser;

var query = "SELECT c.name FROM customers c JOIN orders o ON o.customer_id = c.id; DROP TABLE tmp";
var tree = Parser.Parse(query).GetValueOrThrow();

// Every node of a type, anywhere in the tree
foreach (var table in tree.Descendants<RangeVar>())
{
    Console.WriteLine(table.Relname);
}
// Output: customers, orders (DROP TABLE names its target as a plain name list, not a RangeVar)

// Every node, with its parent and depth
foreach (var visit in tree.Walk())
{
    Console.WriteLine($"{new string(' ', visit.Depth * 2)}{visit.Node.Descriptor.Name}");
}

// Pattern match on a statement without the .SelectStmt / .DropStmt property chain
foreach (var stmt in tree.Stmts)
{
    var kind = stmt.Stmt.Unwrap() switch
    {
        SelectStmt => "select",
        DropStmt => "drop",
        _ => "other"
    };
    // The statement's text and kind
    Console.WriteLine($"{kind}: {stmt.GetText(query)}");
}
// Output:
// select: SELECT c.name FROM customers c JOIN orders o ON o.customer_id = c.id
// drop: DROP TABLE tmp
```

Any node can be turned back into SQL on its own, which is how to get the text of a single clause or expression:

```csharp
var select = tree.Stmts[0].Stmt.SelectStmt;

Console.WriteLine(select.FromClause[0].Deparse().GetValueOrThrow());
// Output: customers c JOIN orders o ON o.customer_id = c.id

foreach (var table in tree.Descendants<RangeVar>())
{
    Console.WriteLine(table.Deparse().GetValueOrThrow());
}
// Output: customers c, orders o
```

`Deparse()` on a node works for statements, expressions, items of a FROM clause, select list and ORDER BY items, WITH clauses and type names. Nodes that are not SQL on their own, such as an `Alias`, return an error.

To control the walk, pass a function. It can skip everything below a node or stop the walk, and each visit tells you which property of its parent the node came from:

```csharp
// Tables used by the query itself, ignoring subqueries inside expressions
var tables = new List<string>();
tree.Walk(visit =>
{
    if (visit.Node is SubLink)
        return WalkAction.SkipChildren;

    if (visit.Node is RangeVar table)
        tables.Add(table.Relname);

    return WalkAction.Continue;   // or WalkAction.Stop to end the walk
});

// visit.FieldName is the parent's property, for example nameof(SelectStmt.WhereClause);
// visit.Index is the position when that property is a list.
```

Each visit also knows where it sits in the tree, which is what most rules need:

```csharp
foreach (var visit in tree.Walk())
{
    if (visit.Node is not RangeVar table)
        continue;

    // The nearest containing node of a type, or null
    var insideCte = visit.FindAncestor<CommonTableExpr>() is not null;
    var insideSubquery = visit.FindAncestor<SubLink>() is not null;

    // The top-level statement the node belongs to
    var writes = visit.Statement?.Stmt.Unwrap() is InsertStmt or UpdateStmt or DeleteStmt;

    Console.WriteLine($"statement {visit.StatementIndex}: {table.Relname} (cte: {insideCte}, subquery: {insideSubquery}, writes: {writes})");
}
```

`visit.Ancestors` lists every containing node, nearest first.

#### Changing and comparing trees

`Rewrite` walks the tree and lets you keep, replace or remove each node. It changes the tree in place, visiting children before their parents, and the result can be deparsed:

```csharp
var tree = Parser.Parse("SELECT a, secret FROM old_name WHERE kind = 'x'::text").GetValueOrThrow();

tree.Rewrite(visit => visit.Node switch
{
    // Point the query at another table
    RangeVar { Relname: "old_name" } => NodeEdit.ReplaceWith(new RangeVar { Relname = "new_name", Inh = true, Relpersistence = "p" }),
    // Drop a cast, keeping what it wraps
    TypeCast cast => NodeEdit.ReplaceWith(cast.Arg),
    // Remove an item from a list
    ResTarget target when target.Deparse().Value == "secret" => NodeEdit.Remove,
    _ => NodeEdit.Keep
});

Console.WriteLine(tree.Deparse().GetValueOrThrow());
// Output: SELECT a FROM new_name WHERE kind = 'x'
```

`EqualsIgnoringLocations` compares two trees, or two nodes, by structure. Queries that differ only in whitespace, comments, keyword case or redundant parentheses are equal:

```csharp
var a = Parser.Parse("SELECT a FROM t WHERE x <> 1").GetValueOrThrow();
var b = Parser.Parse("select a\nfrom t -- note\nwhere (x != 1)").GetValueOrThrow();

Console.WriteLine(a.EqualsIgnoringLocations(b));
// Output: True
```

A few helpers go with these: `Parser.ParseExpression` and `Parser.ParseTypeName` parse a single expression or type name on its own, `node.AsNode()` wraps a node for a property or list that takes any node, and `PgIdentifier.Quote` quotes a name the way PostgreSQL's `quote_ident` does.

`Walk` and `Descendants` visit parents before their children and siblings in field order, which is not always the order of the query text. `GetLocation()` returns a node's location if it has one; see [Offsets and non-ASCII text](#offsets-and-non-ascii-text) for its unit. Only statements record a length, so `GetText` is available for statements and not for other nodes.

### Scan

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

### Classify

Get the facts about each statement: its kind, whether it is read-only, and anything that makes a harmless-looking statement write. This is the basis for read/write routing and for checking SQL before running it.

```csharp
using PgSqlParser;

foreach (var info in Parser.Classify("SELECT 1; WITH gone AS (DELETE FROM t RETURNING *) SELECT * FROM gone; EXPLAIN ANALYZE DELETE FROM t").GetValueOrThrow())
{
    Console.WriteLine($"{info.Kind}: read-only {info.IsReadOnly}");
}
// Output:
// Select: read-only True
// Select: read-only False    (HasDataModifyingCte is true)
// Explain: read-only False   (ExecutesInner is true, and Inner is a DELETE)
```

`StatementInfo` also has `IsUtility`, `HasSelectInto`, `HasLockingClause`, `RunsProgram` and `Inner` (the statement wrapped by EXPLAIN, PREPARE, DECLARE CURSOR, CREATE TABLE AS or COPY). `IsReadOnly` errs on the side of false: CALL, DO and EXECUTE are not read-only because what they run is not visible. A function called from a SELECT can still write, which no parser can see. These are facts, not a security boundary; what to allow is up to you.

### References

Find the tables, functions and columns a query refers to, as parse tree nodes with their role:

```csharp
using PgSqlParser;

var tree = Parser.Parse(
    "WITH recent AS (SELECT * FROM orders) " +
    "UPDATE customers c SET total = r.total FROM recent r WHERE r.customer_id = c.id").GetValueOrThrow();
var references = tree.GetReferences();

foreach (var table in references.Tables)
{
    Console.WriteLine($"{table.Name}: {table.Role}{(table.IsCte ? " (a CTE, not a table)" : "")}");
}
// Output:
// customers: Write
// recent: Read (a CTE, not a table)
// orders: Read

foreach (var column in references.Columns)
{
    Console.WriteLine($"{column} -> {column.Table?.Name ?? "unknown"}");
}
// Output:
// r.total -> unknown        (recent is a CTE)
// r.customer_id -> unknown
// c.id -> customers
// * -> orders
```

A column is matched to its table when the query alone settles it: its qualifier names a table or alias in scope, or it has no qualifier and there is only one table in scope. Otherwise `Table` is null, because it would take the database catalog to tell. `Summary` returns similar information as plain names and is faster; use `GetReferences` when you need the nodes, aliases or column matching.

### Format

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

### ParameterRefs

Find the parameter references (`$1`, `$2`, ...) in a query, with where each one is in the query string. References inside string literals and comments are not parameters and are left out.

```csharp
using PgSqlParser;

var query = "SELECT * FROM t WHERE a = $1 AND b = $2 AND note <> '$3'";
var parameters = Parser.ParameterRefs(query).GetValueOrThrow();

foreach (var parameter in parameters)
{
    Console.WriteLine($"${parameter.Number} at {parameter.Start}..{parameter.End}");
}
// Output: $1 at 26..28, $2 at 37..39
```

When a parameter is cast in the query, `TypeName` holds the type: `$1::int` gives `int`, and `CAST($2 AS numeric(10,2))` gives `numeric(10, 2)`. It is null for a parameter that is not cast.

### ParsePlpgsql

Parse PL/pgSQL function bodies and return a JSON representation

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
// [{"PLpgSQL_function":{"datums":[{"PLpgSQL_var":{"refname":"found","datatype":{"PLpgSQL_type":{"typname":"bool"}}}},{"PLpgSQL_var":{"refname":"r","lineno":3,"datatype":{"PLpgSQL_type":{"typname":"foo%rowtype"}}}},{"PLpgSQL_row":{"refname":"(unnamed row)","lineno":5,"fields":[{"name":"r","varno":1}]}}],"action":{"PLpgSQL_stmt_block":{"lineno":4,"body":[{"PLpgSQL_stmt_fors":{"lineno":5,"var":{"PLpgSQL_row":{"refname":"(unnamed row)","lineno":5,"fields":[{"name":"r","varno":1}]}},"body":[{"PLpgSQL_stmt_return_next":{"lineno":9}}],"query":{"PLpgSQL_expr":{"query":"SELECT * FROM foo WHERE fooid \u003e 0","parseMode":0}}}},{"PLpgSQL_stmt_return":{"lineno":11}}]}}}}]
```

`ParsePlpgsqlFunctions` returns the same information as objects, one `PlpgsqlFunction` per `CREATE FUNCTION` or `DO` statement:

```csharp
var function = Parser.ParsePlpgsqlFunctions(sql).GetValueOrThrow()[0];

// Variables and parameters
foreach (var datum in function.Datums)
{
    Console.WriteLine($"{datum.Kind}: {datum.GetString("refname")}");
}

// Every statement, including nested ones
foreach (var statement in function.Statements())
{
    Console.WriteLine($"line {statement.LineNo}: {statement.Kind}");
}

// The SQL inside the function, ready to pass to Parser.Parse
foreach (var query in function.Queries())
{
    Console.WriteLine(query);
}
// Output: SELECT * FROM foo WHERE fooid > 0
```

libpg_query has no schema for its PL/pgSQL output, so a node (`PlpgsqlNode`) exposes its `Kind`, its `Children` and its raw `Json` (a `JsonElement`) and not typed properties per statement kind.

### Fingerprint

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

`error.Format(query)` renders the error the way psql does, with the line it points at, and `error.GetLineAndColumn(query)` gives the position:

```csharp
var query = "SELECT a,\n  b FROM WHERE x";
var error = Parser.Parse(query).Error!;

Console.WriteLine(error.Format(query));
// Output:
// ERROR:  syntax error at or near "WHERE"
// LINE 2:   b FROM WHERE x
//                  ^
```

The `Error` also carries `CursorPos`, the position of the error in the query (see [Offsets and non-ASCII text](#offsets-and-non-ascii-text)), and the PostgreSQL source location that raised it in `FuncName`, `FileName` and `LineNo`.

Two kinds of invalid input are caught before the query reaches libpg_query:

- A `null` query throws `ArgumentNullException`.
- A query containing a NUL character (`\0`) returns an `Error` with the message `query contains a NUL character`. PostgreSQL does not allow NUL in queries.

Deeply nested queries, such as a long chain of operators without parentheses (`a || b || c ...`) or many nested subqueries, are read on a dedicated thread with a large enough stack, so they cannot overflow the stack of the calling thread. A parse tree nested more than 4000 levels deep is rejected with an `Error`. The PostgreSQL parser has its own limit, which depends on the stack available to the calling thread and reports `stack depth limit exceeded`.

Protobuf's own recursive operations on a parse tree, such as `ToString()`, `Clone()` and `Equals()`, run on your thread. On a very deeply nested tree they can still overflow a small stack.

## What's new in 2.2

All additions; nothing from earlier versions changes.

- **Classify statements**: `Parser.Classify` gives each statement's kind, whether it is read-only, and facts such as a data-modifying CTE, `SELECT INTO`, a locking clause or `EXPLAIN ANALYZE`.
- **Resolved references**: `GetReferences` returns the tables, functions and columns of a query as nodes, with table roles (read, write, DDL), CTE detection and columns matched to their table.
- **Format in one call**: `Parser.Format` pretty prints a query or a script and keeps its comments.
- **Readable errors**: `Error.Format` and `Error.GetLineAndColumn`.

## What's new in 2.1

All additions; nothing from 2.0 changes.

- **Deparse a single node**: `node.Deparse()` turns one clause, expression or table reference back into SQL.
- **More control when walking**: skip a subtree or stop, and each visit knows its ancestors, the property it came from and its top-level statement.
- **Change trees**: `Rewrite` keeps, replaces or removes nodes in place.
- **Compare trees**: `EqualsIgnoringLocations` treats queries that differ only in formatting as equal.
- **Parse fragments**: `ParseExpression` and `ParseTypeName`.
- **Parameters**: `ParameterRefs` finds each `$n`, where it is, and the type it is cast to.
- **Identifier quoting**: `PgIdentifier.Quote`, following PostgreSQL's `quote_ident`.

See [Navigating the parse tree](#navigating-the-parse-tree) and [ParameterRefs](#parameterrefs).

## What's new in 2.0

- **PostgreSQL 18**: built on libpg_query 18.1.0, which uses the PostgreSQL 18.6 parser and includes its security fix for `pg_query_normalize`.
- **More platforms**: Linux on ARM64, Alpine and other musl-based Linux, and Windows on ARM64. The Linux and macOS libraries are now built against old OS baselines, so they load on older systems too (see the table under [Installation](#installation)).
- **.NET 10**, alongside .NET 8 and .NET 9, and compatibility with trimming and Native AOT.
- **New functions**: `IsUtilityStmt`, `Summary`, `DeparseComments` and `ParsePlpgsqlFunctions`.
- **Deparse options**: pretty printing and keeping comments, through `DeparseOptions`.
- **Fingerprint options**: `FingerprintOptions`, including a PostgreSQL 17 compatible mode.
- **Easier results**: `GetValueOrThrow`, `TryGetValue`, `Match` and deconstruction on `Result<T>` (see [Working with results](#working-with-results)).
- **Parse tree navigation**: `Descendants<T>`, `Walk`, `Unwrap`, `GetLocation` and `GetText` (see [Navigating the parse tree](#navigating-the-parse-tree)).
- **Correct offsets for non-ASCII text**: `Scan` and split results index the query string directly, with `Utf8OffsetMapper` and `Error.GetCursorCharOffset` for the rest (see [Offsets and non-ASCII text](#offsets-and-non-ascii-text)).
- **Safer input handling**: a `null` query, a NUL character or a deeply nested query no longer crashes the process or is silently cut short.
- **Package**: IntelliSense documentation, a symbol package and Source Link are included.

## Upgrading from 1.x

Version 2.0 moves from the PostgreSQL 17 parser to PostgreSQL 18, which changes some results:

- **Parse tree**: node classes and fields follow PostgreSQL 18, and `ParseResult.Version` is `180006`.
- **Fingerprints**: the default now follows PostgreSQL 18 query ID rules, so stored fingerprints for queries that use table aliases or schema names will change. Pass `FingerprintOptions.RangeVarPg17Compat` to keep the previous relation handling.
- **Statement locations**: a statement now starts at its first non-whitespace, non-comment character. This affects `SplitWithParser` and `RawStmt.StmtLocation`; for `SELECT 1; SELECT 2` the second statement is at location 10 with length 8, where it was 9 and 9.
- **Scan and Split offsets**: `Scan` token offsets and `SplitStmt` locations are UTF-16 offsets into the query string. In 1.x they were UTF-8 byte offsets, which differ as soon as the query contains non-ASCII text. Remove any conversion you did yourself.
- **Errors**: `Error.FuncName` and `Error.FileName` now hold the function and file name. In 1.x `FuncName` held the message and the file name was missing.
- **`Parse` return type**: `Parse` and `ParseAsync` return `Result<ParseResult>`, where 1.x returned `Result<ParseResult?>`. Code that declared the nullable type explicitly needs updating.
- **Dependencies**: the minimum `Google.Protobuf` version is 3.36.2.

## License

PgSqlParser is licensed under the [MIT License](LICENSE).

Copyright (c) 2026, Babu Annamalai <babu.annamalai@gmail.com>

Refer to [libpg_query license](https://github.com/pganalyze/libpg_query?tab=readme-ov-file#license) for license details on libpg_query.

This project includes code derived from the [PostgreSQL project](http://www.postgresql.org/),
see [LICENSE.POSTGRESQL](LICENSE.POSTGRESQL) for details.
