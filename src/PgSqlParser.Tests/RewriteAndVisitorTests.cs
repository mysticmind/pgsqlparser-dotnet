using Google.Protobuf;
using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class RewriteAndVisitorTests
{
    private static ParseResult Parse(string query) => Parser.Parse(query).GetValueOrThrow();

    private static string Sql(IMessage node) => node.Deparse().GetValueOrThrow();

    [Theory]
    [InlineData("SELECT * FROM t", "SELECT * FROM t WHERE tenant_id = $1")]
    [InlineData("SELECT * FROM t WHERE a = 1", "SELECT * FROM t WHERE a = 1 AND tenant_id = $1")]
    [InlineData("SELECT * FROM t WHERE a = 1 AND b = 2", "SELECT * FROM t WHERE a = 1 AND b = 2 AND tenant_id = $1")]
    [InlineData("SELECT * FROM t WHERE a = 1 OR b = 2", "SELECT * FROM t WHERE (a = 1 OR b = 2) AND tenant_id = $1")]
    [InlineData("UPDATE t SET a = 1", "UPDATE t SET a = 1 WHERE tenant_id = $1")]
    [InlineData("DELETE FROM t WHERE a = 1", "DELETE FROM t WHERE a = 1 AND tenant_id = $1")]
    [InlineData("SELECT * FROM t ORDER BY a LIMIT 5", "SELECT * FROM t WHERE tenant_id = $1 ORDER BY a LIMIT 5")]
    public void AddWhere(string before, string after)
    {
        var tree = Parse(before);

        tree.AddWhere(Ast.Eq(Ast.Column("tenant_id"), Ast.Param(1)));

        Sql(tree).ShouldBe(after);
        // The result is a tree the parser itself would build.
        tree.EqualsIgnoringLocations(Parse(after)).ShouldBeTrue();
    }

    [Fact]
    public void AddWhereWorksOnNodesAndRejectsWhatHasNoWhere()
    {
        var tree = Parse("SELECT * FROM t WHERE id IN (SELECT id FROM u)");
        var inner = tree.Descendants<SelectStmt>().Last();

        inner.AddWhere(Ast.Expression("active"));
        tree.Stmts[0].AddWhere(Ast.Expression("x = 1"));

        Sql(tree).ShouldBe("SELECT * FROM t WHERE id IN (SELECT id FROM u WHERE active) AND x = 1");

        Should.Throw<NotSupportedException>(() => Parse("SELECT 1 UNION SELECT 2").AddWhere(Ast.Expression("true")))
            .Message.ShouldBe("A WHERE condition cannot be added to a UNION, INTERSECT or EXCEPT.");
        Should.Throw<NotSupportedException>(() => Parse("INSERT INTO t VALUES (1)").AddWhere(Ast.Expression("true")))
            .Message.ShouldBe("A WHERE condition cannot be added to a InsertStmt.");
        Should.Throw<NotSupportedException>(() => Parse("VALUES (1)").AddWhere(Ast.Expression("true")));
        Should.Throw<NotSupportedException>(() => Parse("SELECT 1; SELECT 2").AddWhere(Ast.Expression("true")));
    }

    [Theory]
    [InlineData("SELECT * FROM t", "SELECT * FROM t LIMIT 100")]
    [InlineData("SELECT * FROM t LIMIT 10", "SELECT * FROM t LIMIT 10")]
    [InlineData("SELECT * FROM t LIMIT 100", "SELECT * FROM t LIMIT 100")]
    [InlineData("SELECT * FROM t LIMIT 5000", "SELECT * FROM t LIMIT 100")]
    [InlineData("SELECT * FROM t LIMIT ALL", "SELECT * FROM t LIMIT 100")]
    [InlineData("SELECT * FROM t LIMIT $1", "SELECT * FROM t LIMIT LEAST($1, 100)")]
    [InlineData("SELECT * FROM t ORDER BY a LIMIT 500 OFFSET 20", "SELECT * FROM t ORDER BY a LIMIT 100 OFFSET 20")]
    [InlineData("SELECT 1 UNION SELECT 2", "SELECT 1 UNION SELECT 2 LIMIT 100")]
    public void CapLimit(string before, string after)
    {
        var tree = Parse(before);

        tree.CapLimit(100);

        Sql(tree).ShouldBe(after);
        Parse(Sql(tree)).EqualsIgnoringLocations(Parse(after)).ShouldBeTrue();
    }

    [Fact]
    public void SetLimitOffsetAndCount()
    {
        var tree = Parse("SELECT id FROM t WHERE a = 1 ORDER BY id LIMIT 3");

        tree.SetLimit(50);
        tree.SetOffset(100);
        Sql(tree).ShouldBe("SELECT id FROM t WHERE a = 1 ORDER BY id LIMIT 50 OFFSET 100");

        var count = tree.ToCount();
        Sql(count).ShouldBe("SELECT count(*) FROM (SELECT id FROM t WHERE a = 1 ORDER BY id LIMIT 50 OFFSET 100) q");
        Sql(Parse("SELECT 1").ToCount("rows")).ShouldBe("SELECT count(*) FROM (SELECT 1) rows");
        // The original is not changed by ToCount.
        Sql(tree).ShouldBe("SELECT id FROM t WHERE a = 1 ORDER BY id LIMIT 50 OFFSET 100");

        Should.Throw<NotSupportedException>(() => Parse("DELETE FROM t").SetLimit(1))
            .Message.ShouldBe("A LIMIT applies to a SELECT, not to a DeleteStmt.");
    }

    [Fact]
    public void QualifyAndRenameTables()
    {
        var tree = Parse("WITH r AS (SELECT * FROM orders) SELECT * FROM customers c JOIN r ON true JOIN other.things t ON true");

        tree.QualifyTables("app").ShouldBe(2);
        Sql(tree).ShouldBe("WITH r AS (SELECT * FROM app.orders) SELECT * FROM app.customers c JOIN r ON true JOIN other.things t ON true");

        tree.RenameSchema("app", "tenant_1").ShouldBe(2);
        tree.RenameTable("things", "stuff", schema: "other").ShouldBe(1);
        tree.RenameTable("r", "renamed").ShouldBe(0, "r is a CTE here, not a table");
        Sql(tree).ShouldBe("WITH r AS (SELECT * FROM tenant_1.orders) SELECT * FROM tenant_1.customers c JOIN r ON true JOIN other.stuff t ON true");

        var dml = Parse("UPDATE accounts SET a = 1; INSERT INTO accounts SELECT * FROM staging");
        dml.RenameTable("accounts", "accounts_v2").ShouldBe(2);
        Sql(dml).ShouldBe("UPDATE accounts_v2 SET a = 1; INSERT INTO accounts_v2 SELECT * FROM staging");
    }

    [Theory]
    [InlineData("CREATE TABLE s.t (a int)", "DROP TABLE s.t")]
    [InlineData("CREATE TABLE \"My Table\" (a int)", "DROP TABLE \"My Table\"")]
    [InlineData("CREATE UNIQUE INDEX idx ON s.t (a)", "DROP INDEX s.idx")]
    [InlineData("CREATE VIEW v AS SELECT 1", "DROP VIEW v")]
    [InlineData("CREATE MATERIALIZED VIEW mv AS SELECT 1", "DROP MATERIALIZED VIEW mv")]
    [InlineData("CREATE TABLE copy AS SELECT 1", "DROP TABLE copy")]
    [InlineData("CREATE SEQUENCE s.seq", "DROP SEQUENCE s.seq")]
    [InlineData("CREATE SCHEMA app", "DROP SCHEMA app")]
    [InlineData("CREATE EXTENSION hstore", "DROP EXTENSION hstore")]
    [InlineData("CREATE TYPE mood AS ENUM ('a', 'b')", "DROP TYPE mood")]
    [InlineData("CREATE TYPE pair AS (a int, b int)", "DROP TYPE pair")]
    [InlineData("CREATE DOMAIN positive AS int CHECK (VALUE > 0)", "DROP DOMAIN positive")]
    [InlineData("CREATE FUNCTION s.f(a int, b text, OUT c int) RETURNS int AS 'select 1' LANGUAGE sql", "DROP FUNCTION s.f(int, text)")]
    [InlineData("CREATE PROCEDURE p(VARIADIC a int[]) AS 'select 1' LANGUAGE sql", "DROP PROCEDURE p(VARIADIC int[])")]
    [InlineData("CREATE TRIGGER trg BEFORE INSERT ON s.t FOR EACH ROW EXECUTE FUNCTION f()", "DROP TRIGGER trg ON s.t")]
    [InlineData("CREATE POLICY p ON t USING (true)", "DROP POLICY p ON t")]
    public void ToDropStatement(string create, string drop)
    {
        var tree = Parse(create);

        Sql(tree.ToDropStatement()).ShouldBe(drop);
        // IF EXISTS goes after the object kind, which is two words for a materialized view.
        var kindEnd = drop.StartsWith("DROP MATERIALIZED VIEW ", StringComparison.Ordinal) ? 23 : drop.IndexOf(' ', 5) + 1;
        Sql(tree.ToDropStatement(ifExists: true, cascade: true)).ShouldBe(drop.Insert(kindEnd, "IF EXISTS ") + " CASCADE");
    }

    [Fact]
    public void ToDropStatementRejectsWhatItCannotUndo()
    {
        Should.Throw<NotSupportedException>(() => Parse("SELECT 1").ToDropStatement())
            .Message.ShouldBe("There is no DROP statement for a SelectStmt.");
        // An index without a name gets one from PostgreSQL, which the statement does not say.
        Should.Throw<NotSupportedException>(() => Parse("CREATE INDEX ON t (a)").ToDropStatement());
    }

    [Theory]
    [InlineData("CREATE TABLE t (a int)", "CREATE TABLE IF NOT EXISTS t (a int)")]
    [InlineData("CREATE INDEX i ON t (a)", "CREATE INDEX IF NOT EXISTS i ON t USING btree (a)")]
    [InlineData("CREATE SEQUENCE s", "CREATE SEQUENCE IF NOT EXISTS s")]
    [InlineData("CREATE SCHEMA app", "CREATE SCHEMA IF NOT EXISTS app")]
    [InlineData("CREATE EXTENSION hstore", "CREATE EXTENSION IF NOT EXISTS hstore")]
    public void EnsureIfNotExists(string before, string after)
    {
        var tree = Parse(before);

        tree.EnsureIfNotExists().ShouldBeTrue();

        Sql(tree).ShouldBe(after);
    }

    [Fact]
    public void EnsureOrReplaceAndUnsupportedStatements()
    {
        var view = Parse("CREATE VIEW v AS SELECT 1");
        view.EnsureOrReplace().ShouldBeTrue();
        Sql(view).ShouldBe("CREATE OR REPLACE VIEW v AS SELECT 1");

        var function = Parse("CREATE FUNCTION f() RETURNS int AS 'select 1' LANGUAGE sql");
        function.EnsureOrReplace().ShouldBeTrue();
        Sql(function).ShouldStartWith("CREATE OR REPLACE FUNCTION f()");

        Parse("CREATE TABLE t (a int)").EnsureOrReplace().ShouldBeFalse();
        Parse("CREATE VIEW v AS SELECT 1").EnsureIfNotExists().ShouldBeFalse();
        Parse("CREATE INDEX ON t (a)").EnsureIfNotExists().ShouldBeFalse("IF NOT EXISTS needs an index name");
    }

    [Fact]
    public void TypedVisitorDispatchesByNodeType()
    {
        var tree = Parse("SELECT lower(a) FROM t1 JOIN t2 ON true WHERE b IN (SELECT c FROM t3)");
        var tables = new List<string>();
        var functions = new List<string>();

        tree.Walk(new NodeVisitor()
            .On<RangeVar>((table, _) => tables.Add(table.Relname))
            .On<FuncCall>((call, visit) => functions.Add($"{call.Funcname[0].String.Sval} in {visit.FieldName}")));

        tables.ShouldBe(["t1", "t2", "t3"]);
        functions.ShouldBe(["lower in Val"]);
    }

    [Fact]
    public void TypedVisitorCanSkipAndStop()
    {
        var tree = Parse("SELECT a FROM t1 WHERE b IN (SELECT c FROM t2) AND d IN (SELECT e FROM t3)");

        var outside = new List<string>();
        tree.Walk(new NodeVisitor()
            .On<SubLink>((_, _) => WalkAction.SkipChildren)
            .On<RangeVar>((table, _) => outside.Add(table.Relname)));
        outside.ShouldBe(["t1"]);

        var firstTwo = new List<string>();
        tree.Walk(new NodeVisitor().On<RangeVar>((table, _) =>
        {
            firstTwo.Add(table.Relname);
            return firstTwo.Count == 2 ? WalkAction.Stop : WalkAction.Continue;
        }));
        firstTwo.ShouldBe(["t1", "t2"]);
    }

    [Fact]
    public void SeveralVisitorsShareOneWalkIndependently()
    {
        var tree = Parse("SELECT a FROM t1 WHERE b IN (SELECT c FROM t2) AND d IN (SELECT e FROM t3)");
        var all = new List<string>();
        var outside = new List<string>();
        var first = new List<string>();
        var subqueries = 0;

        tree.Walk(
            new NodeVisitor().On<RangeVar>((table, _) => all.Add(table.Relname)),
            new NodeVisitor()
                .On<SubLink>((_, _) => WalkAction.SkipChildren)
                .On<RangeVar>((table, _) => outside.Add(table.Relname)),
            new NodeVisitor().On<RangeVar>((table, _) =>
            {
                first.Add(table.Relname);
                return WalkAction.Stop;
            }),
            new NodeVisitor().On<SubLink>((_, _) => subqueries++));

        // One visitor skipping or stopping does not affect the others.
        all.ShouldBe(["t1", "t2", "t3"]);
        outside.ShouldBe(["t1"]);
        first.ShouldBe(["t1"]);
        subqueries.ShouldBe(2);
    }

    [Fact]
    public void SeveralHandlersForOneTypeAndNoVisitors()
    {
        var tree = Parse("SELECT a FROM t1, t2");
        var log = new List<string>();

        tree.Walk(new NodeVisitor()
            .On<RangeVar>((table, _) => log.Add($"first {table.Relname}"))
            .On<RangeVar>((table, _) => log.Add($"second {table.Relname}")));

        log.ShouldBe(["first t1", "second t1", "first t2", "second t2"]);
        tree.Walk(Array.Empty<NodeVisitor>());
        Should.Throw<ArgumentNullException>(() => new NodeVisitor().On<RangeVar>((Action<RangeVar, NodeVisit>)null!));
    }
}
