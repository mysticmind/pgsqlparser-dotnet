# Query analysis

## Classify

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

## References

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

Each column also has a `Clause` (select list, WHERE, join condition, GROUP BY, and so on), so the columns that filter rows are `references.Columns.Where(c => c.Clause is QueryClause.Where or QueryClause.JoinCondition)`. For a SELECT, `GetOutputColumns()` lists the result columns with the names PostgreSQL gives them:

```csharp
var select = Parser.Parse("SELECT id, lower(name), total AS amount, 1 FROM t").GetValueOrThrow().Stmts[0].Stmt.SelectStmt;
Console.WriteLine(string.Join(", ", select.GetOutputColumns().Select(column => column.Name)));
// Output: id, lower, amount, ?column?
```

## Locks

Find out which table locks a statement takes, and what they block. This follows PostgreSQL's documented lock rules, and for ALTER TABLE the same per-sub-command rules PostgreSQL itself uses.

```csharp
using PgSqlParser;

var script = "CREATE INDEX i ON orders (customer_id); ALTER TABLE orders ADD COLUMN note text; ALTER TABLE orders VALIDATE CONSTRAINT fk";
foreach (var tableLock in Parser.Parse(script).GetValueOrThrow().GetLocks())
{
    Console.WriteLine($"{tableLock}  blocks reads: {tableLock.Mode.BlocksReads()}, blocks writes: {tableLock.Mode.BlocksWrites()}");
}
// Output:
// orders: Share  blocks reads: False, blocks writes: True
// orders: AccessExclusive  blocks reads: True, blocks writes: True
// orders: ShareUpdateExclusive  blocks reads: False, blocks writes: False
```

It reports locks on the tables a statement names. It cannot see locks on tables reached through a view, an index name, a partition or a trigger, since those need the database catalog, and it does not cover row-level locks. See the documentation of `GetLocks` for the list of statements covered.

## Summary

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

## OperationSummary

Describe a query in a few low-cardinality words: the operation and the tables it touches. Literals, parameters and columns are left out, so it is safe as a span name or a metric label. This is the form OpenTelemetry's `db.query.summary` attribute asks for.

```csharp
using PgSqlParser;

Console.WriteLine(Parser.OperationSummary("SELECT c.name FROM customers c JOIN orders o ON o.cid = c.id WHERE c.email = 'a@b.c'").GetValueOrThrow());
// Output: SELECT customers orders

Console.WriteLine(Parser.OperationSummary("INSERT INTO audit SELECT * FROM users").GetValueOrThrow());
// Output: INSERT audit users
```

## ParameterRefs

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

## IsUtilityStmt

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

