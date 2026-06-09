using Microsoft.Data.Sqlite;
using System.IO;
using System.Text;
using VMS_SUSA.Models;

namespace VMS_SUSA.Repositories;

public sealed class SqliteSerialItemRepository : ISerialItemRepository
{
    private const int SchemaVersion = 1;
    private const string DatabaseFileName = "printer_data.db";
    private const int InsertBatchSize = 100;

    private readonly SemaphoreSlim _sync = new(1, 1);

    public SqliteSerialItemRepository()
    {
        DataLogsFolderPath = Path.Combine(AppContext.BaseDirectory, "Data Logs");
        Directory.CreateDirectory(DataLogsFolderPath);
        DatabasePath = Path.Combine(DataLogsFolderPath, DatabaseFileName);
    }

    public string DataLogsFolderPath { get; }

    public string DatabasePath { get; }

    public async Task InitializeAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var commands = new[]
            {
                """
                PRAGMA journal_mode = WAL;
                """,
                """
                PRAGMA synchronous = NORMAL;
                """,
                """
                CREATE TABLE IF NOT EXISTS schema_info (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    version INTEGER NOT NULL
                );
                """,
                """
                INSERT INTO schema_info (id, version)
                VALUES (1, 1)
                ON CONFLICT(id) DO UPDATE SET version = excluded.version;
                """,
                """
                CREATE TABLE IF NOT EXISTS serial_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    display_index INTEGER NOT NULL,
                    serial TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    sent_at TEXT NULL,
                    printed_at TEXT NULL,
                    note TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_serial_items_display_index ON serial_items(display_index);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_serial_items_status ON serial_items(status);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_serial_items_serial ON serial_items(serial);
                """
            };

            foreach (var commandText in commands)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = commandText;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await EnsureSchemaVersionAsync(connection).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task ReplaceAllAsync(IEnumerable<SerialItem> items, IProgress<int>? progress = null)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM serial_items;";
                await deleteCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var batch = new List<SerialItem>(InsertBatchSize);
            var insertedCount = 0;
            foreach (var item in items)
            {
                batch.Add(item);
                if (batch.Count >= InsertBatchSize)
                {
                    insertedCount += batch.Count;
                    await InsertBatchAsync(connection, transaction, batch).ConfigureAwait(false);
                    progress?.Report(insertedCount);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                insertedCount += batch.Count;
                await InsertBatchAsync(connection, transaction, batch).ConfigureAwait(false);
                progress?.Report(insertedCount);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<List<SerialItem>> GetAllAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT display_index, serial, status, sent_at, printed_at, note
                FROM serial_items
                ORDER BY display_index ASC;
                """;

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            var items = new List<SerialItem>();
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                items.Add(ReadSerialItem(reader));
            }

            return items;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async IAsyncEnumerable<SerialItem> StreamAllAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT display_index, serial, status, sent_at, printed_at, note
                FROM serial_items
                ORDER BY display_index ASC;
                """;

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                yield return ReadSerialItem(reader);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async IAsyncEnumerable<SerialItem> StreamByStatusesAsync(IEnumerable<SerialStatus> statuses)
    {
        var statusList = statuses?.Distinct().ToArray() ?? Array.Empty<SerialStatus>();
        if (statusList.Length == 0)
        {
            yield break;
        }

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            var parameters = new List<string>();
            for (var i = 0; i < statusList.Length; i++)
            {
                var parameterName = $"@status{i}";
                parameters.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, (int)statusList[i]);
            }

            command.CommandText = $"""
                SELECT display_index, serial, status, sent_at, printed_at, note
                FROM serial_items
                WHERE status IN ({string.Join(", ", parameters)})
                ORDER BY display_index ASC;
                """;

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                yield return ReadSerialItem(reader);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<SerialItemPageResult> GetPageAsync(int pageNumber, int pageSize, string? searchText = null, SerialStatus? statusFilter = null)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Max(1, pageSize);
        var offset = (pageNumber - 1) * pageSize;

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var whereClause = BuildWhereClause(searchText, statusFilter);
            var countSql = $"SELECT COUNT(*) FROM serial_items {whereClause};";
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = countSql;
            BindFilters(countCommand, searchText, statusFilter);
            var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync().ConfigureAwait(false));

            var itemsSql = $"""
                SELECT display_index, serial, status, sent_at, printed_at, note
                FROM serial_items
                {whereClause}
                ORDER BY display_index ASC
                LIMIT @limit OFFSET @offset;
                """;

            await using var itemsCommand = connection.CreateCommand();
            itemsCommand.CommandText = itemsSql;
            BindFilters(itemsCommand, searchText, statusFilter);
            itemsCommand.Parameters.AddWithValue("@limit", pageSize);
            itemsCommand.Parameters.AddWithValue("@offset", offset);

            await using var reader = await itemsCommand.ExecuteReaderAsync().ConfigureAwait(false);
            var items = new List<SerialItem>();
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                items.Add(ReadSerialItem(reader));
            }

