using LlamaLauncher.Application.Benchmarks;
using LlamaLauncher.Domain.Benchmarks;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Sqlite;

/// <summary>基准测试运行记录的 SQLite 仓储（摘要查询不加载样本明细）。</summary>
public sealed class SqliteBenchmarkRunStore : IBenchmarkRunStore
{
    private const string SummaryColumns =
        "RunId, InstanceId, InstanceNameSnapshot, ModelNameSnapshot, VersionLabel, " +
        "StartedUtc, EndedUtc, DurationMs, State, ErrorSummary";

    private readonly SqliteDatabase _database;
    private readonly ILogger<SqliteBenchmarkRunStore> _logger;

    public SqliteBenchmarkRunStore(SqliteDatabase database, ILogger<SqliteBenchmarkRunStore> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task UpsertAsync(BenchmarkRunRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BenchmarkRuns(RunId, InstanceId, InstanceNameSnapshot, ModelNameSnapshot,
                                      ProfileNameSnapshot, VersionLabel, AppVersion,
                                      StartedUtc, EndedUtc, DurationMs, State,
                                      OptionsJson, SamplesJson, ErrorSummary)
            VALUES($runId, $instanceId, $instanceName, $modelName,
                   $profileName, $versionLabel, $appVersion,
                   $started, $ended, $duration, $state,
                   $options, $samples, $error)
            ON CONFLICT(RunId) DO UPDATE SET
                EndedUtc = excluded.EndedUtc,
                DurationMs = excluded.DurationMs,
                State = excluded.State,
                OptionsJson = excluded.OptionsJson,
                SamplesJson = excluded.SamplesJson,
                ErrorSummary = excluded.ErrorSummary;
            """;
        command.Parameters.AddWithValue("$runId", record.RunId);
        command.Parameters.AddWithValue("$instanceId", record.InstanceId.ToString("D"));
        command.Parameters.AddWithValue("$instanceName", record.InstanceNameSnapshot);
        command.Parameters.AddNullable("$modelName", record.ModelNameSnapshot);
        command.Parameters.AddNullable("$profileName", record.ProfileNameSnapshot);
        command.Parameters.AddNullable("$versionLabel", record.VersionLabel);
        command.Parameters.AddWithValue("$appVersion", record.AppVersion);
        command.Parameters.AddWithValue("$started", SqliteMapping.FormatDate(record.StartedUtc));
        command.Parameters.AddNullable("$ended", SqliteMapping.FormatDateOrNull(record.EndedUtc));
        command.Parameters.AddNullable("$duration", record.DurationMs);
        command.Parameters.AddWithValue("$state", (int)record.State);
        command.Parameters.AddWithValue(
            "$options",
            SqliteMapping.SerializeOrNull(record.Options) ?? "{}");
        command.Parameters.AddWithValue(
            "$samples",
            SqliteMapping.SerializeOrNull(record.Samples) ?? "[]");
        command.Parameters.AddNullable("$error", record.ErrorSummary);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BenchmarkRunSummary>> ListAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SummaryColumns} FROM BenchmarkRuns ORDER BY StartedUtc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<BenchmarkRunSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new BenchmarkRunSummary
            {
                RunId = reader.GetString(0),
                InstanceId = Guid.Parse(reader.GetString(1)),
                InstanceNameSnapshot = reader.GetString(2),
                ModelNameSnapshot = reader.GetStringOrNull(3),
                VersionLabel = reader.GetStringOrNull(4),
                StartedUtc = reader.GetDate(5),
                EndedUtc = reader.GetDateOrNull(6),
                DurationMs = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                State = (BenchmarkRunState)reader.GetInt32(8),
                ErrorSummary = reader.GetStringOrNull(9),
            });
        }

        return results;
    }

    public async Task<BenchmarkRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RunId, InstanceId, InstanceNameSnapshot, ModelNameSnapshot, ProfileNameSnapshot,
                   VersionLabel, AppVersion, StartedUtc, EndedUtc, DurationMs, State,
                   OptionsJson, SamplesJson, ErrorSummary
            FROM BenchmarkRuns WHERE RunId = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var options = SqliteMapping.DeserializeOrNull<BenchmarkOptions>(reader.GetStringOrNull(11))
            ?? new BenchmarkOptions();
        var samples = SqliteMapping.DeserializeOrNull<List<BenchmarkSample>>(reader.GetStringOrNull(12))
            ?? new List<BenchmarkSample>();

        return new BenchmarkRunRecord
        {
            RunId = reader.GetString(0),
            InstanceId = Guid.Parse(reader.GetString(1)),
            InstanceNameSnapshot = reader.GetString(2),
            ModelNameSnapshot = reader.GetStringOrNull(3),
            ProfileNameSnapshot = reader.GetStringOrNull(4),
            VersionLabel = reader.GetStringOrNull(5),
            AppVersion = reader.GetString(6),
            StartedUtc = reader.GetDate(7),
            EndedUtc = reader.GetDateOrNull(8),
            DurationMs = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            State = (BenchmarkRunState)reader.GetInt32(10),
            Options = options,
            Samples = samples,
            ErrorSummary = reader.GetStringOrNull(13),
        };
    }

    public async Task<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return false;
        }

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM BenchmarkRuns WHERE RunId = $runId;";
        command.Parameters.AddWithValue("$runId", runId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected > 0)
        {
            _logger.LogInformation("已删除基准测试记录 {RunId}", runId);
        }

        return affected > 0;
    }
}
