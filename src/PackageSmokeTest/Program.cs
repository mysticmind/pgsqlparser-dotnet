// Consumes the packed NuGet package the way a client does, to check that the package itself works:
// its contents, and the native library NuGet and the runtime pick for the current platform.
// The unit tests cannot check this, because they reference the project and not the package.
//
// To run it locally from the repository root:
//
//   dotnet pack src/PgSqlParser/PgSqlParser.csproj -o artifacts/package -p:Version=2.0.0-local
//   dotnet run --project src/PackageSmokeTest -f net8.0 -p:PgSqlParserVersion=2.0.0-local

using System.Runtime.InteropServices;
using PgSqlParser;

Console.WriteLine($"{RuntimeInformation.RuntimeIdentifier}, {RuntimeInformation.FrameworkDescription}, PostgreSQL {Parser.PgVersion}");

var failed = false;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    failed |= !ok;
}

// Every call below goes into the native library, so one success already proves it loaded.
const string query = "SELECT 'Coût', '😀' FROM \"données\" WHERE id = 1; SELECT 2";

Check("Parse", Parser.Parse(query).Value?.Stmts.Count == 2);
Check("Scan", Parser.Scan(query).Value?.Tokens.Count == 13);
Check("SplitWithScanner", Parser.SplitWithScanner(query).Value?.Statements.Count == 2);
Check("SplitWithParser", Parser.SplitWithParser(query).Value?.Statements[1].Text == "SELECT 2");
Check("Normalize", Parser.Normalize("SELECT 1").Value == "SELECT $1");
Check("NormalizeUtility", Parser.NormalizeUtility("CREATE ROLE r PASSWORD 'x'").Value == "CREATE ROLE r PASSWORD $1");
Check("Fingerprint", Parser.Fingerprint("SELECT 1").Value == "50fde20626009aba");
Check("Deparse", Parser.Deparse(Parser.Parse("SELECT 1").Value!).Value == "SELECT 1");
Check("DeparseComments", Parser.DeparseComments("SELECT 1 /* one */").Value?.Count == 1);
Check("IsUtilityStmt", Parser.IsUtilityStmt("SHOW fsync").Value is [true]);
Check("Summary", Parser.Summary(query).Value?.Tables.Count == 1);
Check("ParsePlpgsql", Parser.ParsePlpgsql(
    "CREATE FUNCTION f() RETURNS int AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql").Value?.Contains("PLpgSQL_function") == true);
// Protobuf's JSON support is reflection-based, which is the part most at risk under Native AOT.
var tree = Parser.Parse("SELECT 1").Value!;
Check("ParseResult to JSON", tree.ToString().Contains("\"SelectStmt\""));
Check("ParseResult from JSON", Parser.Deparse(ParseResult.Parser.ParseJson(tree.ToString())).Value == "SELECT 1");
// The tree walker reads fields through Protobuf's reflection API, so check it under Native AOT too.
var walked = Parser.Parse(query).Value!;
Check("Descendants", walked.Descendants<RangeVar>().Select(table => table.Relname).SequenceEqual(["données"]));
Check("Unwrap", walked.Stmts[0].Stmt.Unwrap() is SelectStmt);
Check("GetText", walked.Stmts[1].GetText(query) == "SELECT 2");
Check("DeparseNode", walked.Stmts[0].Stmt.SelectStmt.WhereClause.Deparse().Value == "id = 1");
var skipped = 0;
walked.Walk(visit => { skipped++; return visit.Node is SelectStmt ? WalkAction.SkipChildren : WalkAction.Continue; });
Check("Walk with a visitor", skipped == 4 && walked.Walk().First().FieldName == "Stmts");
var tableVisit = walked.Walk().First(visit => visit.Node is RangeVar);
Check("Ancestors and statement", tableVisit.FindAncestor<SelectStmt>() is not null && tableVisit.StatementIndex == 0);
Check("ParameterRefs cast type", Parser.ParameterRefs("SELECT $1::int").Value?[0].TypeName == "int");
var edited = Parser.Parse("SELECT a, b FROM old_name WHERE c = 'x'::text").Value!;
edited.Rewrite(visit => visit.Node switch
{
    RangeVar => NodeEdit.ReplaceWith(new RangeVar { Relname = "new_name", Inh = true, Relpersistence = "p" }),
    TypeCast cast => NodeEdit.ReplaceWith(cast.Arg),
    ResTarget when visit.Index == 1 => NodeEdit.Remove,
    _ => NodeEdit.Keep
});
Check("Rewrite", edited.Deparse().Value == "SELECT a FROM new_name WHERE c = 'x'");
Check("EqualsIgnoringLocations", edited.EqualsIgnoringLocations(Parser.Parse("select a\nfrom new_name where (c = 'x')").Value));
Check("ParseExpression", Parser.ParseExpression("(a > 0)").Value?.Deparse().Value == "a > 0");
Check("ParseTypeName", Parser.ParseTypeName("character varying(20)").Value?.Deparse().Value == "varchar(20)");
Check("PgIdentifier", PgIdentifier.Quote("public", "order") == "public.\"order\"");
var classified = Parser.Classify("WITH d AS (DELETE FROM t RETURNING 1) SELECT * FROM d; EXPLAIN SELECT 1").Value!;
Check("Classify", classified is [{ Kind: StatementKind.Select, IsReadOnly: false }, { Kind: StatementKind.Explain, IsReadOnly: true }]);
var refs = Parser.Parse("UPDATE a SET x = b.y FROM b WHERE b.id = a.id").Value!.GetReferences();
Check("GetReferences", refs.Tables.Select(t => t.Role).SequenceEqual([TableRole.Write, TableRole.Read])
                       && refs.Columns.All(c => c.Table is not null));
