using Google.Protobuf;

namespace PgSqlParser;

/// <summary>
/// How a statement uses a table.
/// </summary>
public enum TableRole
{
    /// <summary>The table is read: in a FROM clause, a join, a subquery and so on.</summary>
    Read,

    /// <summary>The table is the target of INSERT, UPDATE, DELETE, MERGE, TRUNCATE or COPY ... FROM.</summary>
    Write,

    /// <summary>The table is defined, altered, dropped or otherwise managed by a utility statement.</summary>
    Ddl
}

/// <summary>
/// A table named in a query.
/// </summary>
/// <param name="Node">The node that names it.</param>
/// <param name="Role">How the statement uses it.</param>
/// <param name="IsCte">
/// True if the name refers to a common table expression of the statement and not to a table in the database.
/// </param>
/// <param name="StatementIndex">The top-level statement it is in, when known.</param>
public sealed record TableReference(RangeVar Node, TableRole Role, bool IsCte, int? StatementIndex)
{
    public string? Schema => Node.Schemaname.Length > 0 ? Node.Schemaname : null;
    public string Name => Node.Relname;
    public string? Alias => Node.Alias is { Aliasname.Length: > 0 } alias ? alias.Aliasname : null;

    /// <summary>The name a column reference uses for this table: its alias, or its name without one.</summary>
    public string ReferenceName => Alias ?? Name;

    public override string ToString() => Schema is null ? Name : $"{Schema}.{Name}";
}

/// <summary>
/// A function called in a query.
/// </summary>
public sealed record FunctionReference(FuncCall Node, string? Schema, string Name, int? StatementIndex)
{
    public override string ToString() => Schema is null ? Name : $"{Schema}.{Name}";
}

/// <summary>
/// A column named in a query.
/// </summary>
/// <param name="Node">The node that names it.</param>
/// <param name="Qualifier">What is written in front of the column, such as a table alias, or null.</param>
/// <param name="Name">The column name, or <c>*</c>.</param>
/// <param name="Table">
/// The table the column belongs to, when the query alone settles it: the qualifier matches a table or
/// alias in scope, or there is no qualifier and only one table in scope. Null when it would take the
/// database catalog to tell, or when the column comes from a subquery or a CTE.
/// </param>
/// <param name="StatementIndex">The top-level statement it is in, when known.</param>
public sealed record ColumnReference(ColumnRef Node, string? Qualifier, string Name, TableReference? Table, int? StatementIndex)
{
    public override string ToString() => Qualifier is null ? Name : $"{Qualifier}.{Name}";
}

/// <summary>
/// The tables, functions and columns a query refers to, in the order they appear in its parse tree.
/// </summary>
public sealed class QueryReferences
{
    internal QueryReferences(IReadOnlyList<TableReference> tables, IReadOnlyList<FunctionReference> functions,
        IReadOnlyList<ColumnReference> columns)
    {
        Tables = tables;
        Functions = functions;
        Columns = columns;
    }

    public IReadOnlyList<TableReference> Tables { get; }
    public IReadOnlyList<FunctionReference> Functions { get; }
    public IReadOnlyList<ColumnReference> Columns { get; }
}

/// <summary>
/// Finds what a query refers to by reading its parse tree.
/// </summary>
public static class QueryReferenceExtensions
{
    /// <summary>
    /// Returns the tables, functions and columns referred to below <paramref name="root"/>. Tables carry
    /// their role and whether they are really a CTE; columns are matched to their table where the query
    /// alone settles it.
    /// </summary>
    public static QueryReferences GetReferences(this IMessage root)
    {
        ArgumentNullException.ThrowIfNull(root);

        // The root is not among a visit's ancestors, but it is the outermost thing enclosing every node.
        var top = root is Node wrapper ? wrapper.Unwrap() ?? root : root;
        var tables = new List<TableReference>();
        var functions = new List<FunctionReference>();
        var columnVisits = new List<NodeVisit>();
        var tableByNode = new Dictionary<RangeVar, TableReference>(ReferenceEqualityComparer.Instance);

        foreach (var visit in root.Walk())
        {
            switch (visit.Node)
            {
                case RangeVar table:
                    var reference = new TableReference(table, RoleOf(visit, top), IsCte(table, visit, top), visit.StatementIndex);
                    tables.Add(reference);
                    tableByNode[table] = reference;
                    break;
                case FuncCall call when call.Funcname.Count > 0:
                    var names = call.Funcname.Select(part => part.String?.Sval ?? string.Empty).ToList();
                    functions.Add(new FunctionReference(call, names.Count > 1 ? names[^2] : null, names[^1], visit.StatementIndex));
                    break;
                case ColumnRef:
                    columnVisits.Add(visit);
                    break;
            }
        }

        // Columns are resolved last, since a column can come before the table it belongs to.
        var columns = columnVisits.Select(visit => ResolveColumn(visit, top, tableByNode)).ToList();
        return new QueryReferences(tables, functions, columns);
    }

    private static IEnumerable<IMessage> Enclosing(NodeVisit visit, IMessage top)
    {
        foreach (var ancestor in visit.Ancestors)
            yield return ancestor;

        yield return top;
    }

