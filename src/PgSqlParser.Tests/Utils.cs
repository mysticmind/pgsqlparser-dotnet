using System.Reflection;
using Google.Protobuf.Reflection;

namespace PgSqlParser.Tests;

static class Utils
{
    public static IEnumerable<string> ReadLines(string path)
    {
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
    
    public static string ReadFile(string path)
    {
        return File.ReadAllText(path);
    }

    // Enum value name as declared in pg_query.proto, e.g. "ASCII_40" and "RESERVED_KEYWORD".
    public static string ProtoName<T>(T value) where T : struct, Enum
    {
        return typeof(T).GetField(value.ToString())!.GetCustomAttribute<OriginalNameAttribute>()!.Name;
    }
}