            return new SerialItemPageResult(items, totalCount, pageNumber, pageSize);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<SerialItemStatistics> GetStatisticsAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COUNT(*) AS total_count,
                    SUM(CASE WHEN status = 0 THEN 1 ELSE 0 END) AS waiting_count,
                    SUM(CASE WHEN status = 1 THEN 1 ELSE 0 END) AS sent_count,
                    SUM(CASE WHEN status = 2 THEN 1 ELSE 0 END) AS printed_count,
                    SUM(CASE WHEN status = 3 THEN 1 ELSE 0 END) AS error_count,
                    SUM(CASE WHEN status = 4 THEN 1 ELSE 0 END) AS duplicate_count,
                    SUM(CASE WHEN status = 5 THEN 1 ELSE 0 END) AS invalid_count
                FROM serial_items;
                """;

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                return new SerialItemStatistics(0, 0, 0, 0, 0, 0, 0);
            }

            return new SerialItemStatistics(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6));
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<SerialItem?> GetFirstByStatusAsync(SerialStatus status)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT display_index, serial, status, sent_at, printed_at, note
                FROM serial_items
                WHERE status = @status
                ORDER BY display_index ASC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@status", (int)status);

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                return null;
            }

            return ReadSerialItem(reader);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<SerialItem?> GetLastByStatusAsync(SerialStatus status)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT display_index, serial, status, sent_at, printed_at, note
                FROM serial_items
                WHERE status = @status
                ORDER BY display_index DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@status", (int)status);

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                return null;
            }

            return ReadSerialItem(reader);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<List<SerialItem>> GetByStatusesAsync(IEnumerable<SerialStatus> statuses)
    {
        var statusList = statuses?.Distinct().ToArray() ?? Array.Empty<SerialStatus>();
        if (statusList.Length == 0)
        {
            return new List<SerialItem>();
        }

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            var parameters = new List<string>();
            for (var i = 0; i < statusList.Length; i++)
            {
                var parameterName = $"@status{i}";
                parameters.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, (int)statusList[i]);
            }

            command.CommandText = $"""
                SELECT display_index, serial, status, sent_at, printed_at, note
                FROM serial_items
                WHERE status IN ({string.Join(", ", parameters)})
                ORDER BY display_index ASC;
                """;

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            var items = new List<SerialItem>();
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                items.Add(ReadSerialItem(reader));
            }

            return items;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task RecalculateDuplicateStatusesAsync(int serialLength)
    {
        serialLength = Math.Max(1, serialLength);

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                WITH ranked AS (
                    SELECT
                        id,
                        status,
                        note,
                        CASE WHEN LENGTH(TRIM(serial)) = @serial_length THEN 1 ELSE 0 END AS is_valid_length,
                        ROW_NUMBER() OVER (PARTITION BY TRIM(serial) ORDER BY display_index ASC) AS rn
                    FROM serial_items
                    WHERE status NOT IN (@error_status, @invalid_status)
                ),
                computed AS (
                    SELECT
                        id,
                        CASE
                            WHEN is_valid_length = 0 THEN @invalid_status
                            WHEN rn > 1 THEN @duplicate_status
                            WHEN status = @duplicate_status THEN @waiting_status
                            ELSE status
                        END AS new_status,
                        CASE
                            WHEN is_valid_length = 0 THEN @invalid_note
                            WHEN rn > 1 THEN @duplicate_note
                            WHEN status = @duplicate_status THEN ''
                            ELSE note
                        END AS new_note
                    FROM ranked
                )
                UPDATE serial_items
                SET
                    status = (SELECT new_status FROM computed WHERE computed.id = serial_items.id),
                    note = (SELECT new_note FROM computed WHERE computed.id = serial_items.id),
                    updated_at = datetime('now')
                WHERE id IN (SELECT id FROM computed)
                  AND (
                    status <> (SELECT new_status FROM computed WHERE computed.id = serial_items.id)
                    OR COALESCE(note, '') <> COALESCE((SELECT new_note FROM computed WHERE computed.id = serial_items.id), '')
                  );
                """;
            command.Parameters.AddWithValue("@serial_length", serialLength);
            command.Parameters.AddWithValue("@error_status", (int)SerialStatus.Error);
            command.Parameters.AddWithValue("@invalid_status", (int)SerialStatus.Invalid);
            command.Parameters.AddWithValue("@duplicate_status", (int)SerialStatus.Duplicate);
            command.Parameters.AddWithValue("@waiting_status", (int)SerialStatus.Waiting);
            command.Parameters.AddWithValue("@invalid_note", "Sai độ dài");
            command.Parameters.AddWithValue("@duplicate_note", "Serial trùng");
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task UpdateAsync(SerialItem item)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE serial_items
                SET serial = @serial,
                    status = @status,
                    sent_at = @sent_at,
                    printed_at = @printed_at,
                    note = @note,
                    updated_at = @updated_at
                WHERE display_index = @display_index;
                """;
            command.Parameters.AddWithValue("@display_index", item.Index);
            command.Parameters.AddWithValue("@serial", item.Serial ?? string.Empty);
            command.Parameters.AddWithValue("@status", (int)item.Status);
            command.Parameters.AddWithValue("@sent_at", ToDbValue(item.SentAt));
            command.Parameters.AddWithValue("@printed_at", ToDbValue(item.PrintedAt));
            command.Parameters.AddWithValue("@note", item.Note ?? string.Empty);
            command.Parameters.AddWithValue("@updated_at", DateTime.Now.ToString("O"));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task UpdateRangeAsync(IEnumerable<SerialItem> items)
    {
        var itemList = items?.ToList() ?? new List<SerialItem>();
        if (itemList.Count == 0)
        {
            return;
        }

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE serial_items
                SET serial = @serial,
                    status = @status,
                    sent_at = @sent_at,
                    printed_at = @printed_at,
                    note = @note,
                    updated_at = @updated_at
                WHERE display_index = @display_index;
                """;