    private static TableRole RoleOf(NodeVisit visit, IMessage top)
    {
        switch (visit.Parent ?? top)
        {
            case InsertStmt or UpdateStmt or DeleteStmt or MergeStmt when visit.FieldName == nameof(InsertStmt.Relation):
            case TruncateStmt:
            case CopyStmt { IsFrom: true }:
                return TableRole.Write;
            // SELECT ... INTO and CREATE TABLE AS create the table they name.
            case IntoClause:
                return TableRole.Ddl;
        }

        // Anything inside a query, or elsewhere in a data-changing statement, is read.
        foreach (var ancestor in Enclosing(visit, top))
        {
            if (ancestor is SelectStmt or InsertStmt or UpdateStmt or DeleteStmt or MergeStmt or CopyStmt)
                return TableRole.Read;
        }

        var statement = visit.Statement ?? top as RawStmt ?? new RawStmt { Stmt = top is ParseResult ? null : top.AsNode() };
        return statement.Classify().Kind is StatementKind.Ddl or StatementKind.Maintenance or StatementKind.Lock
            ? TableRole.Ddl
            : TableRole.Read;
    }

    // An unqualified name that matches a CTE of an enclosing statement refers to that CTE.
    private static bool IsCte(RangeVar table, NodeVisit visit, IMessage top)
    {
        if (table.Schemaname.Length > 0)
            return false;

        foreach (var ancestor in Enclosing(visit, top))
        {
            var with = ancestor switch
            {
                SelectStmt select => select.WithClause,
                InsertStmt insert => insert.WithClause,
                UpdateStmt update => update.WithClause,
                DeleteStmt delete => delete.WithClause,
                MergeStmt merge => merge.WithClause,
                _ => null
            };

            if (with is not null && with.Ctes.Any(cte => cte.CommonTableExpr?.Ctename == table.Relname))
                return true;
        }

        return false;
    }

    private static ColumnReference ResolveColumn(NodeVisit visit, IMessage top, Dictionary<RangeVar, TableReference> tableByNode)
    {
        var column = (ColumnRef)visit.Node;
        var parts = column.Fields.Select(field => field.String?.Sval ?? (field.AStar is not null ? "*" : string.Empty)).ToList();
        var name = parts.Count > 0 ? parts[^1] : string.Empty;
        var qualifier = parts.Count > 1 ? parts[^2] : null;

        TableReference? table = null;
        foreach (var ancestor in Enclosing(visit, top))
        {
            var scope = ScopeOf(ancestor);
            if (scope is null)
                continue;

            if (qualifier is null)
            {
                // Without a qualifier only a single table in the nearest scope is certain.
                if (scope is [{ Table: { } only }])
                    table = tableByNode.GetValueOrDefault(only);
                break;
            }

            var match = scope.FirstOrDefault(item => item.Name == qualifier);
            if (match.Name is not null)
            {
                table = match.Table is null ? null : tableByNode.GetValueOrDefault(match.Table);
                break;
            }
        }

        // A name that is really a CTE is not a table to attribute columns to.
        return new ColumnReference(column, qualifier, name, table is { IsCte: true } ? null : table, visit.StatementIndex);
    }

    // The names a statement brings into scope for its columns. Table is null for a subquery, function
    // or other source that is not a plain table.
    private static List<(string Name, RangeVar? Table)>? ScopeOf(IMessage statement)
    {
        var scope = new List<(string Name, RangeVar? Table)>();
        switch (statement)
        {
            case SelectStmt select:
                foreach (var item in select.FromClause)
                    AddFromItem(item, scope);
                break;
            case UpdateStmt update:
                AddTable(update.Relation, scope);
                foreach (var item in update.FromClause)
                    AddFromItem(item, scope);
                break;
            case DeleteStmt delete:
                AddTable(delete.Relation, scope);
                foreach (var item in delete.UsingClause)
                    AddFromItem(item, scope);
                break;
            case InsertStmt insert:
                AddTable(insert.Relation, scope);
                break;
            case MergeStmt merge:
                AddTable(merge.Relation, scope);
                AddFromItem(merge.SourceRelation, scope);
                break;
            default:
                return null;
        }

        return scope;
    }

    private static void AddFromItem(Node? item, List<(string Name, RangeVar? Table)> scope)
    {
        switch (item?.Unwrap())
        {
            case RangeVar table:
                AddTable(table, scope);
                break;
            case JoinExpr join:
                AddFromItem(join.Larg, scope);
                AddFromItem(join.Rarg, scope);
                if (join.Alias is { Aliasname.Length: > 0 } joinAlias)
                    scope.Add((joinAlias.Aliasname, null));
                break;
            case RangeSubselect { Alias.Aliasname: { Length: > 0 } alias }:
                scope.Add((alias, null));
                break;
            case RangeFunction { Alias.Aliasname: { Length: > 0 } alias }:
                scope.Add((alias, null));
                break;
            case RangeTableFunc { Alias.Aliasname: { Length: > 0 } alias }:
                scope.Add((alias, null));
                break;
        }
    }

    private static void AddTable(RangeVar? table, List<(string Name, RangeVar? Table)> scope)
    {
        if (table is null)
            return;

        scope.Add((table.Alias is { Aliasname.Length: > 0 } alias ? alias.Aliasname : table.Relname, table));
    }
}
