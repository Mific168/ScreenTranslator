using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Data.Sqlite;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services;

/// <summary>История последних 50 переводов в SQLite (%AppData%\ScreenTranslator\history.db).</summary>
public sealed class HistoryService
{
    public const int MaxEntries = 50;

    private readonly string _dbPath = Path.Combine(SettingsService.AppDataFolder, "history.db");
    private readonly object _sync = new();
    private bool _schemaReady;

    /// <summary>История изменилась (вызывается в UI-потоке).</summary>
    public event Action? Changed;

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(SettingsService.AppDataFolder);
        // Без пула соединений файл не держится открытым, пока приложение в фоне.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString());
        connection.Open();

        if (!_schemaReady)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS history (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    created_at      INTEGER NOT NULL,
                    kind            INTEGER NOT NULL,
                    source_text     TEXT    NOT NULL,
                    translated_text TEXT    NOT NULL,
                    source_lang     TEXT,
                    target_lang     TEXT    NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
            _schemaReady = true;
        }
        return connection;
    }

    /// <summary>Добавляет запись в фоне, если история включена.</summary>
    public void Add(HistoryEntry entry)
    {
        if (!SettingsService.Current.HistoryEnabled) return;
        if (string.IsNullOrWhiteSpace(entry.SourceText)) return;

        Task.Run(() =>
        {
            try
            {
                lock (_sync)
                {
                    using var connection = Open();
                    using var transaction = connection.BeginTransaction();

                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = """
                            INSERT INTO history (created_at, kind, source_text, translated_text, source_lang, target_lang)
                            VALUES ($created, $kind, $source, $translated, $sourceLang, $targetLang);
                            """;
                        insert.Parameters.AddWithValue("$created", entry.CreatedAt.ToUniversalTime().Ticks);
                        insert.Parameters.AddWithValue("$kind", (int)entry.Kind);
                        insert.Parameters.AddWithValue("$source", entry.SourceText);
                        insert.Parameters.AddWithValue("$translated", entry.TranslatedText ?? "");
                        insert.Parameters.AddWithValue("$sourceLang", (object?)entry.SourceLang ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$targetLang", entry.TargetLang);
                        insert.ExecuteNonQuery();
                    }

                    // Оставляем только последние MaxEntries записей.
                    using (var trim = connection.CreateCommand())
                    {
                        trim.Transaction = transaction;
                        trim.CommandText = "DELETE FROM history WHERE id NOT IN (SELECT id FROM history ORDER BY id DESC LIMIT $max);";
                        trim.Parameters.AddWithValue("$max", MaxEntries);
                        trim.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                RaiseChanged();
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось сохранить запись истории", ex);
            }
        });
    }

    public Task<List<HistoryEntry>> GetAllAsync() => Task.Run(() =>
    {
        var list = new List<HistoryEntry>();
        try
        {
            lock (_sync)
            {
                if (!File.Exists(_dbPath)) return list;
                using var connection = Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT id, created_at, kind, source_text, translated_text, source_lang, target_lang
                    FROM history ORDER BY id DESC LIMIT $max;
                    """;
                cmd.Parameters.AddWithValue("$max", MaxEntries);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new HistoryEntry
                    {
                        Id = reader.GetInt64(0),
                        CreatedAt = new DateTime(reader.GetInt64(1), DateTimeKind.Utc).ToLocalTime(),
                        Kind = (HistoryKind)reader.GetInt32(2),
                        SourceText = reader.GetString(3),
                        TranslatedText = reader.GetString(4),
                        SourceLang = reader.IsDBNull(5) ? null : reader.GetString(5),
                        TargetLang = reader.GetString(6)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось прочитать историю", ex);
        }
        return list;
    });

    public bool Clear()
    {
        try
        {
            lock (_sync)
            {
                if (File.Exists(_dbPath))
                {
                    using var connection = Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "DELETE FROM history; VACUUM;";
                    cmd.ExecuteNonQuery();
                }
            }
            RaiseChanged();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось очистить историю", ex);
            return false;
        }
    }

    private void RaiseChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(() => Changed?.Invoke());
    }
}
