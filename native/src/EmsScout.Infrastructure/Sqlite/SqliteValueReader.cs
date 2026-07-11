using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

internal static class SqliteValueReader
{
    public static string ReadString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? string.Empty
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static int ReadInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public static long? ReadNullableInt64(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public static double? ReadNullableDouble(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }
}
