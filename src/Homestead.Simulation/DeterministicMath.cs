namespace Homestead.Simulation;

/// <summary>
/// Sine, cosine and arc tangent from plain arithmetic only, so every machine computes the same bits. Math.Sin and
/// its kin come from the platform's C library and may differ in the last place between Windows, Linux and macOS
/// (x64 or ARM); in a lockstep game every client must simulate exactly alike. Addition, multiplication, division and
/// square root are exact to IEEE 754 everywhere. Accurate to about 1e-12, plenty for the simulation.
/// </summary>
public static class DeterministicMath
{
    private const double TwoPi = 2 * Math.PI, HalfPi = Math.PI / 2;

    public static double Sin(double x)
    {
        // Into [-π, π], then by symmetry into [-π/2, π/2], where the series converges fast.
        double r = x - Math.Round(x / TwoPi) * TwoPi;
        if (r > HalfPi)
            r = Math.PI - r;
        else if (r < -HalfPi)
            r = -Math.PI - r;
        double r2 = r * r;
        // Taylor series up to r^17 (error below 1e-14 on [-π/2, π/2]), evaluated from the highest term down.
        double sum = 1.0 / 355687428096000; // 1/17!
        sum = 1.0 / 1307674368000 - r2 * sum; // 1/15!
        sum = 1.0 / 6227020800 - r2 * sum; // 1/13!
        sum = 1.0 / 39916800 - r2 * sum; // 1/11!
        sum = 1.0 / 362880 - r2 * sum; // 1/9!
        sum = 1.0 / 5040 - r2 * sum; // 1/7!
        sum = 1.0 / 120 - r2 * sum; // 1/5!
        sum = 1.0 / 6 - r2 * sum; // 1/3!
        sum = 1 - r2 * sum;
        return r * sum;
    }

    public static double Cos(double x) => Sin(x + HalfPi);

    /// <summary>The angle of the point (x, y) in (-π, π], like <see cref="Math.Atan2"/>.</summary>
    public static double Atan2(double y, double x)
    {
        if (x == 0 && y == 0)
            return 0;
        // In the first octant (z = small/large ≤ 1), then back by symmetry.
        double ax = Math.Abs(x), ay = Math.Abs(y);
        bool steep = ay > ax;
        double angle = Atan(steep ? ax / ay : ay / ax);
        if (steep)
            angle = HalfPi - angle;
        if (x < 0)
            angle = Math.PI - angle;
        return y < 0 ? -angle : angle;
    }

    /// <summary>Arc tangent of z in [0, 1].</summary>
    private static double Atan(double z)
    {
        // atan(z) = 2·atan(z / (1 + √(1 + z²))): twice halves the angle to at most π/16 (z ≤ 0.2).
        for (int i = 0; i < 2; i++)
            z /= 1 + Math.Sqrt(1 + z * z);
        double z2 = z * z;
        // Series z - z³/3 + z⁵/5 - … up to z^23 (error below 1e-17 for z ≤ 0.2).
        double sum = 0;
        for (int n = 23; n >= 1; n -= 2)
            sum = 1.0 / n - z2 * sum;
        return 4 * z * sum;
    }
}
