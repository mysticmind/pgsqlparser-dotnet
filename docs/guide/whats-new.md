# What's new

## What's new in 2.1

All additions; nothing from 2.0 changes.

**Understanding a statement**

- **Classify statements**: `Parser.Classify` gives each statement's kind, whether it is read-only, and facts such as a data-modifying CTE, `SELECT INTO`, a locking clause or `EXPLAIN ANALYZE`.
- **Resolved references**: `GetReferences` returns the tables, functions and columns of a query as nodes, with table roles (read, write, DDL), CTE detection and columns matched to their table.
- **Parameters**: `ParameterRefs` finds each `$n`, where it is, and the type it is cast to.
- **Query shape**: each column reference knows its clause (WHERE, join condition, GROUP BY, ...), and `GetOutputColumns` names a SELECT's result columns as PostgreSQL does.
- **Locks**: `GetLocks` reports the table locks a statement takes and what they block.
- **Inside PL/pgSQL**: `ParseQueries` parses the SQL in a function body.

**Working with the tree**

- **Deparse a single node**: `node.Deparse()` turns one clause, expression or table reference back into SQL.
- **More control when walking**: skip a subtree or stop, and each visit knows its ancestors, the property it came from and its top-level statement.
- **Typed visitors**: `NodeVisitor` with a handler per node type, and several visitors in one walk.
- **Compare trees**: `EqualsIgnoringLocations` treats queries that differ only in formatting as equal.

**Changing a statement**

- **Build nodes**: `Ast.Column`, `Ast.Const`, `Ast.Eq`, `Ast.And`, `Ast.Call`, `Ast.Table`, `Ast.Expression` and more, each giving the same node the parser would.
- **Rewrite**: `Rewrite` keeps, replaces or removes nodes in place.
- **Common changes ready made**: `AddWhere`, `SetLimit`, `CapLimit`, `SetOffset`, `ToCount`, `QualifyTables`, `RenameSchema`, `RenameTable`, `ToDropStatement`, `EnsureIfNotExists`, `EnsureOrReplace`.

**Working with text**

- **Format in one call**: `Parser.Format` pretty prints a query or a script and keeps its comments.
- **Readable errors**: `Error.Format` and `Error.GetLineAndColumn`.
- **Tokens for display**: `Parser.Tokenize` classifies each token as keyword, identifier, literal, comment and so on.
- **Telemetry summary**: `Parser.OperationSummary` gives the short form OpenTelemetry's `db.query.summary` asks for.
- **Parse fragments**: `ParseExpression` and `ParseTypeName`.
- **Identifier quoting**: `PgIdentifier.Quote`, following PostgreSQL's `quote_ident`.

## What's new in 2.0

- **PostgreSQL 18**: built on libpg_query 18.1.0, which uses the PostgreSQL 18.6 parser and includes its security fix for `pg_query_normalize`.
- **More platforms**: Linux on ARM64, Alpine and other musl-based Linux, and Windows on ARM64. The Linux and macOS libraries are now built against old OS baselines, so they load on older systems too (see the table under [Installation](/guide/getting-started#installation)).
- **.NET 10**, alongside .NET 8 and .NET 9, and compatibility with trimming and Native AOT.
- **New functions**: `IsUtilityStmt`, `Summary`, `DeparseComments` and `ParsePlpgsqlFunctions`.
- **Deparse options**: pretty printing and keeping comments, through `DeparseOptions`.
- **Fingerprint options**: `FingerprintOptions`, including a PostgreSQL 17 compatible mode.
- **Easier results**: `GetValueOrThrow`, `TryGetValue`, `Match` and deconstruction on `Result<T>` (see [Working with results](/guide/getting-started#working-with-results)).
- **Parse tree navigation**: `Descendants<T>`, `Walk`, `Unwrap`, `GetLocation` and `GetText` (see [Navigating the parse tree](/guide/parse-tree#navigating-the-parse-tree)).
- **Correct offsets for non-ASCII text**: `Scan` and split results index the query string directly, with `Utf8OffsetMapper` and `Error.GetCursorCharOffset` for the rest (see [Offsets and non-ASCII text](/guide/errors-offsets#offsets-and-non-ascii-text)).
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

