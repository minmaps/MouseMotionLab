using Microsoft.Data.Sqlite;
using MouseMotionLab.Core.Analysis;
using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Core.Data;

public sealed class DatasetRepository
{
    private const int SchemaVersion = 3;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public DatasetRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "SELECT version FROM schema_info LIMIT 1;";
            var version = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (version is < 1 or > SchemaVersion)
            {
                throw new InvalidDataException(
                    $"Dataset schema version {version} is not supported by this application ({SchemaVersion}).");
            }

            await EnsureTrialDiagnosticsColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureCursorSampleColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version < SchemaVersion)
            {
                await using var migrateVersion = connection.CreateCommand();
                migrateVersion.CommandText = "UPDATE schema_info SET version = $version;";
                migrateVersion.Parameters.AddWithValue("$version", SchemaVersion);
                await migrateVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task UpsertProfileAsync(CaptureProfile profile, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO profiles (
                    id, device_id, display_name, dpi, nominal_polling_hz,
                    display_pixels_per_mm, created_utc)
                VALUES (
                    $id, $deviceId, $displayName, $dpi, $pollingHz,
                    $displayScale, $createdUtc)
                ON CONFLICT(id) DO UPDATE SET
                    device_id = excluded.device_id,
                    display_name = excluded.display_name,
                    dpi = excluded.dpi,
                    nominal_polling_hz = excluded.nominal_polling_hz,
                    display_pixels_per_mm = excluded.display_pixels_per_mm;
                """;
            command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
            command.Parameters.AddWithValue("$deviceId", profile.DeviceId);
            command.Parameters.AddWithValue("$displayName", profile.DisplayName);
            command.Parameters.AddWithValue("$dpi", profile.Dpi);
            command.Parameters.AddWithValue("$pollingHz", profile.NominalPollingHz);
            command.Parameters.AddWithValue("$displayScale", profile.DisplayPixelsPerMillimeter);
            command.Parameters.AddWithValue("$createdUtc", profile.CreatedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task CreateSessionAsync(CaptureSession session, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sessions (
                    id, profile_id, started_utc, app_version, os_version, qpc_frequency)
                VALUES ($id, $profileId, $startedUtc, $appVersion, $osVersion, $qpcFrequency);
                """;
            command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
            command.Parameters.AddWithValue("$profileId", session.ProfileId.ToString("D"));
            command.Parameters.AddWithValue("$startedUtc", session.StartedUtc.ToString("O"));
            command.Parameters.AddWithValue("$appVersion", session.AppVersion);
            command.Parameters.AddWithValue("$osVersion", session.OsVersion);
            command.Parameters.AddWithValue("$qpcFrequency", session.QpcFrequency);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SaveTrialAsync(TrialCapture trial, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();

            await using (var trialCommand = connection.CreateCommand())
            {
                trialCommand.Transaction = transaction;
                trialCommand.CommandText = """
                    INSERT INTO trials (
                        id, session_id, ordinal, armed_qpc, first_motion_qpc, terminal_qpc,
                        target_dx, target_dy, path_length_counts, duration_us,
                        effective_packet_hz, event_count, median_interval_us, max_interval_us,
                        pause_count, status, rejection_reason, created_utc)
                    VALUES (
                        $id, $sessionId, $ordinal, $armedQpc, $firstMotionQpc, $terminalQpc,
                        $targetDx, $targetDy, $pathLength, $durationUs,
                        $effectiveHz, $eventCount, $medianIntervalUs, $maxIntervalUs,
                        $pauseCount, $status, $reason, $createdUtc);
                    """;
                trialCommand.Parameters.AddWithValue("$id", trial.Id.ToString("D"));
                trialCommand.Parameters.AddWithValue("$sessionId", trial.SessionId.ToString("D"));
                trialCommand.Parameters.AddWithValue("$ordinal", trial.Ordinal);
                trialCommand.Parameters.AddWithValue("$armedQpc", trial.ArmedQpc);
                trialCommand.Parameters.AddWithValue("$firstMotionQpc", trial.FirstMotionQpc);
                trialCommand.Parameters.AddWithValue("$terminalQpc", trial.TerminalQpc);
                trialCommand.Parameters.AddWithValue("$targetDx", trial.Metrics.TargetDx);
                trialCommand.Parameters.AddWithValue("$targetDy", trial.Metrics.TargetDy);
                trialCommand.Parameters.AddWithValue("$pathLength", trial.Metrics.PathLengthCounts);
                trialCommand.Parameters.AddWithValue("$durationUs", trial.Metrics.DurationMicroseconds);
                trialCommand.Parameters.AddWithValue("$effectiveHz", trial.Metrics.EffectivePacketHz);
                trialCommand.Parameters.AddWithValue("$eventCount", trial.Metrics.EventCount);
                trialCommand.Parameters.AddWithValue("$medianIntervalUs", trial.Metrics.MedianIntervalMicroseconds);
                trialCommand.Parameters.AddWithValue("$maxIntervalUs", trial.Metrics.MaximumIntervalMicroseconds);
                trialCommand.Parameters.AddWithValue("$pauseCount", trial.Metrics.PauseCount);
                trialCommand.Parameters.AddWithValue("$status", trial.Status.ToString().ToLowerInvariant());
                trialCommand.Parameters.AddWithValue("$reason", (object?)trial.RejectionReason ?? DBNull.Value);
                trialCommand.Parameters.AddWithValue("$createdUtc", trial.CreatedUtc.ToString("O"));
                await trialCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var eventCommand = connection.CreateCommand())
            {
                eventCommand.Transaction = transaction;
                eventCommand.CommandText = """
                    INSERT INTO events (
                        trial_id, sequence, qpc_ticks, dx, dy, wheel_delta, button_flags,
                        cursor_dx_pixels, cursor_dy_pixels)
                    VALUES (
                        $trialId, $sequence, $qpcTicks, $dx, $dy, $wheelDelta, $buttonFlags,
                        $cursorDxPixels, $cursorDyPixels);
                    """;
                var trialId = eventCommand.Parameters.Add("$trialId", SqliteType.Text);
                var sequence = eventCommand.Parameters.Add("$sequence", SqliteType.Integer);
                var qpcTicks = eventCommand.Parameters.Add("$qpcTicks", SqliteType.Integer);
                var dx = eventCommand.Parameters.Add("$dx", SqliteType.Integer);
                var dy = eventCommand.Parameters.Add("$dy", SqliteType.Integer);
                var wheelDelta = eventCommand.Parameters.Add("$wheelDelta", SqliteType.Integer);
                var buttonFlags = eventCommand.Parameters.Add("$buttonFlags", SqliteType.Integer);
                var cursorDxPixels = eventCommand.Parameters.Add("$cursorDxPixels", SqliteType.Integer);
                var cursorDyPixels = eventCommand.Parameters.Add("$cursorDyPixels", SqliteType.Integer);

                trialId.Value = trial.Id.ToString("D");
                for (var index = 0; index < trial.Events.Count; index++)
                {
                    var item = trial.Events[index];
                    sequence.Value = index;
                    qpcTicks.Value = item.QpcTicks;
                    dx.Value = item.Dx;
                    dy.Value = item.Dy;
                    wheelDelta.Value = item.WheelDelta;
                    buttonFlags.Value = (int)item.ButtonFlags;
                    cursorDxPixels.Value = (object?)item.CursorDxPixels ?? DBNull.Value;
                    cursorDyPixels.Value = (object?)item.CursorDyPixels ?? DBNull.Value;
                    await eventCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SetTrialStatusAsync(
        Guid trialId,
        TrialStatus status,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE trials
                SET status = $status, rejection_reason = $reason
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", trialId.ToString("D"));
            command.Parameters.AddWithValue("$status", status.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<(int Dx, int Dy)>> GetAcceptedEndpointsAsync(
        int limit = 25_000,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target_dx, target_dy
            FROM trials
            WHERE status = 'accepted'
            ORDER BY created_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<(int Dx, int Dy)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return result;
    }

    public async Task<SpeedCurveSummary> GetSpeedCurvesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.first_motion_qpc, t.terminal_qpc, s.qpc_frequency, p.dpi,
                   e.qpc_ticks, e.dx, e.dy
            FROM trials t
            JOIN sessions s ON s.id = t.session_id
            JOIN profiles p ON p.id = s.profile_id
            LEFT JOIN events e ON e.trial_id = t.id
            WHERE t.status = 'accepted'
            ORDER BY t.id, e.sequence;
            """;

        var accumulator = new SpeedCurveAccumulator();
        var events = new List<SpeedCurveEvent>();
        string? trialId = null;
        long firstMotionQpc = 0;
        long terminalQpc = 0;
        long qpcFrequency = 0;
        var dpi = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextTrialId = reader.GetString(0);
            if (nextTrialId != trialId)
            {
                if (trialId is not null)
                {
                    accumulator.AddTrial(firstMotionQpc, terminalQpc, qpcFrequency, dpi, events);
                    events.Clear();
                }

                trialId = nextTrialId;
                firstMotionQpc = reader.GetInt64(1);
                terminalQpc = reader.GetInt64(2);
                qpcFrequency = reader.GetInt64(3);
                dpi = reader.GetInt32(4);
            }

            if (!reader.IsDBNull(5))
            {
                events.Add(new SpeedCurveEvent(reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7)));
            }
        }

        if (trialId is not null)
        {
            accumulator.AddTrial(firstMotionQpc, terminalQpc, qpcFrequency, dpi, events);
        }

        return accumulator.GetSummary();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task EnsureTrialDiagnosticsColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(trials);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }
        }

        var additions = new (string Name, string Sql)[]
        {
            ("median_interval_us", "ALTER TABLE trials ADD COLUMN median_interval_us REAL NOT NULL DEFAULT 0;"),
            ("max_interval_us", "ALTER TABLE trials ADD COLUMN max_interval_us REAL NOT NULL DEFAULT 0;"),
            ("pause_count", "ALTER TABLE trials ADD COLUMN pause_count INTEGER NOT NULL DEFAULT 0;")
        };
        foreach (var addition in additions)
        {
            if (columns.Contains(addition.Name))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = addition.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureCursorSampleColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(events);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }
        }

        var additions = new (string Name, string Sql)[]
        {
            ("cursor_dx_pixels", "ALTER TABLE events ADD COLUMN cursor_dx_pixels INTEGER NULL;"),
            ("cursor_dy_pixels", "ALTER TABLE events ADD COLUMN cursor_dy_pixels INTEGER NULL;")
        };
        foreach (var addition in additions)
        {
            if (columns.Contains(addition.Name))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = addition.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_info (
            version INTEGER NOT NULL
        );

        INSERT INTO schema_info(version)
        SELECT 3
        WHERE NOT EXISTS (SELECT 1 FROM schema_info);

        CREATE TABLE IF NOT EXISTS profiles (
            id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            dpi INTEGER NOT NULL CHECK (dpi > 0),
            nominal_polling_hz INTEGER NOT NULL CHECK (nominal_polling_hz > 0),
            display_pixels_per_mm REAL NOT NULL CHECK (display_pixels_per_mm > 0),
            created_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sessions (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL REFERENCES profiles(id),
            started_utc TEXT NOT NULL,
            app_version TEXT NOT NULL,
            os_version TEXT NOT NULL,
            qpc_frequency INTEGER NOT NULL CHECK (qpc_frequency > 0)
        );

        CREATE TABLE IF NOT EXISTS trials (
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES sessions(id),
            ordinal INTEGER NOT NULL,
            armed_qpc INTEGER NOT NULL,
            first_motion_qpc INTEGER NOT NULL,
            terminal_qpc INTEGER NOT NULL,
            target_dx INTEGER NOT NULL,
            target_dy INTEGER NOT NULL,
            path_length_counts REAL NOT NULL,
            duration_us REAL NOT NULL,
            effective_packet_hz REAL NOT NULL,
            event_count INTEGER NOT NULL,
            median_interval_us REAL NOT NULL DEFAULT 0,
            max_interval_us REAL NOT NULL DEFAULT 0,
            pause_count INTEGER NOT NULL DEFAULT 0,
            status TEXT NOT NULL,
            rejection_reason TEXT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_trials_session ON trials(session_id, ordinal);
        CREATE INDEX IF NOT EXISTS ix_trials_status ON trials(status);

        CREATE TABLE IF NOT EXISTS events (
            trial_id TEXT NOT NULL REFERENCES trials(id) ON DELETE CASCADE,
            sequence INTEGER NOT NULL,
            qpc_ticks INTEGER NOT NULL,
            dx INTEGER NOT NULL,
            dy INTEGER NOT NULL,
            wheel_delta INTEGER NOT NULL,
            button_flags INTEGER NOT NULL,
            cursor_dx_pixels INTEGER NULL,
            cursor_dy_pixels INTEGER NULL,
            PRIMARY KEY (trial_id, sequence)
        );
        """;
}

