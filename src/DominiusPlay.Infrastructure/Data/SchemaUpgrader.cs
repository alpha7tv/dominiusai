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
                foreach (var stmt in createScript.Split(";", StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
                             .Where(s => s.Contains($"\"{table}\"") && (s.StartsWith("CREATE TABLE") || s.StartsWith("CREATE UNIQUE INDEX") || s.StartsWith("CREATE INDEX"))))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = stmt;
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
