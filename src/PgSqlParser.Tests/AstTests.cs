using Google.Protobuf;
using Shouldly;
using Xunit;

namespace PgSqlParser.Tests;

public class AstTests
{
    public static TheoryData<string, IMessage> Expressions => new()
    {
        { "name", Ast.Column("name") },
        { "t.name", Ast.Column("t", "name") },
        { "s.t.name", Ast.Column("s", "t", "name") },
        { "1", Ast.Const(1) },
        { "0", Ast.Const(0) },
        { "5000000000", Ast.Const(5000000000) },
        { "1.5", Ast.Const(1.5m) },
        { "2.25", Ast.Const(2.25) },
        { "'it''s'", Ast.Const("it's") },
        { "'😀 é'", Ast.Const("😀 é") },
        { "true", Ast.Const(true) },
        { "false", Ast.Const(false) },
        { "NULL", Ast.Null() },
        { "$1", Ast.Param(1) },
        { "a = 1", Ast.Eq(Ast.Column("a"), Ast.Const(1)) },
        { "a <> 1", Ast.NotEq(Ast.Column("a"), Ast.Const(1)) },
        { "a < 1", Ast.Lt(Ast.Column("a"), Ast.Const(1)) },
        { "a <= 1", Ast.LtEq(Ast.Column("a"), Ast.Const(1)) },
        { "a > 1", Ast.Gt(Ast.Column("a"), Ast.Const(1)) },
        { "a >= 1", Ast.GtEq(Ast.Column("a"), Ast.Const(1)) },
        { "a || b", Ast.Op("||", Ast.Column("a"), Ast.Column("b")) },
        { "a = 1 AND b = 2", Ast.And(Ast.Eq(Ast.Column("a"), Ast.Const(1)), Ast.Eq(Ast.Column("b"), Ast.Const(2))) },
        { "a OR b OR c", Ast.Or(Ast.Column("a"), Ast.Or(Ast.Column("b"), Ast.Column("c"))) },
        { "a AND b AND c", Ast.And(Ast.And(Ast.Column("a"), Ast.Column("b")), Ast.Column("c")) },
        { "a AND (b OR c)", Ast.And(Ast.Column("a"), Ast.Or(Ast.Column("b"), Ast.Column("c"))) },
        { "NOT a", Ast.Not(Ast.Column("a")) },
        { "a IS NULL", Ast.IsNull(Ast.Column("a")) },
        { "a IS NOT NULL", Ast.IsNotNull(Ast.Column("a")) },
        { "a IN (1, 2, 3)", Ast.In(Ast.Column("a"), Ast.Const(1), Ast.Const(2), Ast.Const(3)) },
        { "a NOT IN ('x')", Ast.NotIn(Ast.Column("a"), Ast.Const("x")) },
        { "lower(name)", Ast.Call("lower", Ast.Column("name")) },
        { "now()", Ast.Call("now") },
        { "pg_catalog.lower(a, 1)", Ast.Call(["pg_catalog", "lower"], Ast.Column("a"), Ast.Const(1)) },
        { "count(*)", Ast.CountStar() },
        { "$1::int", Ast.Cast(Ast.Param(1), "int") },
        { "a::numeric(10, 2)", Ast.Cast(Ast.Column("a"), "numeric(10,2)") },
        { "tenant_id = $1 AND deleted_at IS NULL", Ast.Expression("tenant_id = $1 and deleted_at is null") }
    };

    [Theory]
    [MemberData(nameof(Expressions))]
    public void ExpressionBuildersMatchTheParser(string sql, IMessage built)
    {
        // The same tree the parser gives, and it deparses to the same text.
        built.AsNode().EqualsIgnoringLocations(Parser.ParseExpression(sql).GetValueOrThrow()).ShouldBeTrue(sql);
        built.Deparse().GetValueOrThrow().ShouldBe(sql);
    }

