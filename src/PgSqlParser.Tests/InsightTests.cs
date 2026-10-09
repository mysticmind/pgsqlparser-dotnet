using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class InsightTests
{
    private static ParseResult Parse(string query) => Parser.Parse(query).GetValueOrThrow();

    private static string Locks(string query) =>
        string.Join(", ", Parse(query).GetLocks().Select(tableLock => tableLock.ToString()));

    [Theory]
    [InlineData("SELECT * FROM a JOIN b ON true", "a: AccessShare, b: AccessShare")]
    [InlineData("SELECT * FROM a, b FOR UPDATE OF a", "a: RowShare, b: AccessShare")]
    [InlineData("SELECT * FROM a FOR SHARE", "a: RowShare")]
    [InlineData("SELECT * FROM a x FOR UPDATE OF x", "a: RowShare")]
    [InlineData("WITH c AS (SELECT * FROM x) INSERT INTO t SELECT * FROM c JOIN y ON true", "t: RowExclusive, y: AccessShare, x: AccessShare")]
    [InlineData("UPDATE t SET a = 1 FROM u WHERE u.id = t.id", "t: RowExclusive, u: AccessShare")]
    [InlineData("DELETE FROM s.t", "s.t: RowExclusive")]
    [InlineData("MERGE INTO t USING s ON true WHEN MATCHED THEN DELETE", "t: RowExclusive, s: AccessShare")]
    [InlineData("COPY t FROM STDIN", "t: RowExclusive")]
    [InlineData("COPY t TO STDOUT", "t: AccessShare")]
    [InlineData("EXPLAIN SELECT * FROM a", "a: AccessShare")]
    [InlineData("TRUNCATE a, s.b", "a: AccessExclusive, s.b: AccessExclusive")]
    [InlineData("LOCK TABLE a", "a: AccessExclusive")]
    [InlineData("LOCK TABLE a IN SHARE MODE", "a: Share")]
    [InlineData("LOCK TABLE a IN ROW EXCLUSIVE MODE", "a: RowExclusive")]
    [InlineData("CREATE INDEX i ON t (a)", "t: Share")]
    [InlineData("CREATE INDEX CONCURRENTLY i ON t (a)", "t: ShareUpdateExclusive")]
    [InlineData("CREATE TRIGGER g BEFORE INSERT ON t FOR EACH ROW EXECUTE FUNCTION f()", "t: ShareRowExclusive")]
    [InlineData("CREATE STATISTICS st ON a, b FROM t", "t: ShareUpdateExclusive")]
    [InlineData("CREATE TABLE n (id int REFERENCES parent, x int, FOREIGN KEY (x) REFERENCES s.other)", "parent: ShareRowExclusive, s.other: ShareRowExclusive")]
    [InlineData("DROP TABLE a, s.b", "a: AccessExclusive, s.b: AccessExclusive")]
    [InlineData("VACUUM t", "t: ShareUpdateExclusive")]
    [InlineData("VACUUM FULL t", "t: AccessExclusive")]
    [InlineData("VACUUM (FULL false, ANALYZE) t", "t: ShareUpdateExclusive")]
    [InlineData("ANALYZE t", "t: ShareUpdateExclusive")]
    [InlineData("CLUSTER t USING i", "t: AccessExclusive")]
    [InlineData("REINDEX TABLE t", "t: Share")]
    [InlineData("REINDEX TABLE CONCURRENTLY t", "t: ShareUpdateExclusive")]
    [InlineData("REFRESH MATERIALIZED VIEW m", "m: AccessExclusive")]
    [InlineData("REFRESH MATERIALIZED VIEW CONCURRENTLY m", "m: Exclusive")]
    [InlineData("COMMENT ON TABLE s.t IS 'x'", "s.t: ShareUpdateExclusive")]
    [InlineData("COMMENT ON COLUMN s.t.c IS 'x'", "s.t: ShareUpdateExclusive")]
    // Not covered: the statement does not name the table, or it creates what it names.
    [InlineData("DROP INDEX i", "")]
    [InlineData("REINDEX INDEX i", "")]
    [InlineData("ALTER INDEX i RENAME TO j", "")]
    [InlineData("SELECT * INTO n FROM a", "a: AccessShare")]
    [InlineData("SELECT 1", "")]
    public void LocksPerStatement(string query, string expected)
    {
        Locks(query).ShouldBe(expected);
    }

    // These follow AlterTableGetLockLevel in PostgreSQL 18's tablecmds.c.
    [Theory]
    [InlineData("ALTER TABLE t ADD COLUMN c int", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t DROP COLUMN c", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t ALTER COLUMN a TYPE bigint", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t ALTER COLUMN a SET DEFAULT 1", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t ALTER COLUMN a SET NOT NULL", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t ADD PRIMARY KEY (id)", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT c CHECK (a > 0) NOT VALID", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (a) REFERENCES p (id)", "p: ShareRowExclusive, t: ShareRowExclusive")]
    [InlineData("ALTER TABLE t VALIDATE CONSTRAINT c", "t: ShareUpdateExclusive")]
    [InlineData("ALTER TABLE t ALTER COLUMN a SET STATISTICS 100", "t: ShareUpdateExclusive")]
    [InlineData("ALTER TABLE t SET (fillfactor = 70)", "t: ShareUpdateExclusive")]
    [InlineData("ALTER TABLE t SET (autovacuum_enabled = false)", "t: ShareUpdateExclusive")]
    [InlineData("ALTER TABLE t SET (fillfactor = 70, user_catalog_table = true)", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t RESET (fillfactor)", "t: ShareUpdateExclusive")]
    [InlineData("ALTER TABLE t DISABLE TRIGGER ALL", "t: ShareRowExclusive")]
    [InlineData("ALTER TABLE t ENABLE TRIGGER trg", "t: ShareRowExclusive")]
    [InlineData("ALTER TABLE t CLUSTER ON i", "t: ShareUpdateExclusive")]
    [InlineData("ALTER TABLE t ATTACH PARTITION p FOR VALUES IN (1)", "t: ShareUpdateExclusive")]
    [InlineData("ALTER TABLE t DETACH PARTITION p", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t DETACH PARTITION p CONCURRENTLY", "t: ShareUpdateExclusive")]
    [InlineData("ALTER TABLE t ENABLE ROW LEVEL SECURITY", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t SET LOGGED", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t RENAME COLUMN a TO b", "t: AccessExclusive")]
    [InlineData("ALTER TABLE t RENAME TO u", "t: AccessExclusive")]
    // The strongest sub-command decides.
    [InlineData("ALTER TABLE t VALIDATE CONSTRAINT c, ALTER COLUMN a SET STATISTICS 10", "t: ShareUpdateExclusive")]
    [InlineData("ALTER TABLE t VALIDATE CONSTRAINT c, ADD COLUMN x int", "t: AccessExclusive")]
    public void AlterTableLocks(string query, string expected)
    {
        Locks(query).ShouldBe(expected);
    }

    [Fact]
    public void LockModesAndConflicts()
    {
        var locks = Parse("SELECT 1 FROM a; CREATE INDEX i ON a (x); ALTER TABLE a ADD COLUMN y int").GetLocks();

        locks.Select(tableLock => (tableLock.Table, tableLock.Mode, tableLock.StatementIndex)).ShouldBe(
            [("a", LockMode.AccessShare, 0), ("a", LockMode.Share, 1), ("a", LockMode.AccessExclusive, 2)]);

        // CREATE INDEX blocks writes but not reads; ALTER TABLE ADD COLUMN blocks both.
        LockMode.Share.BlocksWrites().ShouldBeTrue();
        LockMode.Share.BlocksReads().ShouldBeFalse();
        LockMode.ShareUpdateExclusive.BlocksWrites().ShouldBeFalse();
        LockMode.AccessExclusive.BlocksReads().ShouldBeTrue();

        // The conflict table is symmetric and AccessShare only conflicts with AccessExclusive.
        foreach (var a in Enum.GetValues<LockMode>())
        {
            foreach (var b in Enum.GetValues<LockMode>())
                a.ConflictsWith(b).ShouldBe(b.ConflictsWith(a));

            LockMode.AccessShare.ConflictsWith(a).ShouldBe(a == LockMode.AccessExclusive);
            LockMode.AccessExclusive.ConflictsWith(a).ShouldBeTrue();
        }

        LockMode.ShareUpdateExclusive.ConflictsWith(LockMode.ShareUpdateExclusive).ShouldBeTrue();
        LockMode.Share.ConflictsWith(LockMode.Share).ShouldBeFalse();
        LockMode.RowExclusive.ConflictsWith(LockMode.RowExclusive).ShouldBeFalse();
        Parse("VACUUM t").Stmts[0].GetLocks().Single().StatementIndex.ShouldBeNull();
    }

    [Fact]
    public void ColumnsKnowTheirClause()
    {
        const string query =
            "SELECT c.name, count(*) FROM customers c JOIN orders o ON o.customer_id = c.id " +
            "WHERE c.region = 'eu' AND o.total > 10 GROUP BY c.name HAVING count(o.id) > 1 ORDER BY c.name; " +
            "UPDATE t SET a = b + 1 WHERE id = 5 RETURNING a; DELETE FROM t USING u WHERE u.x = t.x; " +
            "MERGE INTO t USING s ON s.id = t.id WHEN MATCHED THEN DELETE";

        var columns = Parse(query).GetReferences().Columns;

        columns.Select(column => (column.ToString(), column.Clause)).ShouldBe(
        [
            ("c.name", QueryClause.SelectList),
            ("o.customer_id", QueryClause.JoinCondition), ("c.id", QueryClause.JoinCondition),
            ("c.region", QueryClause.Where), ("o.total", QueryClause.Where),
            ("c.name", QueryClause.GroupBy), ("o.id", QueryClause.Having), ("c.name", QueryClause.OrderBy),
            ("b", QueryClause.Set), ("id", QueryClause.Where), ("a", QueryClause.Returning),
            ("u.x", QueryClause.Where), ("t.x", QueryClause.Where),
            ("s.id", QueryClause.JoinCondition), ("t.id", QueryClause.JoinCondition)
        ]);

        // The columns that filter rows, per table: the input for index advice.
        var filters = columns
            .Where(column => column is { Clause: QueryClause.Where or QueryClause.JoinCondition, Table: not null, StatementIndex: 0 })
            .Select(column => $"{column.Table!.Name}.{column.Name}");
        filters.ShouldBe(["orders.customer_id", "customers.id", "customers.region", "orders.total"]);
    }

    [Fact]
    public void ColumnClauseBelongsToTheNearestStatement()
    {
        var columns = Parse("SELECT a FROM t WHERE b IN (SELECT c FROM u WHERE d = 1 ORDER BY e)").GetReferences().Columns;

        columns.Select(column => (column.Name, column.Clause)).ShouldBe(
        [
            ("a", QueryClause.SelectList), ("b", QueryClause.Where),
            ("c", QueryClause.SelectList), ("d", QueryClause.Where), ("e", QueryClause.OrderBy)
        ]);
    }

    [Fact]
    public void OutputColumnsAreNamedAsPostgresNamesThem()
    {
        const string query =
            "SELECT a, t.b, lower(c), d AS x, 1, 'x'::text, e::int, count(*), CASE WHEN a THEN b END, " +
            "CASE WHEN a THEN 1 ELSE f END, coalesce(a, b), greatest(1, 2), (SELECT g FROM u), EXISTS (SELECT 1), " +
            "ARRAY[1], ROW(1, 2), current_date, a.b.c, nullif(a, b), a + 1, (r).field, a COLLATE \"C\" FROM t";

        var columns = Parse(query).Stmts[0].Stmt.SelectStmt.GetOutputColumns();

        columns.Select(column => column.Name).ShouldBe(
        [
            "a", "b", "lower", "x", "?column?", "text", "e", "count", "case", "f", "coalesce", "greatest", "g", "exists",
            "array", "row", "current_date", "c", "nullif", "?column?", "field", "a"
        ]);
        columns.ShouldAllBe(column => !column.IsStar);
        columns[3].Expression.ShouldBeOfType<ColumnRef>();
        QueryShape.UnnamedColumn.ShouldBe("?column?");
    }

    [Fact]
    public void OutputColumnsOfStarsSetOperationsAndValues()
    {
        var star = Parse("SELECT *, t.*, a FROM t").Stmts[0].Stmt.SelectStmt.GetOutputColumns();
        star.Select(column => (column.Name, column.IsStar)).ShouldBe([(null, true), (null, true), ("a", false)]);

        Parse("SELECT a, b FROM t UNION SELECT c, d FROM u UNION ALL SELECT e, f FROM v").Stmts[0].Stmt.SelectStmt
            .GetOutputColumns().Select(column => column.Name).ShouldBe(["a", "b"]);

        Parse("VALUES (1, 'x'), (2, 'y')").Stmts[0].Stmt.SelectStmt
            .GetOutputColumns().Select(column => column.Name).ShouldBe(["column1", "column2"]);
    }

    [Fact]
    public void TokenizeClassifiesEveryToken()
    {
        const string query = "SELECT a, 'x' AS \"B\", 1.5 + $1 FROM s.t -- note\n WHERE a::int >= 2 /* c */ AND b <> 'é😀';";

        var tokens = Parser.Tokenize(query).GetValueOrThrow();

        tokens.Select(token => (query[token.Start..token.End], token.Kind)).ShouldBe(
        [
            ("SELECT", SqlTokenKind.Keyword), ("a", SqlTokenKind.Identifier), (",", SqlTokenKind.Punctuation),
            ("'x'", SqlTokenKind.StringLiteral), ("AS", SqlTokenKind.Keyword), ("\"B\"", SqlTokenKind.Identifier),
            (",", SqlTokenKind.Punctuation), ("1.5", SqlTokenKind.NumericLiteral), ("+", SqlTokenKind.Operator),
            ("$1", SqlTokenKind.Parameter), ("FROM", SqlTokenKind.Keyword), ("s", SqlTokenKind.Identifier),
            (".", SqlTokenKind.Punctuation), ("t", SqlTokenKind.Identifier), ("-- note", SqlTokenKind.Comment),
            ("WHERE", SqlTokenKind.Keyword), ("a", SqlTokenKind.Identifier), ("::", SqlTokenKind.Operator),
            ("int", SqlTokenKind.Keyword), (">=", SqlTokenKind.Operator), ("2", SqlTokenKind.NumericLiteral),
            ("/* c */", SqlTokenKind.Comment), ("AND", SqlTokenKind.Keyword), ("b", SqlTokenKind.Identifier),
            ("<>", SqlTokenKind.Operator), ("'é😀'", SqlTokenKind.StringLiteral), (";", SqlTokenKind.Punctuation)
        ]);
        tokens[0].KeywordKind.ShouldBe(KeywordKind.ReservedKeyword);
        tokens[0].Token.ShouldBe(Token.Select);
    }

    [Fact]
    public async Task TokenizeEdgeCases()
    {
        Parser.Tokenize("").GetValueOrThrow().ShouldBeEmpty();
        Parser.Tokenize("SELECT 'unterminated").IsSuccess.ShouldBeFalse();
        // Tokens are available for SQL that does not parse.
        Parser.Tokenize("SELECT FROM WHERE").GetValueOrThrow().Select(token => token.Kind)
            .ShouldBe([SqlTokenKind.Keyword, SqlTokenKind.Keyword, SqlTokenKind.Keyword]);
        (await Parser.TokenizeAsync("a = 1")).GetValueOrThrow().Select(token => token.Kind)
            .ShouldBe([SqlTokenKind.Identifier, SqlTokenKind.Operator, SqlTokenKind.NumericLiteral]);
    }

    [Theory]
    [InlineData("SELECT c.name FROM customers c JOIN public.orders o ON true WHERE c.id = 5", "SELECT customers public.orders")]
    [InlineData("SELECT * FROM t WHERE secret = 'hunter2' AND id = 42", "SELECT t")]
    [InlineData("SELECT * FROM t t1 JOIN t t2 ON true", "SELECT t")]
    [InlineData("INSERT INTO audit SELECT * FROM users", "INSERT audit users")]
    [InlineData("WITH r AS (SELECT 1) UPDATE t SET a = 1 FROM r", "UPDATE t")]
    [InlineData("DELETE FROM t", "DELETE t")]
    [InlineData("VALUES (1)", "VALUES")]
    [InlineData("SELECT 1", "SELECT")]
    [InlineData("CREATE TABLE s.t (a int)", "CREATE TABLE s.t")]
    [InlineData("ALTER TABLE t ADD COLUMN b int", "ALTER TABLE t")]
    [InlineData("DROP TABLE IF EXISTS a, s.b", "DROP TABLE a s.b")]
    [InlineData("CREATE UNIQUE INDEX CONCURRENTLY i ON t (a)", "CREATE INDEX t")]
    [InlineData("CREATE OR REPLACE VIEW v AS SELECT * FROM t", "CREATE VIEW v t")]
    [InlineData("VACUUM t", "VACUUM t")]
    [InlineData("TRUNCATE t", "TRUNCATE t")]
    [InlineData("BEGIN", "BEGIN")]
    [InlineData("SET search_path = x", "SET")]
    [InlineData("/* hi */ GRANT SELECT ON t TO x", "GRANT t")]
    [InlineData("EXPLAIN ANALYZE SELECT * FROM t", "EXPLAIN t")]
    [InlineData("CALL p()", "CALL")]
    [InlineData("SELECT 1; DELETE FROM t", "SELECT; DELETE t")]
    public void OperationSummary(string query, string expected)
    {
        Parser.OperationSummary(query).GetValueOrThrow().ShouldBe(expected);
    }

    [Fact]
    public void OperationSummaryIsCutAtAWordBoundary()
    {
        const string query = "SELECT * FROM customers, orders, order_items";

        Parser.OperationSummary(query).GetValueOrThrow().ShouldBe("SELECT customers orders order_items");
        Parser.OperationSummary(query, 20).GetValueOrThrow().ShouldBe("SELECT customers");
        Parser.OperationSummary(query, 23).GetValueOrThrow().ShouldBe("SELECT customers orders");
        Parser.OperationSummary("SELECT FROM WHERE").IsSuccess.ShouldBeFalse();
        Parser.OperationSummary("").GetValueOrThrow().ShouldBe("");
    }

    [Fact]
    public void PlpgsqlQueriesAreParsedInTheirOwnMode()
    {
        const string sql = """
            CREATE FUNCTION f(n int) RETURNS int AS $$
            DECLARE total int := 0; r record;
            BEGIN
              FOR r IN SELECT * FROM items WHERE qty > n LOOP total := total + r.qty; END LOOP;
              IF total > 100 THEN RETURN total; END IF;
              PERFORM log_it(total);
              RETURN 0;
            END $$ LANGUAGE plpgsql
            """;
        var function = Parser.ParsePlpgsqlFunctions(sql).GetValueOrThrow().ShouldHaveSingleItem();

        var queries = function.ParseQueries();

        queries.ShouldAllBe(query => query.Tree != null && query.Error == null);
        queries.Select(query => (query.Sql, query.Tree!.Stmts[0].Stmt.Unwrap()!.Descriptor.Name)).ShouldBe(
        [
            ("0", "SelectStmt"),
            // An assignment is not a query; PL/pgSQL parses it in its own mode.
            ("total := total + r.qty", "PLAssignStmt"),
            ("SELECT * FROM items WHERE qty > n", "SelectStmt"),
            ("total > 100", "SelectStmt"),
            ("SELECT log_it(total)", "SelectStmt"),
            ("0", "SelectStmt")
        ]);

        // The tables and functions a function body uses, through the same APIs as any query.
        var trees = queries.Select(query => query.Tree!).ToList();
        trees.SelectMany(tree => tree.GetReferences().Tables).Select(table => table.Name).ShouldBe(["items"]);
        trees.SelectMany(tree => tree.GetReferences().Functions).Select(call => call.Name).ShouldBe(["log_it"]);
    }

    [Fact]
    public void PlpgsqlParseQueryOnOtherNodesAndBrokenSql()
    {
        var function = Parser.ParsePlpgsqlFunctions(
            "CREATE FUNCTION f() RETURNS void AS $$ BEGIN PERFORM 1; END $$ LANGUAGE plpgsql").GetValueOrThrow()[0];

        var body = function.Body.ShouldNotBeNull();
        body.ParseQuery().Error.ShouldNotBeNull().Message.ShouldBe("a PLpgSQL_stmt_block node holds no SQL to parse");
        function.Root.Descendants().Single(node => node.Kind == "PLpgSQL_expr").ParseQuery().GetValueOrThrow()
            .Deparse().GetValueOrThrow().ShouldBe("SELECT 1");
    }
}
