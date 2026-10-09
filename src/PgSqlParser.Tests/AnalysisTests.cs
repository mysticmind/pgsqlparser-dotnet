using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class AnalysisTests
{
    private static StatementInfo Classify(string query) => Parser.Classify(query).GetValueOrThrow().ShouldHaveSingleItem();

    [Theory]
    [InlineData("SELECT 1", StatementKind.Select, true)]
    [InlineData("VALUES (1)", StatementKind.Select, true)]
    [InlineData("TABLE t", StatementKind.Select, true)]
    [InlineData("INSERT INTO t SELECT 1", StatementKind.Insert, false)]
    [InlineData("UPDATE t SET a = 1", StatementKind.Update, false)]
    [InlineData("DELETE FROM t", StatementKind.Delete, false)]
    [InlineData("MERGE INTO t USING s ON true WHEN MATCHED THEN DELETE", StatementKind.Merge, false)]
    [InlineData("TRUNCATE t", StatementKind.Truncate, false)]
    [InlineData("COPY t TO STDOUT", StatementKind.Copy, true)]
    [InlineData("COPY t FROM STDIN", StatementKind.Copy, false)]
    [InlineData("CREATE TABLE t (a int)", StatementKind.Ddl, false)]
    [InlineData("CREATE INDEX i ON t (a)", StatementKind.Ddl, false)]
    [InlineData("ALTER TABLE t ADD COLUMN b int", StatementKind.Ddl, false)]
    [InlineData("DROP TABLE t", StatementKind.Ddl, false)]
    [InlineData("GRANT SELECT ON t TO x", StatementKind.Ddl, false)]
    [InlineData("COMMENT ON TABLE t IS 'x'", StatementKind.Ddl, false)]
    [InlineData("CREATE TABLE t AS SELECT 1", StatementKind.Ddl, false)]
    [InlineData("BEGIN", StatementKind.Transaction, true)]
    [InlineData("COMMIT", StatementKind.Transaction, true)]
    [InlineData("SET search_path = x", StatementKind.Session, true)]
    [InlineData("SHOW all", StatementKind.Session, true)]
    [InlineData("DECLARE c CURSOR FOR SELECT 1", StatementKind.Cursor, true)]
    [InlineData("DECLARE c CURSOR FOR SELECT 1 FROM t FOR UPDATE", StatementKind.Cursor, false)]
    [InlineData("FETCH 1 FROM c", StatementKind.Cursor, true)]
    [InlineData("PREPARE p AS SELECT 1", StatementKind.Prepared, true)]
    [InlineData("PREPARE p AS DELETE FROM t", StatementKind.Prepared, false)]
    [InlineData("EXECUTE p", StatementKind.Prepared, false)]
    [InlineData("DEALLOCATE p", StatementKind.Prepared, true)]
    [InlineData("CALL p()", StatementKind.Call, false)]
    [InlineData("DO $$ BEGIN END $$", StatementKind.Call, false)]
    [InlineData("VACUUM t", StatementKind.Maintenance, false)]
    [InlineData("ANALYZE t", StatementKind.Maintenance, false)]
    [InlineData("REFRESH MATERIALIZED VIEW m", StatementKind.Maintenance, false)]
    [InlineData("LOCK TABLE t", StatementKind.Lock, false)]
    public void KindAndReadOnly(string query, StatementKind kind, bool readOnly)
    {
        var info = Classify(query);

        info.Kind.ShouldBe(kind);
        info.IsReadOnly.ShouldBe(readOnly);
        // IsUtility agrees with libpg_query's own answer.
        info.IsUtility.ShouldBe(Parser.IsUtilityStmt(query).GetValueOrThrow()[0]);
    }

    [Fact]
    public void WritesHiddenInsideASelect()
    {
        var into = Classify("SELECT * INTO backup FROM t");
        into.HasSelectInto.ShouldBeTrue();
        into.IsReadOnly.ShouldBeFalse();

        var cte = Classify("WITH gone AS (DELETE FROM t RETURNING *) SELECT * FROM gone");
        cte.Kind.ShouldBe(StatementKind.Select);
        cte.HasDataModifyingCte.ShouldBeTrue();
        cte.IsReadOnly.ShouldBeFalse();

        var nested = Classify("SELECT * FROM (WITH x AS (INSERT INTO t VALUES (1) RETURNING a) SELECT a FROM x) s");
        nested.HasDataModifyingCte.ShouldBeTrue();

        var locking = Classify("SELECT * FROM t WHERE id IN (SELECT id FROM u FOR SHARE)");
        locking.HasLockingClause.ShouldBeTrue();
        locking.IsReadOnly.ShouldBeFalse();

        Classify("WITH x AS (SELECT 1) SELECT * FROM x").IsReadOnly.ShouldBeTrue();
    }

    [Theory]
    [InlineData("EXPLAIN DELETE FROM t", false, true)]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t", true, false)]
    [InlineData("EXPLAIN (ANALYZE) DELETE FROM t", true, false)]
    [InlineData("EXPLAIN (ANALYZE true, BUFFERS) DELETE FROM t", true, false)]
    [InlineData("EXPLAIN (ANALYZE false) DELETE FROM t", false, true)]
    [InlineData("EXPLAIN (ANALYZE off) DELETE FROM t", false, true)]
    [InlineData("EXPLAIN ANALYZE SELECT 1", true, true)]
    public void ExplainRunsItsStatementOnlyWithAnalyze(string query, bool executes, bool readOnly)
    {
        var info = Classify(query);

        info.Kind.ShouldBe(StatementKind.Explain);
        info.ExecutesInner.ShouldBe(executes);
        info.IsReadOnly.ShouldBe(readOnly);
        info.Inner.ShouldNotBeNull();
    }

    [Fact]
    public void InnerStatementsAndPrograms()
    {
        Classify("EXPLAIN SELECT 1").Inner.ShouldNotBeNull().Kind.ShouldBe(StatementKind.Select);
        Classify("CREATE TABLE t AS SELECT 1").Inner.ShouldNotBeNull().Node.ShouldBeOfType<SelectStmt>();
        Classify("COPY (SELECT 1) TO STDOUT").IsReadOnly.ShouldBeTrue();

        var program = Classify("COPY (SELECT 1) TO PROGRAM 'cat'");
        program.RunsProgram.ShouldBeTrue();
        program.IsReadOnly.ShouldBeFalse();

        // A write inside the wrapped statement belongs to Inner, not to the wrapper.
        var explain = Classify("EXPLAIN WITH d AS (DELETE FROM t RETURNING 1) SELECT * FROM d");
        explain.HasDataModifyingCte.ShouldBeFalse();
        explain.Inner.ShouldNotBeNull().HasDataModifyingCte.ShouldBeTrue();
    }

    [Fact]
    public async Task ClassifyScriptsAndErrors()
    {
        var infos = Parser.Classify("SELECT 1; DROP TABLE t; BEGIN").GetValueOrThrow();
        infos.Select(info => info.Kind).ShouldBe([StatementKind.Select, StatementKind.Ddl, StatementKind.Transaction]);

        var tree = Parser.Parse("SELECT 1; DELETE FROM t").GetValueOrThrow();
        tree.Classify().Select(info => info.IsReadOnly).ShouldBe([true, false]);
        tree.Stmts[1].Classify().Kind.ShouldBe(StatementKind.Delete);

        Parser.Classify("SELECT FROM WHERE").IsSuccess.ShouldBeFalse();
        Parser.Classify("").GetValueOrThrow().ShouldBeEmpty();
        (await Parser.ClassifyAsync("SELECT 1")).GetValueOrThrow().Single().Kind.ShouldBe(StatementKind.Select);
    }

    [Fact]
    public void TableReferencesWithRolesAliasesAndCtes()
    {
        const string query =
            "WITH recent AS (SELECT * FROM orders WHERE placed > now()) " +
            "SELECT c.name, lower(c.email) FROM public.customers c JOIN recent r ON r.customer_id = c.id " +
            "WHERE c.id IN (SELECT customer_id FROM vip)";

        var references = Parser.Parse(query).GetValueOrThrow().GetReferences();

        references.Tables.Select(table => (table.ToString(), table.Alias, table.Role, table.IsCte)).ShouldBe(
        [
            ("public.customers", "c", TableRole.Read, false),
            ("recent", "r", TableRole.Read, true),
            ("vip", null, TableRole.Read, false),
            ("orders", null, TableRole.Read, false)
        ]);
        references.Tables[0].Schema.ShouldBe("public");
        references.Tables[0].ReferenceName.ShouldBe("c");
        references.Tables[2].ReferenceName.ShouldBe("vip");
        references.Functions.Select(function => function.Name).ShouldBe(["lower", "now"]);
    }

    [Fact]
    public void TableRolesAcrossStatementKinds()
    {
        const string script =
            "INSERT INTO audit (who) SELECT name FROM users; " +
            "UPDATE accounts a SET balance = 0 FROM transfers x WHERE x.account_id = a.id; " +
            "DELETE FROM sessions s USING users u WHERE u.id = s.user_id; " +
            "TRUNCATE logs; COPY imports FROM STDIN; COPY exports TO STDOUT; " +
            "CREATE INDEX i ON items (sku); CREATE VIEW v AS SELECT id FROM items; " +
            "SELECT * INTO backup FROM items; ALTER TABLE items ADD COLUMN x int; LOCK TABLE items";

        var tables = Parser.Parse(script).GetValueOrThrow().GetReferences().Tables;

        tables.Select(table => (table.Name, table.Role, table.StatementIndex)).ShouldBe(
        [
            ("audit", TableRole.Write, 0), ("users", TableRole.Read, 0),
            ("accounts", TableRole.Write, 1), ("transfers", TableRole.Read, 1),
            ("sessions", TableRole.Write, 2), ("users", TableRole.Read, 2),
            ("logs", TableRole.Write, 3),
            ("imports", TableRole.Write, 4),
            ("exports", TableRole.Read, 5),
            ("items", TableRole.Ddl, 6),
            ("v", TableRole.Ddl, 7), ("items", TableRole.Read, 7),
            ("backup", TableRole.Ddl, 8), ("items", TableRole.Read, 8),
            ("items", TableRole.Ddl, 9),
            ("items", TableRole.Ddl, 10)
        ]);
    }

    [Fact]
    public void ColumnsAreMatchedToTablesWhereTheQuerySettlesIt()
    {
        const string query =
            "SELECT c.name, r.total, status, s.x FROM public.customers c " +
            "JOIN recent r ON r.customer_id = c.id, (SELECT x FROM other) s " +
            "WHERE c.id IN (SELECT customer_id FROM vip WHERE c.region = region)";

        var columns = Parser.Parse(query).GetValueOrThrow().GetReferences().Columns;

        columns.Select(column => (column.ToString(), column.Table?.ToString())).ShouldBe(
        [
            ("c.name", "public.customers"),
            ("r.total", "recent"),
            // No qualifier and several tables in scope: it takes the catalog to tell.
            ("status", null),
            // s is a subquery, not a table.
            ("s.x", null),
            ("r.customer_id", "recent"),
            ("c.id", "public.customers"),
            ("x", "other"),
            ("c.id", "public.customers"),
            // Inside the subquery: one table in scope, and c reaches the outer query.
            ("customer_id", "vip"),
            ("c.region", "public.customers"),
            ("region", "vip")
        ]);
        columns[0].Qualifier.ShouldBe("c");
        columns[0].Name.ShouldBe("name");
    }

    [Fact]
    public void ColumnsOfDmlStarsAndCtes()
    {
        var update = Parser.Parse("UPDATE accounts a SET balance = a.balance - x.amount FROM transfers x WHERE x.id = a.id RETURNING id")
            .GetValueOrThrow().GetReferences();
        update.Columns.Select(column => (column.ToString(), column.Table?.Name)).ShouldBe(
            [("a.balance", "accounts"), ("x.amount", "transfers"), ("x.id", "transfers"), ("a.id", "accounts"), ("id", null)]);

        var star = Parser.Parse("SELECT *, t.* FROM t").GetValueOrThrow().GetReferences();
        star.Columns.Select(column => (column.Qualifier, column.Name, column.Table?.Name)).ShouldBe([(null, "*", "t"), ("t", "*", "t")]);

        // A column of a CTE is not attributed to a table.
        var cte = Parser.Parse("WITH r AS (SELECT id FROM base) SELECT r.id, id FROM r").GetValueOrThrow().GetReferences();
        cte.Columns.Select(column => (column.ToString(), column.Table?.Name)).ShouldBe([("r.id", null), ("id", null), ("id", "base")]);
    }

    [Fact]
    public void ReferencesFromANodeAndWithNonAsciiNames()
    {
        var tree = Parser.Parse("SELECT d.\"é\" FROM \"données\" d WHERE d.\"😀\" = 1").GetValueOrThrow();

        var references = tree.Stmts[0].Stmt.SelectStmt.GetReferences();

        references.Tables.Single().Name.ShouldBe("données");
        references.Tables.Single().StatementIndex.ShouldBeNull();
        references.Columns.Select(column => column.Name).ShouldBe(["é", "😀"]);
        references.Columns.ShouldAllBe(column => column.Table != null && column.Table.Name == "données");
    }

    [Fact]
    public void ReferencesFromASingleStatementNode()
    {
        var tree = Parser.Parse("WITH r AS (SELECT 1) INSERT INTO target SELECT x FROM r; CREATE INDEX i ON items (sku)").GetValueOrThrow();

        // Starting from the statement node itself, with nothing above it.
        var insert = tree.Stmts[0].Stmt.InsertStmt.GetReferences();
        insert.Tables.Select(table => (table.Name, table.Role, table.IsCte)).ShouldBe(
            [("target", TableRole.Write, false), ("r", TableRole.Read, true)]);

        tree.Stmts[1].Stmt.IndexStmt.GetReferences().Tables.Single().Role.ShouldBe(TableRole.Ddl);
        // And from the Node wrapper or the RawStmt.
        tree.Stmts[1].Stmt.GetReferences().Tables.Single().Role.ShouldBe(TableRole.Ddl);
        tree.Stmts[1].GetReferences().Tables.Single().Role.ShouldBe(TableRole.Ddl);
    }

    [Fact]
    public void FormatKeepsCommentsAndSeparatesStatements()
    {
        const string query = "select a, b from t -- pick\n where x = 1; -- second\ndelete from t where a = 1; -- done";

        Parser.Format(query).GetValueOrThrow().ShouldBe(
            "SELECT a, b\nFROM t\nWHERE\n    -- pick\n    x = 1;\n\n-- second\nDELETE FROM t\nWHERE a = 1;\n\n-- done");
        Parser.Format(query, new FormatOptions { KeepComments = false }).GetValueOrThrow().ShouldBe(
            "SELECT a, b\nFROM t\nWHERE x = 1;\n\nDELETE FROM t\nWHERE a = 1;");
    }

    [Fact]
    public async Task FormatOptionsAndEdgeCases()
    {
        Parser.Format("select 1").GetValueOrThrow().ShouldBe("SELECT 1");
        Parser.Format("select 1;").GetValueOrThrow().ShouldBe("SELECT 1");
        Parser.Format("select 1", new FormatOptions { TrailingNewline = true }).GetValueOrThrow().ShouldBe("SELECT 1\n");
        Parser.Format("select a from t where x = 1 and y = 2", new FormatOptions { IndentSize = 2 }).GetValueOrThrow()
            .ShouldBe("SELECT a\nFROM t\nWHERE\n  x = 1\n  AND y = 2");
        Parser.Format("-- only a comment").GetValueOrThrow().ShouldBe("-- only a comment");
        Parser.Format("").GetValueOrThrow().ShouldBe("");
        Parser.Format("select '😀' as \"é\" from \"données\"").GetValueOrThrow().ShouldBe("SELECT '😀' AS \"é\"\nFROM \"données\"");
        Parser.Format("select from where").IsSuccess.ShouldBeFalse();
        (await Parser.FormatAsync("select 1")).GetValueOrThrow().ShouldBe("SELECT 1");

        // Formatting what was formatted changes nothing.
        var once = Parser.Format("select a,b from t where x in (select 1) order by a").GetValueOrThrow();
        Parser.Format(once).GetValueOrThrow().ShouldBe(once);
    }

    [Fact]
    public void ErrorLineColumnAndRendering()
    {
        const string query = "SELECT a,\n\t'😀' AS e,\n\tb FROM WHERE x";
        var error = Parser.Parse(query).Error.ShouldNotBeNull();

        error.GetLineAndColumn(query).ShouldBe((3, 9));
        error.Format(query).ShouldBe(
            "ERROR:  syntax error at or near \"WHERE\"\n" +
            "LINE 3:  b FROM WHERE x\n" +
            "                ^");
    }

    [Fact]
    public void ErrorRenderingEdgeCases()
    {
        const string single = "SELECT FROM WHERE";
        var error = Parser.Parse(single).Error.ShouldNotBeNull();
        error.GetLineAndColumn(single).ShouldBe((1, 13));
        error.Format(single).ShouldBe("ERROR:  syntax error at or near \"WHERE\"\nLINE 1: SELECT FROM WHERE\n                    ^");

        // At the end of the input, and with Windows line endings.
        const string atEnd = "SELECT 1\r\nFROM";
        var endError = Parser.Parse(atEnd).Error.ShouldNotBeNull();
        endError.GetLineAndColumn(atEnd).ShouldBe((2, 5));
        endError.Format(atEnd).ShouldBe("ERROR:  syntax error at end of input\nLINE 2: FROM\n            ^");

        var noPosition = new Error("something failed", null, null, 0, 0, null);
        noPosition.GetLineAndColumn(single).ShouldBeNull();
        noPosition.Format(single).ShouldBe("ERROR:  something failed");
    }
}
