# PL/pgSQL

## ParsePlpgsql

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

`function.ParseQueries()` goes one step further and parses each of those pieces of SQL, in the mode PL/pgSQL itself uses for it, so a function body can be analysed with the same tools as any query:

```csharp
var tables = function.ParseQueries()
    .Where(query => query.Tree is not null)
    .SelectMany(query => query.Tree!.GetReferences().Tables)
    .Select(table => table.Name);
// foo
```

libpg_query has no schema for its PL/pgSQL output, so a node (`PlpgsqlNode`) exposes its `Kind`, its `Children` and its raw `Json` (a `JsonElement`) and not typed properties per statement kind.

