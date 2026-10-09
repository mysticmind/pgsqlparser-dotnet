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
Check("ParameterRefs", Parser.ParameterRefs("SELECT $1, '$2', $3").Value?.Select(p => p.Number).SequenceEqual([1, 3]) == true);
Check("ParsePlpgsqlFunctions", Parser.ParsePlpgsqlFunctions(
    "CREATE FUNCTION f() RETURNS int AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql").Value?[0].Queries().SequenceEqual(["1"]) == true);
Check("Error", Parser.Parse("SELECT FROM WHERE").Error?.Message?.StartsWith("syntax error") == true);

return failed ? 1 : 0;
