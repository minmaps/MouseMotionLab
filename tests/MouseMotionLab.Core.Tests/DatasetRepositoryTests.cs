using MouseMotionLab.Core.Data;
using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Core.Tests;

public sealed class DatasetRepositoryTests
{
    [Fact]
    public async Task SpeedCurvesReadAllAcceptedTrialsAndRespectQueuedExclusions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mousemotionlab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new DatasetRepository(Path.Combine(directory, "dataset.db"));
            await repository.InitializeAsync();
            Assert.Empty((await repository.GetSpeedCurvesAsync()).Points);
            var profile = new CaptureProfile(Guid.NewGuid(), "device", "Test", 254, 1_000, 1, DateTimeOffset.UtcNow);
            await repository.UpsertProfileAsync(profile);
            var session = new CaptureSession(Guid.NewGuid(), profile.Id, DateTimeOffset.UtcNow, "test", "windows", 1_000);
            await repository.CreateSessionAsync(session);

            TrialCapture CreateTrial(int ordinal, int delta, TrialStatus status) => TrialCapture.Create(
                Guid.NewGuid(), session.Id, ordinal, 0, 10, 30, status, null,
                [
                    new(10, 1, 0, 0, MouseButtonFlags.None, "device"),
                    new(20, delta, 0, 0, MouseButtonFlags.None, "device"),
                    new(30, delta, 0, 0, MouseButtonFlags.LeftDown, "device")
                ], session.QpcFrequency);

            var slower = CreateTrial(1, 10, TrialStatus.Accepted);
            var faster = CreateTrial(2, 30, TrialStatus.Accepted);
            await using var queue = new DatasetWriteQueue(repository);
            Assert.True(queue.TryEnqueue(slower));
            Assert.True(queue.TryEnqueue(faster));
            Assert.True(queue.TryEnqueue(CreateTrial(3, 1_000, TrialStatus.Invalid)));
            Assert.True(queue.TryEnqueue(CreateTrial(4, 1_000, TrialStatus.Cancelled)));
            await queue.FlushAsync();

            var summary = await repository.GetSpeedCurvesAsync();
            Assert.Equal(2, summary.TrialCount);
            Assert.All(summary.Points, point =>
            {
                Assert.Equal(100d, point.MinimumMillimetersPerSecond, 8);
                Assert.Equal(300d, point.MaximumMillimetersPerSecond, 8);
                Assert.Equal(200d, point.MeanMillimetersPerSecond, 8);
            });

            await queue.EnqueueExcludeAsync(faster.Id);
            await queue.FlushAsync();
            summary = await repository.GetSpeedCurvesAsync();
            Assert.Equal(1, summary.TrialCount);
            Assert.All(summary.Points, point => Assert.Equal(100d, point.MaximumMillimetersPerSecond, 8));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RoundTripPersistsEndpointAndStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mousemotionlab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "dataset.db");

        try
        {
            var repository = new DatasetRepository(database);
            await repository.InitializeAsync();
            var profile = new CaptureProfile(
                Guid.NewGuid(),
                "device-1",
                "Test mouse",
                800,
                1_000,
                4,
                DateTimeOffset.UtcNow);
            await repository.UpsertProfileAsync(profile);
            var session = new CaptureSession(
                Guid.NewGuid(),
                profile.Id,
                DateTimeOffset.UtcNow,
                "test",
                "windows",
                10_000_000);
            await repository.CreateSessionAsync(session);
            var events = new[]
            {
                new RawMouseEvent(100, 2, 1, 0, MouseButtonFlags.None, "device-1", 4, 2),
                new RawMouseEvent(200, 3, -1, 0, MouseButtonFlags.LeftDown, "device-1", 11, -3)
            };
            var trial = TrialCapture.Create(
                Guid.NewGuid(),
                session.Id,
                1,
                50,
                100,
                200,
                TrialStatus.Accepted,
                null,
                events,
                session.QpcFrequency);

            await repository.SaveTrialAsync(trial);
            var endpoints = await repository.GetAcceptedEndpointsAsync();
            Assert.Single(endpoints);
            Assert.Equal((5, 0), endpoints[0]);

            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                             $"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT median_interval_us, max_interval_us, pause_count FROM trials WHERE id = $id;";
                command.Parameters.AddWithValue("$id", trial.Id.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(trial.Metrics.MedianIntervalMicroseconds, reader.GetDouble(0));
                Assert.Equal(trial.Metrics.MaximumIntervalMicroseconds, reader.GetDouble(1));
                Assert.Equal(trial.Metrics.PauseCount, reader.GetInt32(2));

                await reader.DisposeAsync();
                await using var eventCommand = connection.CreateCommand();
                eventCommand.CommandText = """
                    SELECT cursor_dx_pixels, cursor_dy_pixels
                    FROM events
                    WHERE trial_id = $id
                    ORDER BY sequence;
                    """;
                eventCommand.Parameters.AddWithValue("$id", trial.Id.ToString("D"));
                await using var eventReader = await eventCommand.ExecuteReaderAsync();
                Assert.True(await eventReader.ReadAsync());
                Assert.Equal(4, eventReader.GetInt32(0));
                Assert.Equal(2, eventReader.GetInt32(1));
                Assert.True(await eventReader.ReadAsync());
                Assert.Equal(11, eventReader.GetInt32(0));
                Assert.Equal(-3, eventReader.GetInt32(1));
            }

            await repository.SetTrialStatusAsync(trial.Id, TrialStatus.Excluded, "test");
            endpoints = await repository.GetAcceptedEndpointsAsync();
            Assert.Empty(endpoints);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SchemaInitializationIsIdempotent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mousemotionlab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new DatasetRepository(Path.Combine(directory, "dataset.db"));
            await repository.InitializeAsync();
            await repository.InitializeAsync();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SchemaOneIsMigratedWithoutRecreatingTheDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mousemotionlab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "dataset.db");
        try
        {
            var repository = new DatasetRepository(database);
            await repository.InitializeAsync();
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                             $"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    ALTER TABLE trials DROP COLUMN median_interval_us;
                    ALTER TABLE trials DROP COLUMN max_interval_us;
                    ALTER TABLE trials DROP COLUMN pause_count;
                    ALTER TABLE events DROP COLUMN cursor_dx_pixels;
                    ALTER TABLE events DROP COLUMN cursor_dy_pixels;
                    UPDATE schema_info SET version = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await repository.InitializeAsync();

            await using var verification = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={database};Pooling=False");
            await verification.OpenAsync();
            await using var versionCommand = verification.CreateCommand();
            versionCommand.CommandText = "SELECT version FROM schema_info;";
            Assert.Equal(3L, await versionCommand.ExecuteScalarAsync());
            await using var columnsCommand = verification.CreateCommand();
            columnsCommand.CommandText = "PRAGMA table_info(trials);";
            await using var reader = await columnsCommand.ExecuteReaderAsync();
            var columns = new HashSet<string>();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("median_interval_us", columns);
            Assert.Contains("max_interval_us", columns);
            Assert.Contains("pause_count", columns);

            await using var eventColumnsCommand = verification.CreateCommand();
            eventColumnsCommand.CommandText = "PRAGMA table_info(events);";
            await using var eventReader = await eventColumnsCommand.ExecuteReaderAsync();
            var eventColumns = new HashSet<string>();
            while (await eventReader.ReadAsync())
            {
                eventColumns.Add(eventReader.GetString(1));
            }

            Assert.Contains("cursor_dx_pixels", eventColumns);
            Assert.Contains("cursor_dy_pixels", eventColumns);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

