using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DominiusPlay.Infrastructure.Data;

/// <summary>
/// Atualização aditiva do banco SQLite criado por versões anteriores: cria colunas novas que faltam
/// (nunca remove nem altera dados). Tabelas novas são criadas pelo EnsureCreated somente em banco vazio,
/// então também são criadas aqui a partir do modelo.
/// </summary>
public static class SchemaUpgrader
{
    /// <summary>Atualiza o banco do provedor em uso (SQLite ou PostgreSQL). Só acrescenta; nunca remove nem altera dados.</summary>
    public static Task<IReadOnlyList<string>> UpgradeAsync(CrmDbContext db, CancellationToken ct = default) =>
        db.Database.IsSqlite() ? UpgradeSqliteAsync(db, ct) : db.Database.IsNpgsql() ? UpgradePostgresAsync(db, ct) : Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// PostgreSQL: EnsureCreated não cria tabelas novas em banco existente. Cria as tabelas/índices que faltam
    /// (IF NOT EXISTS) e acrescenta colunas novas com valor padrão compatível.
    /// </summary>
    public static async Task<IReadOnlyList<string>> UpgradePostgresAsync(CrmDbContext db, CancellationToken ct = default)
    {
        var changes = new List<string>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        var creator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>(db);
        var script = creator.GenerateCreateScript();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null) continue;
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @t";
                var p = cmd.CreateParameter(); p.ParameterName = "t"; p.Value = table; cmd.Parameters.Add(p);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) existing.Add(r.GetString(0));
            }
            if (existing.Count == 0)
            {
                foreach (var stmt in script.Split(";", StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
                             .Where(s => s.StartsWith($"CREATE TABLE \"{table}\"") ||
                                         ((s.StartsWith("CREATE UNIQUE INDEX") || s.StartsWith("CREATE INDEX")) && s.Contains($" ON \"{table}\""))))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = stmt.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
                        .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ").Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ");
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                changes.Add($"tabela {table}");
                continue;
            }
            var id = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var p in entity.GetProperties())
            {
                var col = p.GetColumnName(id);
                if (col is null || existing.Contains(col)) continue;
                var type = p.GetColumnType();
                var def = p.IsNullable ? "" : " NOT NULL DEFAULT " + PgDefault(p.ClrType);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN IF NOT EXISTS \"{col}\" {type}{def}";
                await cmd.ExecuteNonQueryAsync(ct);
                changes.Add($"{table}.{col}");
            }
        }
        return changes;
    }

    private static string PgDefault(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string)) return "''";
        if (t == typeof(bool)) return "FALSE";
        if (t == typeof(Guid)) return "'00000000-0000-0000-0000-000000000000'";
        if (t == typeof(DateTimeOffset) || t == typeof(DateTime)) return "now()";
        if (t == typeof(TimeOnly)) return "'00:00'";
        if (t == typeof(DateOnly)) return "CURRENT_DATE";
        return "0"; // números e enums
    }

    public static async Task<IReadOnlyList<string>> UpgradeSqliteAsync(CrmDbContext db, CancellationToken ct = default)
    {
        var changes = new List<string>();
        if (!db.Database.IsSqlite()) return changes;
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        var creator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>(db);
        var createScript = creator.GenerateCreateScript();

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null) continue;
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) existing.Add(r.GetString(1));
            }
            if (existing.Count == 0)
            {
                // tabela nova: executa o CREATE TABLE (e índices) do script gerado pelo próprio modelo
                // só o CREATE TABLE desta tabela e os índices dela (não os de tabelas que apenas a referenciam)
                foreach (var stmt in createScript.Split(";", StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
                             .Where(s => s.StartsWith($"CREATE TABLE \"{table}\"") ||
                                         ((s.StartsWith("CREATE UNIQUE INDEX") || s.StartsWith("CREATE INDEX")) && s.Contains($" ON \"{table}\""))))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = stmt.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
                        .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ").Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ");
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                changes.Add($"tabela {table}");
                continue;
            }
            var id = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var p in entity.GetProperties())
            {
                var col = p.GetColumnName(id);
                if (col is null || existing.Contains(col)) continue;
                var type = p.GetColumnType();
                var def = p.IsNullable ? "" : type.Equals("TEXT", StringComparison.OrdinalIgnoreCase) ? " NOT NULL DEFAULT ''" : " NOT NULL DEFAULT 0";
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{col}\" {type}{def}";
                await cmd.ExecuteNonQueryAsync(ct);
                changes.Add($"{table}.{col}");
            }
        }
        return changes;
    }
}
