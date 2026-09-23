using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Data.Sqlite;
using PerformanceCI;

namespace PerformanceCI.Integration;

/// <summary>
/// Measures raw SQLite (via Microsoft.Data.Sqlite — a NuGet package, the only external
/// dependency permitted) in isolation: schema creation, writes, and reads. No reference
/// to any other project; fully self-contained.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
[PerfCritical(250_000)] // 250 µs class-level budget for the I/O-bound suite
[PerfAllocation(50_000)]
public class SqliteVocabularyBenchmarks
{
    private string _databasePath = string.Empty;
    private SqliteConnection _connection = null!;

    /// <summary>Creates an isolated temporary database for the benchmark run.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pci-bench-{Guid.NewGuid():N}.db3");
        _connection = new SqliteConnection($"Data Source={_databasePath}");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS tiles (id TEXT PRIMARY KEY, label TEXT, icon TEXT, category TEXT)";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Drops the temporary database after the benchmark run.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
        if (System.IO.File.Exists(_databasePath))
            System.IO.File.Delete(_databasePath);
    }

    /// <summary>Measures an insert into a SQLite table.</summary>
    [Benchmark]
    [PerfCritical(150_000)]
    [PerfAllocation(20_000)]
    public void InsertTile()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO tiles (id, label, icon, category) VALUES (@id, @label, @icon, @cat)";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@label", "Benchmark tile");
        cmd.Parameters.AddWithValue("@icon", "bolt");
        cmd.Parameters.AddWithValue("@cat", "performance");
        cmd.ExecuteNonQuery();
    }

    /// <summary>Measures a filtered read of rows by category.</summary>
    [Benchmark]
    public int ReadByCategory()
    {
        int count = 0;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT label FROM tiles WHERE category = @cat";
        cmd.Parameters.AddWithValue("@cat", "performance");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            count++;
        return count;
    }
}