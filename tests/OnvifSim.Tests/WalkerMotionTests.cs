using OnvifSim.World;

namespace OnvifSim.Tests;

public class WalkerMotionTests
{
    // A moment at which a walker at 1 m/s has walked exactly `walked` metres plus whole rounds of `round`.
    private static DateTimeOffset After(double walked, double round) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)((round * 1000 + walked) * 1000));

    [Fact]
    public void ALoop_GoesOnFromTheLastPointToTheFirst()
    {
        // A 10 m square: east, south, west, then north back to the start.
        var walker = new WalkerModel { Route = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)], Speed = 1 };

        var pose = WalkerMotion.PoseAt(walker, After(15, round: 40));
        Assert.Equal((10f, 5f, 180f), (pose.X, pose.Z, pose.Heading));

        var closing = WalkerMotion.PoseAt(walker, After(35, round: 40));
        Assert.Equal((0f, 5f, 0f), (closing.X, closing.Z, closing.Heading));
    }

    [Fact]
    public void AWalkerWhoTurnsRound_ComesBackTheSameWay()
    {
        var walker = new WalkerModel { Route = [new(0, 0), new(10, 0)], Speed = 1, Return = WalkerReturn.Reverse };
        var pose = WalkerMotion.PoseAt(walker, After(15, round: 20));
        Assert.Equal((5f, 0f, -90f), (pose.X, pose.Z, pose.Heading));
    }

    [Fact]
    public void AWalkerWithoutSpeed_StandsAtTheStartFacingTheNextPoint()
    {
        var walker = new WalkerModel { Route = [new(3, 4), new(3, -10)], Speed = 0 };
        var pose = WalkerMotion.PoseAt(walker, DateTimeOffset.UtcNow);
        Assert.Equal((3f, 4f, 0f, 0f), (pose.X, pose.Z, pose.Heading, pose.StridePhase));
    }
}