Check("Format", Parser.Format("select a from t -- note\n where x = 1").Value == "SELECT a\nFROM t\nWHERE\n    -- note\n    x = 1");
Check("Error.Format", Parser.Parse("SELECT FROM WHERE").Error!.Format("SELECT FROM WHERE").EndsWith("            ^"));
var rewritten = Parser.Parse("SELECT id FROM customers WHERE active").Value!;
rewritten.AddWhere(Ast.And(Ast.Eq(Ast.Column("tenant_id"), Ast.Param(1)), Ast.IsNull(Ast.Column("deleted_at"))));
rewritten.CapLimit(100);
rewritten.QualifyTables("app");
Check("Ast and rewrites", rewritten.Deparse().Value == "SELECT id FROM app.customers WHERE active AND tenant_id = $1 AND deleted_at IS NULL LIMIT 100");
Check("ToDropStatement", Parser.Parse("CREATE TABLE s.t (a int)").Value!.ToDropStatement(ifExists: true).Deparse().Value == "DROP TABLE IF EXISTS s.t");
var visitedTables = 0;
rewritten.Walk(new NodeVisitor().On<RangeVar>((_, _) => visitedTables++), new NodeVisitor().On<ParamRef>((_, _) => WalkAction.Stop));
Check("NodeVisitor", visitedTables == 1);
Check("GetLocks", Parser.Parse("ALTER TABLE t VALIDATE CONSTRAINT c; CREATE INDEX i ON t (a)").Value!.GetLocks()
    .Select(l => l.Mode).SequenceEqual([LockMode.ShareUpdateExclusive, LockMode.Share]));
var shape = Parser.Parse("SELECT id, lower(name) AS n FROM t WHERE a = 1").Value!;
Check("Clauses and output columns", shape.GetReferences().Columns.Last().Clause == QueryClause.Where
                                    && shape.Stmts[0].Stmt.SelectStmt.GetOutputColumns().Select(c => c.Name).SequenceEqual(["id", "n"]));
Check("Tokenize", Parser.Tokenize("SELECT 1 -- x").Value?.Select(t => t.Kind)
    .SequenceEqual([SqlTokenKind.Keyword, SqlTokenKind.NumericLiteral, SqlTokenKind.Comment]) == true);
Check("OperationSummary", Parser.OperationSummary("SELECT * FROM a JOIN b ON true WHERE x = 'secret'").Value == "SELECT a b");
Check("PL/pgSQL ParseQueries", Parser.ParsePlpgsqlFunctions(
    "CREATE FUNCTION f() RETURNS void AS $$ BEGIN PERFORM x FROM items; END $$ LANGUAGE plpgsql").Value![0]
    .ParseQueries().Single().Tree!.GetReferences().Tables.Single().Name == "items");
Check("ParameterRefs", Parser.ParameterRefs("SELECT $1, '$2', $3").Value?.Select(p => p.Number).SequenceEqual([1, 3]) == true);
Check("ParsePlpgsqlFunctions", Parser.ParsePlpgsqlFunctions(
    "CREATE FUNCTION f() RETURNS int AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql").Value?[0].Queries().SequenceEqual(["1"]) == true);
Check("Error", Parser.Parse("SELECT FROM WHERE").Error?.Message?.StartsWith("syntax error") == true);

return failed ? 1 : 0;
