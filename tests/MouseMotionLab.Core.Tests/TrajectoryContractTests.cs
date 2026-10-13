using MouseMotionLab.Core.Contracts;

namespace MouseMotionLab.Core.Tests;

public sealed class TrajectoryContractTests
{
    [Fact]
    public void ValidTrajectoryHasExactEndpointAndClickTime()
    {
        var request = new TrajectoryRequest(7, -2, 800, 1_000, 42, "baseline");
        var result = new TrajectoryResult(
            1,
            new[]
            {
                new TrajectorySample(1_000, 3, -1),
                new TrajectorySample(1_000, 4, -1)
            },
            2_000,
            0.5,
            Array.Empty<string>(),
            "test",
            42);

        result.ValidateAgainst(request);
        Assert.Equal((7, -2), result.Endpoint);
    }

    [Fact]
    public void EndpointMismatchIsRejected()
    {
        var request = new TrajectoryRequest(8, 0, 800, 1_000, 1, "baseline");
        var result = new TrajectoryResult(
            1,
            new[] { new TrajectorySample(1_000, 7, 0) },
            1_000,
            1,
            Array.Empty<string>(),
            "test",
            1);

        Assert.Throws<InvalidDataException>(() => result.ValidateAgainst(request));
    }
}

