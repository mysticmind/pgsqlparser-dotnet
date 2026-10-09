# Parse trees

## Parse

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

## Navigating the parse tree

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

### Changing and comparing trees

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

### Building nodes and common changes

`Ast` builds nodes without writing out their fields. Each method gives the same node the parser would for the matching SQL, and `Ast.Expression` parses a piece of SQL when that is easier:

```csharp
var condition = Ast.And(
    Ast.Eq(Ast.Column("tenant_id"), Ast.Param(1)),
    Ast.IsNull(Ast.Column("deleted_at")));
// The same as Ast.Expression("tenant_id = $1 AND deleted_at IS NULL")
```

The usual changes to a statement are ready made. They work on the tree, so they hold however the SQL was written:

```csharp
var tree = Parser.Parse("SELECT id, name FROM customers WHERE active ORDER BY name").GetValueOrThrow();

tree.AddWhere(condition);      // combined with the existing WHERE using AND
tree.CapLimit(100);            // adds LIMIT 100, or lowers a larger one
tree.QualifyTables("app");     // gives unqualified tables a schema, leaving CTE names alone

Console.WriteLine(tree.Deparse().GetValueOrThrow());
// Output: SELECT id, name FROM app.customers WHERE active AND tenant_id = $1 AND deleted_at IS NULL ORDER BY name LIMIT 100

Console.WriteLine(tree.ToCount().Deparse().GetValueOrThrow());
// Output: SELECT count(*) FROM (SELECT id, name FROM app.customers WHERE active AND tenant_id = $1 AND deleted_at IS NULL ORDER BY name LIMIT 100) q
```

Also available: `SetLimit`, `SetOffset`, `RenameSchema`, `RenameTable`, and for DDL `ToDropStatement` (the DROP that undoes a CREATE), `EnsureIfNotExists` and `EnsureOrReplace`.

### Typed visitors

A `NodeVisitor` holds a handler per node type. Several visitors can share one walk, and each skips or stops on its own:

```csharp
var tables = new List<string>();
var functions = new List<string>();

tree.Walk(
    new NodeVisitor().On<RangeVar>((table, visit) => tables.Add(table.Relname)),
    new NodeVisitor().On<FuncCall>((call, visit) => functions.Add(call.Funcname[^1].String.Sval)));
```

A few helpers go with these: `Parser.ParseExpression` and `Parser.ParseTypeName` parse a single expression or type name on its own, `node.AsNode()` wraps a node for a property or list that takes any node, and `PgIdentifier.Quote` quotes a name the way PostgreSQL's `quote_ident` does.

`Walk` and `Descendants` visit parents before their children and siblings in field order, which is not always the order of the query text. `GetLocation()` returns a node's location if it has one; see [Offsets and non-ASCII text](/guide/errors-offsets#offsets-and-non-ascii-text) for its unit. Only statements record a length, so `GetText` is available for statements and not for other nodes.

