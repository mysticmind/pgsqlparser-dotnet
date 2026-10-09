using Google.Protobuf;

namespace PgSqlParser;

/// <summary>
/// Common changes to statements, made on the parse tree so they hold for any way the SQL was written.
/// They change the tree in place; deparse it to get the SQL.
/// </summary>
public static class QueryRewrites
{
    /// <summary>
    /// Adds a condition to the WHERE clause of a SELECT, UPDATE or DELETE, combining it with AND if
    /// there already is one.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The statement is of another kind, or is a UNION, INTERSECT or EXCEPT, which has no WHERE of its own.
    /// </exception>
    public static void AddWhere(this IMessage statement, IMessage condition)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(condition);

        switch (Statement(statement))
        {
            case SelectStmt { Op: SetOperation.SetopNone } select when select.ValuesLists.Count == 0:
                select.WhereClause = Combine(select.WhereClause, condition);
                break;
            case UpdateStmt update:
                update.WhereClause = Combine(update.WhereClause, condition);
                break;
            case DeleteStmt delete:
                delete.WhereClause = Combine(delete.WhereClause, condition);
                break;
            case var other:
                throw new NotSupportedException($"A WHERE condition cannot be added to {Describe(other)}.");
        }
    }

    /// <summary>Sets the LIMIT of a SELECT, replacing any it has.</summary>
    public static void SetLimit(this IMessage statement, long limit)
    {
        var select = Select(statement, "A LIMIT");
        select.LimitCount = Ast.Const(limit).AsNode();
        select.LimitOption = LimitOption.Count;
    }

    /// <summary>
    /// Makes sure a SELECT returns at most <paramref name="max"/> rows: adds a LIMIT if it has none,
    /// lowers a larger constant LIMIT, and wraps any other LIMIT expression in <c>LEAST(..., max)</c>.
    /// </summary>
    public static void CapLimit(this IMessage statement, long max)
    {
        var select = Select(statement, "A LIMIT");
        switch (select.LimitCount?.Unwrap())
        {
            // No LIMIT, or LIMIT ALL / LIMIT NULL.
            case null or A_Const { Isnull: true }:
                select.SetLimit(max);
                break;
            case A_Const { Ival: { } integer }:
                if (integer.Ival > max)
                    select.SetLimit(max);
                break;
            case var expression:
                var least = new MinMaxExpr { Op = MinMaxOp.IsLeast };
                least.Args.Add(expression.AsNode());
                least.Args.Add(Ast.Const(max).AsNode());
                select.LimitCount = least.AsNode();
                break;
        }
    }

    /// <summary>Sets the OFFSET of a SELECT, replacing any it has.</summary>
    public static void SetOffset(this IMessage statement, long offset)
    {
        Select(statement, "An OFFSET").LimitOffset = Ast.Const(offset).AsNode();
    }

    /// <summary>
    /// Returns a new statement that counts the rows of a SELECT: <c>SELECT count(*) FROM (...) AS alias</c>.
    /// The original is used as it is, including its LIMIT, and is not modified.
    /// </summary>
    public static SelectStmt ToCount(this IMessage statement, string alias = "q")
    {
        var select = Select(statement, "A count");
        var subquery = new RangeSubselect { Subquery = select.AsNode(), Alias = new Alias { Aliasname = alias } };
        return Ast.Select([Ast.CountStar()], subquery);
    }

    /// <summary>
    /// Gives every table reference that has no schema the schema <paramref name="schema"/>. Names that
    /// refer to a CTE are left alone. Returns the number of references changed.
    /// </summary>
    public static int QualifyTables(this IMessage root, string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var changed = 0;
        foreach (var table in root.GetReferences().Tables)
        {
            if (table.IsCte || table.Schema is not null)
                continue;

            table.Node.Schemaname = schema;
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Changes every table reference in schema <paramref name="from"/> to schema <paramref name="to"/>.
    /// Returns the number of references changed. Only table references are changed, not functions or types.
    /// </summary>
    public static int RenameSchema(this IMessage root, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var changed = 0;
        foreach (var table in root.Descendants<RangeVar>())
        {
            if (table.Schemaname != from)
                continue;

            table.Schemaname = to;
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Changes every reference to table <paramref name="from"/> (with no schema, or in <paramref name="schema"/>
    /// when given) to <paramref name="to"/>. CTE names are left alone. Returns the number of references changed.
    /// </summary>
    public static int RenameTable(this IMessage root, string from, string to, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var changed = 0;
        foreach (var table in root.GetReferences().Tables)
        {
            if (table.IsCte || table.Name != from || table.Schema != schema)
                continue;

            table.Node.Relname = to;
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Adds IF NOT EXISTS to a CREATE statement that supports it (table, index, sequence, schema,
    /// extension, CREATE TABLE AS, materialized view). Returns false for other statements.
    /// </summary>
    public static bool EnsureIfNotExists(this IMessage statement)
    {
        switch (Statement(statement))
        {
            case CreateStmt create: create.IfNotExists = true; return true;
            case IndexStmt { Idxname.Length: > 0 } index: index.IfNotExists = true; return true;
            case CreateSeqStmt sequence: sequence.IfNotExists = true; return true;
            case CreateSchemaStmt schema: schema.IfNotExists = true; return true;
            case CreateExtensionStmt extension: extension.IfNotExists = true; return true;
            case CreateTableAsStmt createAs: createAs.IfNotExists = true; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Adds OR REPLACE to a CREATE statement that supports it (view, function, procedure, trigger, rule).
    /// Returns false for other statements.
    /// </summary>
    public static bool EnsureOrReplace(this IMessage statement)
    {
        switch (Statement(statement))
        {
            case ViewStmt view: view.Replace = true; return true;
            case CreateFunctionStmt function: function.Replace = true; return true;
            case CreateTrigStmt trigger: trigger.Replace = true; return true;
            case RuleStmt rule: rule.Replace = true; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Returns the DROP statement that undoes a CREATE statement, for tables, indexes, views,
    /// materialized views, sequences, schemas, types, domains, functions, procedures, triggers,
    /// policies and extensions.
    /// </summary>
    /// <exception cref="NotSupportedException">There is no DROP for this statement here.</exception>
    public static IMessage ToDropStatement(this IMessage statement, bool ifExists = false, bool cascade = false)
    {
        var (kind, name) = Statement(statement) switch
        {
            CreateStmt create => ("TABLE", Name(create.Relation)),
            IndexStmt { Idxname.Length: > 0 } index =>
                ("INDEX", PgIdentifier.Quote([.. Parts(index.Relation.Schemaname), index.Idxname])),
            ViewStmt view => ("VIEW", Name(view.View)),
            CreateTableAsStmt { Into.Rel: { } relation } createAs =>
                (createAs.Objtype == ObjectType.ObjectMatview ? "MATERIALIZED VIEW" : "TABLE", Name(relation)),
            CreateSeqStmt sequence => ("SEQUENCE", Name(sequence.Sequence)),
            CreateSchemaStmt { Schemaname.Length: > 0 } schema => ("SCHEMA", PgIdentifier.Quote(schema.Schemaname)),
            CreateExtensionStmt extension => ("EXTENSION", PgIdentifier.Quote(extension.Extname)),
            CreateEnumStmt type => ("TYPE", Name(type.TypeName)),
            CreateRangeStmt type => ("TYPE", Name(type.TypeName)),
            CompositeTypeStmt type => ("TYPE", Name(type.Typevar)),
            CreateDomainStmt domain => ("DOMAIN", Name(domain.Domainname)),
            CreateFunctionStmt function =>
                (function.IsProcedure ? "PROCEDURE" : "FUNCTION", $"{Name(function.Funcname)}({Signature(function)})"),
            CreateTrigStmt trigger => ("TRIGGER", $"{PgIdentifier.Quote(trigger.Trigname)} ON {Name(trigger.Relation)}"),
            CreatePolicyStmt policy => ("POLICY", $"{PgIdentifier.Quote(policy.PolicyName)} ON {Name(policy.Table)}"),
            var other => throw new NotSupportedException($"There is no DROP statement for {Describe(other)}.")
        };

        // Written as SQL and parsed, so the tree is exactly what PostgreSQL's parser builds for it.
        var sql = $"DROP {kind} {(ifExists ? "IF EXISTS " : "")}{name}{(cascade ? " CASCADE" : "")}";
        return Parser.Parse(sql).GetValueOrThrow().Stmts[0].Stmt.Unwrap()!;
    }

    private static IMessage? Statement(IMessage statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        return statement switch
        {
            ParseResult { Stmts.Count: 1 } tree => tree.Stmts[0].Stmt?.Unwrap(),
            ParseResult => throw new NotSupportedException("This needs a single statement, and the parse tree has several or none."),
            RawStmt raw => raw.Stmt?.Unwrap(),
            Node node => node.Unwrap(),
            _ => statement
        };
    }

    private static SelectStmt Select(IMessage statement, string what)
    {
        return Statement(statement) as SelectStmt
               ?? throw new NotSupportedException($"{what} applies to a SELECT, not to {Describe(Statement(statement))}.");
    }

    private static string Describe(IMessage? node)
    {
        return node switch
        {
            null => "an empty statement",
            SelectStmt { Op: not SetOperation.SetopNone } => "a UNION, INTERSECT or EXCEPT",
            SelectStmt { ValuesLists.Count: > 0 } => "a VALUES list",
            _ => $"a {node.Descriptor.Name}"
        };
    }

    private static Node Combine(Node? existing, IMessage condition)
    {
        return existing?.Unwrap() is { } current ? Ast.And(current, condition).AsNode() : condition.AsNode();
    }

    private static string[] Parts(string schema) => schema.Length > 0 ? [schema] : [];

    private static string Name(RangeVar relation) => PgIdentifier.Quote([.. Parts(relation.Schemaname), relation.Relname]);

    private static string Name(IEnumerable<Node> names) => PgIdentifier.Quote(names.Select(part => part.String.Sval).ToArray());

    // The argument types that identify a function: everything except OUT and TABLE parameters.
    private static string Signature(CreateFunctionStmt function)
    {
        var types = function.Parameters
            .Select(parameter => parameter.FunctionParameter)
            .Where(parameter => parameter.Mode is not (FunctionParameterMode.FuncParamOut or FunctionParameterMode.FuncParamTable))
            .Select(parameter => (parameter.Mode == FunctionParameterMode.FuncParamVariadic ? "VARIADIC " : "")
                                 + parameter.ArgType.Deparse().GetValueOrThrow());

        return string.Join(", ", types);
    }
}
