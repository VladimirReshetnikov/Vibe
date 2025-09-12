using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Vibe.Gui;

internal static class DecompiledCodeCache
{
    private static readonly string DbPath;

    static DecompiledCodeCache()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(baseDir, "Vibe");
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "decompiled.db");

        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS cache (
                                file_hash TEXT NOT NULL,
                                func_name TEXT NOT NULL,
                                code TEXT NOT NULL,
                                PRIMARY KEY(file_hash, func_name)
                            );";
        cmd.ExecuteNonQuery();
    }

    public static string? TryGet(string fileHash, string funcName)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT code FROM cache WHERE file_hash = @h AND func_name = @f";
        cmd.Parameters.AddWithValue("@h", fileHash);
        cmd.Parameters.AddWithValue("@f", funcName);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }

    public static void Save(string fileHash, string funcName, string code)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO cache(file_hash, func_name, code) VALUES(@h,@f,@c)";
        cmd.Parameters.AddWithValue("@h", fileHash);
        cmd.Parameters.AddWithValue("@f", funcName);
        cmd.Parameters.AddWithValue("@c", code);
        cmd.ExecuteNonQuery();
    }
}
