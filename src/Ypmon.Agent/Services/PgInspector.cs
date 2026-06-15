using Npgsql;

namespace Ypmon.Agent.Services;

/// <summary>Проверка подключения к PostgreSQL и получение списка баз данных.</summary>
public static class PgInspector
{
    private static string ConnString(string host, int port, string user, string password, string db)
        => $"Host={host};Port={port};Username={user};Password={password};Database={db};" +
           "Timeout=5;Command Timeout=10;SSL Mode=Prefer;Trust Server Certificate=true";

    /// <summary>Проверка соединения и получение списка БД. Подключается к служебной базе postgres.</summary>
    public static (bool ok, string message, List<string> databases) ListDatabases(
        string host, int port, string user, string password)
    {
        var dbs = new List<string>();
        try
        {
            using var conn = new NpgsqlConnection(ConnString(host, port, user, password, "postgres"));
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "SELECT datname FROM pg_database WHERE datistemplate = false AND datallowconn = true ORDER BY datname",
                conn);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) dbs.Add(rd.GetString(0));
            return (true, $"Соединение успешно. Найдено баз: {dbs.Count}.", dbs);
        }
        catch (Exception ex)
        {
            return (false, "Ошибка соединения: " + ex.Message, dbs);
        }
    }
}
