using System.Text.Json;
using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class PlpgsqlFunctionsTests
{
    private const string Sql = """
        CREATE FUNCTION get_all_foo(min_id int) RETURNS SETOF foo AS $$
        DECLARE
          r foo%rowtype;
          total int := 0;
        BEGIN
          FOR r IN SELECT * FROM foo WHERE fooid > min_id LOOP
            IF r.fooid > 100 THEN
              total := total + 1;
            END IF;
            RETURN NEXT r;
          END LOOP;
          RETURN;
        END
        $$ LANGUAGE plpgsql;
        """;

    [Fact]
    public void ExposesVariablesStatementsAndQueries()
    {
        var function = Parser.ParsePlpgsqlFunctions(Sql).GetValueOrThrow().ShouldHaveSingleItem();

        function.Root.Kind.ShouldBe("PLpgSQL_function");
        function.Datums.Select(datum => datum.GetString("refname"))
            .ShouldBe(["min_id", "found", "r", "total", "(unnamed row)"]);
        function.Datums.Select(datum => datum.Kind).Distinct().ShouldBe(["PLpgSQL_var", "PLpgSQL_row"]);
        function.Datums[3].LineNo.ShouldBe(4);
        function.Datums[0].LineNo.ShouldBeNull();

        function.Statements().Select(statement => statement.Kind).ShouldBe(
        [
            "PLpgSQL_stmt_block", "PLpgSQL_stmt_fors", "PLpgSQL_stmt_if", "PLpgSQL_stmt_assign",
            "PLpgSQL_stmt_return_next", "PLpgSQL_stmt_return"
        ]);
        function.Statements().Select(statement => statement.LineNo).ShouldBe([5, 6, 7, 8, 10, 12]);

        function.Queries().ShouldBe(
            ["0", "r.fooid > 100", "total := total + 1", "SELECT * FROM foo WHERE fooid > min_id"]);
    }

    [Fact]
    public void NodesExposeChildrenAndJson()
    {
        var function = Parser.ParsePlpgsqlFunctions(Sql).GetValueOrThrow()[0];
        var body = function.Body.ShouldNotBeNull();

        body.Kind.ShouldBe("PLpgSQL_stmt_block");
        body.ToString().ShouldBe("PLpgSQL_stmt_block");
        body.Children.Select(child => child.Kind).ShouldBe(["PLpgSQL_stmt_fors", "PLpgSQL_stmt_return"]);

        var loop = body.Children[0];
        loop.Json.GetProperty("lineno").GetInt32().ShouldBe(6);
        loop.Json.GetProperty("body").ValueKind.ShouldBe(JsonValueKind.Array);
        loop.Descendants().Select(node => node.Kind).ShouldContain("PLpgSQL_stmt_assign");
        loop.GetString("missing").ShouldBeNull();
        loop.GetString("lineno").ShouldBeNull();
    }

    [Fact]
    public void QueriesCanBeParsed()
    {
        var function = Parser.ParsePlpgsqlFunctions(Sql).GetValueOrThrow()[0];

        var tables = function.Queries()
            .Select(query => Parser.Parse(query))
            .Where(result => result.IsSuccess)
            .SelectMany(result => result.Value!.Descendants<RangeVar>())
            .Select(table => table.Relname);

        tables.ShouldBe(["foo"]);
    }

    [Fact]
    public void ReturnsOneFunctionPerCreateFunctionOrDoStatement()
    {
        const string sql = Sql + "\nCREATE FUNCTION s() RETURNS int LANGUAGE sql RETURN 1;\n"
                               + "DO $$ BEGIN PERFORM 1; END $$; SELECT 1";

        var functions = Parser.ParsePlpgsqlFunctions(sql).GetValueOrThrow();

        functions.Count.ShouldBe(3);
        // A function that is not PL/pgSQL has no body.
        functions[1].Body.ShouldBeNull();
        functions[1].Datums.ShouldBeEmpty();
        functions[1].Statements().ShouldBeEmpty();
        functions[1].Queries().ShouldBeEmpty();
        functions[2].Statements().Select(statement => statement.Kind)
            .ShouldBe(["PLpgSQL_stmt_block", "PLpgSQL_stmt_perform", "PLpgSQL_stmt_return"]);
        Parser.ParsePlpgsqlFunctions("SELECT 1").GetValueOrThrow().ShouldBeEmpty();
    }

    [Fact]
    public void HandlesTriggerFunctions()
    {
        // libpg_query 18.1.0 writes the TG_* variables of a trigger function as invalid JSON ("{}}").
        const string sql = """
            CREATE FUNCTION audit() RETURNS trigger AS $$
            BEGIN
              INSERT INTO log VALUES (TG_OP, '[{"a":1},{}},{}}]', NEW.id);
              RETURN NEW;
            END $$ LANGUAGE plpgsql;
            """;
        Should.Throw<JsonException>(() => JsonDocument.Parse(Parser.ParsePlpgsql(sql).GetValueOrThrow()));

        var function = Parser.ParsePlpgsqlFunctions(sql).GetValueOrThrow().ShouldHaveSingleItem();

        function.Datums.Select(datum => datum.GetString("refname")).ShouldContain("new");
        function.Statements().Select(statement => statement.Kind).ShouldContain("PLpgSQL_stmt_execsql");
        // The same character sequence inside a string literal is left alone.
        function.Queries().ShouldContain(query => query.Contains("""'[{"a":1},{}},{}}]'"""));
    }

    [Fact]
    public void ReadsEveryUpstreamSample()
    {
        var functions = Parser.ParsePlpgsqlFunctions(Utils.ReadFile("plpgsql_samples.sql")).GetValueOrThrow();

        functions.Count.ShouldBeGreaterThan(20);
        var nodes = functions.SelectMany(function => function.Root.Descendants()).ToList();
        nodes.ShouldAllBe(node => node.Kind.StartsWith("PLpgSQL_"));
        nodes.Select(node => node.Kind).Distinct().Count().ShouldBeGreaterThan(20);
        functions.SelectMany(function => function.Queries()).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ErrorsAndAsync()
    {
        var error = Parser.ParsePlpgsqlFunctions("CREATE FUNCTION f() RETURNS int AS $$ BEGIN RETURN 1 $$ LANGUAGE plpgsql").Error;
        error.ShouldNotBeNull().Message.ShouldNotBeNullOrEmpty();

        Should.Throw<ArgumentNullException>(() => Parser.ParsePlpgsqlFunctions(null!));
        (await Parser.ParsePlpgsqlFunctionsAsync(Sql)).GetValueOrThrow().Count.ShouldBe(1);
    }
}
