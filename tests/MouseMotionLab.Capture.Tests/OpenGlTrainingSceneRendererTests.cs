using MouseMotionLab.Capture.Services;
using OpenTK.Mathematics;

namespace MouseMotionLab.Capture.Tests;

public sealed class OpenGlTrainingSceneRendererTests
{
    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(90d, 0d)]
    [InlineData(144d, -60d)]
    [InlineData(-179d, 84d)]
    public void ViewProjection_MapsCameraForwardToViewportCenter(double yawDegrees, double pitchDegrees)
    {
        var viewProjection = OpenGlTrainingSceneRenderer.CreateViewProjection(
            1920,
            1080,
            yawDegrees,
            pitchDegrees);
        var eye = new Vector3(0f, 1.7f, 0f);
        var direction = OpenGlTrainingSceneRenderer.CreateCameraDirection(yawDegrees, pitchDegrees);

        var clip = Vector4.TransformRow(new Vector4(eye + direction * 10f, 1f), viewProjection);

        Assert.True(clip.W > 0f);
        Assert.InRange(clip.X / clip.W, -0.0001f, 0.0001f);
        Assert.InRange(clip.Y / clip.W, -0.0001f, 0.0001f);
        Assert.InRange(clip.Z / clip.W, -1f, 1f);
    }

    [Fact]
    public void ViewProjection_UsesViewportAspectRatio()
    {
        var wideProjection = OpenGlTrainingSceneRenderer.CreateViewProjection(1920, 1080, 0d, 0d);
        var squareProjection = OpenGlTrainingSceneRenderer.CreateViewProjection(1080, 1080, 0d, 0d);
        var pointToTheRight = new Vector4(1f, 1.7f, -10f, 1f);

        var wideClip = Vector4.TransformRow(pointToTheRight, wideProjection);
        var squareClip = Vector4.TransformRow(pointToTheRight, squareProjection);

        Assert.True(MathF.Abs(wideClip.X / wideClip.W) < MathF.Abs(squareClip.X / squareClip.W));
    }
}