            var displayIndex = command.CreateParameter();
            displayIndex.ParameterName = "@display_index";
            command.Parameters.Add(displayIndex);

            var serial = command.CreateParameter();
            serial.ParameterName = "@serial";
            command.Parameters.Add(serial);

            var status = command.CreateParameter();
            status.ParameterName = "@status";
            command.Parameters.Add(status);

            var sentAt = command.CreateParameter();
            sentAt.ParameterName = "@sent_at";
            command.Parameters.Add(sentAt);

            var printedAt = command.CreateParameter();
            printedAt.ParameterName = "@printed_at";
            command.Parameters.Add(printedAt);

            var note = command.CreateParameter();
            note.ParameterName = "@note";
            command.Parameters.Add(note);

            var updatedAt = command.CreateParameter();
            updatedAt.ParameterName = "@updated_at";
            command.Parameters.Add(updatedAt);

            command.Prepare();

            foreach (var item in itemList)
            {
                displayIndex.Value = item.Index;
                serial.Value = item.Serial ?? string.Empty;
                status.Value = (int)item.Status;
                sentAt.Value = ToDbValue(item.SentAt);
                printedAt.Value = ToDbValue(item.PrintedAt);
                note.Value = item.Note ?? string.Empty;
                updatedAt.Value = DateTime.Now.ToString("O");
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task DeleteAllAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM serial_items;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task EnsureSchemaVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
        if (version < SchemaVersion)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = "UPDATE schema_info SET version = @version WHERE id = 1;";
            updateCommand.Parameters.AddWithValue("@version", SchemaVersion);
            await updateCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private SqliteConnection CreateConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    private static string BuildWhereClause(string? searchText, SerialStatus? statusFilter)
    {
        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            clauses.Add("serial LIKE @search ESCAPE '\\'");
        }

        if (statusFilter.HasValue)
        {
            clauses.Add("status = @status");
        }

        return clauses.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", clauses)}";
    }

    private static void BindFilters(SqliteCommand command, string? searchText, SerialStatus? statusFilter)
    {
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            command.Parameters.AddWithValue("@search", $"%{EscapeLike(searchText.Trim())}%");
        }

        if (statusFilter.HasValue)
        {
            command.Parameters.AddWithValue("@status", (int)statusFilter.Value);
        }
    }

    private static string EscapeLike(string input)
    {
        return input
            .Replace(@"\", @"\\")
            .Replace("%", @"\%")
            .Replace("_", @"\_");
    }

    private static SerialItem ReadSerialItem(SqliteDataReader reader)
    {
        return new SerialItem
        {
            Index = reader.GetInt32(0),
            Serial = reader.GetString(1),
            Status = (SerialStatus)reader.GetInt32(2),
            SentAt = ReadDateTime(reader, 3),
            PrintedAt = ReadDateTime(reader, 4),
            Note = reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
        };
    }

    private static DateTime? ReadDateTime(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var text = reader.GetString(ordinal);
        return DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private static object ToDbValue(DateTime? value)
    {
        return value.HasValue ? value.Value.ToString("O") : DBNull.Value;
    }

    private static async Task InsertBatchAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<SerialItem> batch)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        var sql = new StringBuilder();
        sql.Append("""
            INSERT INTO serial_items (
                display_index,
                serial,
                status,
                sent_at,
                printed_at,
                note,
                created_at,
                updated_at
            )
            VALUES
            """);

        var nowText = DateTime.Now.ToString("O");
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0)
            {
                sql.AppendLine(",");
            }

            sql.AppendLine($"(@display_index{i}, @serial{i}, @status{i}, @sent_at{i}, @printed_at{i}, @note{i}, @created_at{i}, @updated_at{i})");

            var item = batch[i];
            command.Parameters.AddWithValue($"@display_index{i}", item.Index);
            command.Parameters.AddWithValue($"@serial{i}", item.Serial ?? string.Empty);
            command.Parameters.AddWithValue($"@status{i}", (int)item.Status);
            command.Parameters.AddWithValue($"@sent_at{i}", ToDbValue(item.SentAt));
            command.Parameters.AddWithValue($"@printed_at{i}", ToDbValue(item.PrintedAt));
            command.Parameters.AddWithValue($"@note{i}", item.Note ?? string.Empty);
            command.Parameters.AddWithValue($"@created_at{i}", nowText);
            command.Parameters.AddWithValue($"@updated_at{i}", nowText);
        }

        sql.Append(';');
        command.CommandText = sql.ToString();
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
