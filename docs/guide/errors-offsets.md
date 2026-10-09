# Errors and offsets

## Parse Errors

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

The `Error` also carries `CursorPos`, the position of the error in the query (see [Offsets and non-ASCII text](/guide/errors-offsets#offsets-and-non-ascii-text)), and the PostgreSQL source location that raised it in `FuncName`, `FileName` and `LineNo`.

Two kinds of invalid input are caught before the query reaches libpg_query:

- A `null` query throws `ArgumentNullException`.
- A query containing a NUL character (`\0`) returns an `Error` with the message `query contains a NUL character`. PostgreSQL does not allow NUL in queries.

Deeply nested queries, such as a long chain of operators without parentheses (`a || b || c ...`) or many nested subqueries, are read on a dedicated thread with a large enough stack, so they cannot overflow the stack of the calling thread. A parse tree nested more than 4000 levels deep is rejected with an `Error`. The PostgreSQL parser has its own limit, which depends on the stack available to the calling thread and reports `stack depth limit exceeded`.

Protobuf's own recursive operations on a parse tree, such as `ToString()`, `Clone()` and `Equals()`, run on your thread. On a very deeply nested tree they can still overflow a small stack.

## Offsets and non-ASCII text

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

