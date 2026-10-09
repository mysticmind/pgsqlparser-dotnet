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
Check("ParsePlpgsqlFunctions", Parser.ParsePlpgsqlFunctions(
    "CREATE FUNCTION f() RETURNS int AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql").Value?[0].Queries().SequenceEqual(["1"]) == true);
Check("Error", Parser.Parse("SELECT FROM WHERE").Error?.Message?.StartsWith("syntax error") == true);

return failed ? 1 : 0;