    [Fact]
    public void TablesTargetsAndOrderBy()
    {
        var parsed = Parser.Parse("SELECT a, lower(b) AS lb, * FROM public.users u ORDER BY a, b DESC").GetValueOrThrow()
            .Stmts[0].Stmt.SelectStmt;

        Ast.Table("public", "users", "u").EqualsIgnoringLocations(parsed.FromClause[0].RangeVar).ShouldBeTrue();
        Ast.Table("users").Deparse().GetValueOrThrow().ShouldBe("users");
        Ast.Table("My Schema", "order").Deparse().GetValueOrThrow().ShouldBe("\"My Schema\".\"order\"");

        Ast.Target(Ast.Column("a")).EqualsIgnoringLocations(parsed.TargetList[0].ResTarget).ShouldBeTrue();
        Ast.Target(Ast.Call("lower", Ast.Column("b")), "lb").EqualsIgnoringLocations(parsed.TargetList[1].ResTarget).ShouldBeTrue();
        Ast.Target(Ast.Star()).EqualsIgnoringLocations(parsed.TargetList[2].ResTarget).ShouldBeTrue();
        Ast.Star("t").Deparse().GetValueOrThrow().ShouldBe("t.*");

        Ast.OrderBy(Ast.Column("a")).EqualsIgnoringLocations(parsed.SortClause[0].SortBy).ShouldBeTrue();
        Ast.OrderBy(Ast.Column("b"), descending: true).EqualsIgnoringLocations(parsed.SortClause[1].SortBy).ShouldBeTrue();
    }

    [Fact]
    public void SelectMatchesTheParser()
    {
        var built = Ast.Select(
            [Ast.Column("id"), Ast.Target(Ast.Call("lower", Ast.Column("name")), "n")],
            Ast.Table("public", "users", "u"),
            Ast.And(Ast.Eq(Ast.Column("u", "tenant"), Ast.Param(1)), Ast.IsNull(Ast.Column("deleted_at"))));

        const string sql = "SELECT id, lower(name) AS n FROM public.users u WHERE u.tenant = $1 AND deleted_at IS NULL";
        built.Deparse().GetValueOrThrow().ShouldBe(sql);
        built.EqualsIgnoringLocations(Parser.Parse(sql).GetValueOrThrow().Stmts[0].Stmt.SelectStmt).ShouldBeTrue();

        Ast.Select([Ast.Const(1)]).Deparse().GetValueOrThrow().ShouldBe("SELECT 1");
    }

    [Fact]
    public void BuiltNodesCanGoIntoAParsedTree()
    {
        var tree = Parser.Parse("SELECT id FROM users").GetValueOrThrow();
        var select = tree.Stmts[0].Stmt.SelectStmt;

        select.WhereClause = Ast.Eq(Ast.Column("tenant_id"), Ast.Param(1)).AsNode();
        select.TargetList.Add(Ast.Target(Ast.CountStar(), "n").AsNode());
        select.SortClause.Add(Ast.OrderBy(Ast.Column("id"), descending: true).AsNode());

        tree.Deparse().GetValueOrThrow().ShouldBe("SELECT id, count(*) AS n FROM users WHERE tenant_id = $1 ORDER BY id DESC");
    }

    [Fact]
    public void RejectsInvalidArguments()
    {
        Should.Throw<ArgumentException>(() => Ast.Column());
        Should.Throw<ArgumentException>(() => Ast.And(Ast.Column("a")));
        Should.Throw<ArgumentException>(() => Ast.In(Ast.Column("a")));
        Should.Throw<ArgumentException>(() => Ast.Call([]));
        Should.Throw<ArgumentNullException>(() => Ast.Const((string)null!));
        Should.Throw<ArgumentNullException>(() => Ast.Eq(null!, Ast.Const(1)));
        Should.Throw<PgSqlParserException>(() => Ast.Expression("a, b"));
        Should.Throw<PgSqlParserException>(() => Ast.Cast(Ast.Column("a"), "not a type!"));
    }
}
