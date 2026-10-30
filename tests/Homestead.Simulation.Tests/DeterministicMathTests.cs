namespace Homestead.Simulation.Tests;

public class DeterministicMathTests
{
    [Fact]
    public void Sine_cosine_and_arc_tangent_match_the_platform_closely()
    {
        for (double x = -20; x <= 20; x += 0.0137)
        {
            Assert.Equal(Math.Sin(x), DeterministicMath.Sin(x), 1e-11);
            Assert.Equal(Math.Cos(x), DeterministicMath.Cos(x), 1e-11);
        }
        for (double y = -3; y <= 3; y += 0.071)
        for (double x = -3; x <= 3; x += 0.059)
            Assert.Equal(Math.Atan2(y, x), DeterministicMath.Atan2(y, x), 1e-12);
        Assert.Equal(0, DeterministicMath.Atan2(0, 0));
        Assert.Equal(Math.PI, DeterministicMath.Atan2(0, -1), 1e-15);
        Assert.Equal(Math.PI / 2, DeterministicMath.Atan2(1, 0), 1e-15);
    }
}
