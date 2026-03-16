using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using NewSanofi.ClassHelper;
using System.Data.SQLite;

namespace NewSanofi
{
    public class SqliteCom
    {
        private static readonly Lazy<SqliteCom> LazyInstance = new Lazy<SqliteCom>(() => new SqliteCom());
        private string connectionString;

        private SqliteCom()
        {
            SetConnection();
        }

        public static SqliteCom Instance
        {
            get { return LazyInstance.Value; }
        }

        public void SetConnection()
        {
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserDB.db");
            connectionString = "Data Source=" + dbPath + ";Version=3;";
        }

        public void CreateDatabase()
        {
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserDB.db");
            if (!File.Exists(dbPath))
            {
                SQLiteConnection.CreateFile(dbPath);
            }

            const string sql = "CREATE TABLE IF NOT EXISTS [User]([ID] INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, UserName TEXT NULL, Password TEXT NULL, Type TEXT NULL, Date TEXT NULL)";
            ExecuteNonQuery(sql);
        }

        public void CreateTable(string tableName, List<TableType> columns)
        {
            string columnSql = string.Join(", ", columns.Select(column =>
                string.Format("{0} {1} {2}", column.ColumnName, column.DataType, column.AllowNull ? "NULL" : "NOT NULL")));
            ExecuteNonQuery("CREATE TABLE IF NOT EXISTS [" + tableName + "](" + columnSql + ")");
        }

        public void CloneTable(string sourceTable, string targetTable)
        {
            ExecuteNonQuery("CREATE TABLE IF NOT EXISTS [" + targetTable + "] AS SELECT * FROM [" + sourceTable + "] WHERE 1 = 0");
        }

        public void DeleteAllRows(string tableName)
        {
            ExecuteNonQuery("DELETE FROM [" + tableName + "]");
        }

        public void DeleteAllTables()
        {
            foreach (string table in GetAllNameTable().Where(name => !string.Equals(name, "sqlite_sequence", StringComparison.OrdinalIgnoreCase)))
            {
                DeleteTable(table);
            }
        }

        public void DeleteRows(string tableName, string whereColumn, string whereValue)
        {
            using (var connection = CreateConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM [" + tableName + "] WHERE [" + whereColumn + "] = @value";
                command.Parameters.AddWithValue("@value", whereValue);
                command.ExecuteNonQuery();
            }
        }

        public void DeleteTable(string tableName)
        {
            ExecuteNonQuery("DROP TABLE IF EXISTS [" + tableName + "]");
        }

        public List<string> GetAllNameTable()
        {
            var names = new List<string>();
            using (var connection = CreateConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        names.Add(reader.GetString(0));
                    }
                }
            }

            return names;
        }

        public DataTable GetSchemaTable(string tableName)
        {
            using (var connection = CreateConnection())
            using (var adapter = new SQLiteDataAdapter("SELECT * FROM [" + tableName + "] LIMIT 0", connection))
            {
                var table = new DataTable();
                adapter.FillSchema(table, SchemaType.Source);
                return table;
            }
        }

        public void InsertRow(string tableName, List<string> values)
        {
            var parameterNames = values.Select((_, index) => "@p" + index).ToList();
            using (var connection = CreateConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO [" + tableName + "] VALUES (NULL, " + string.Join(", ", parameterNames) + ")";
                for (int i = 0; i < values.Count; i++)
                {
                    command.Parameters.AddWithValue(parameterNames[i], values[i]);
                }

                command.ExecuteNonQuery();
            }
        }

        public DataTable LoadTable(string tableName)
        {
            return LoadTable(tableName, string.Empty);
        }

        public DataTable LoadTable(string tableName, string whereClause)
        {
            string sql = "SELECT * FROM [" + tableName + "]";
            if (!string.IsNullOrWhiteSpace(whereClause))
            {
                sql += " WHERE " + whereClause;
            }

            using (var connection = CreateConnection())
            using (var adapter = new SQLiteDataAdapter(sql, connection))
            {
                var table = new DataTable();
                adapter.Fill(table);
                return table;
            }
        }

        public void UpdateTable(string tableName, List<string> columns, List<string> values, string whereColumn, string whereValue)
        {
            using (var connection = CreateConnection())
            using (var command = connection.CreateCommand())
            {
                var assignments = new List<string>();
                for (int i = 0; i < columns.Count; i++)
                {
                    assignments.Add("[" + columns[i] + "] = @p" + i);
                    command.Parameters.AddWithValue("@p" + i, values[i]);
                }

                command.Parameters.AddWithValue("@where", whereValue);
                command.CommandText = "UPDATE [" + tableName + "] SET " + string.Join(", ", assignments) + " WHERE [" + whereColumn + "] = @where";
                command.ExecuteNonQuery();
            }
        }

        private SQLiteConnection CreateConnection()
        {
            SetConnection();
            var connection = new SQLiteConnection(connectionString);
            connection.Open();
            return connection;
        }

        private void ExecuteNonQuery(string sql)
        {
            using (var connection = CreateConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }
    }
}
