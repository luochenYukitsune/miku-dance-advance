// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// Adapted from Saba VMDAnimation.cpp, commit 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332.
// Copyright(c) 2016-2017 benikabocha. Distributed under the MIT License.
// Full license: third_party/saba/LICENCE.txt. This port bounds inversion iterations.

namespace Mmd.Modern.Animation;

public readonly record struct BezierCurve
{
    public float X1 { get; }
    public float Y1 { get; }
    public float X2 { get; }
    public float Y2 { get; }
    public static BezierCurve Linear { get; } = new(0, 0, 1, 1);

    public BezierCurve(float x1, float y1, float x2, float y2)
    {
        if (!float.IsFinite(x1) || !float.IsFinite(y1) || !float.IsFinite(x2) || !float.IsFinite(y2)
            || x1 is < 0 or > 1 || y1 is < 0 or > 1 || x2 is < 0 or > 1 || y2 is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(x1), "Bezier controls must be finite in [0,1].");
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
    }

    public double Evaluate(double progress)
    {
        if (!double.IsFinite(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        if (X1 == Y1 && X2 == Y2) return progress;
        double lower = 0, upper = 1;
        // Controls in [0,1] guarantee monotonic x, including reversed control order.
        // Parameter-space termination also handles flat derivatives and extreme curves.
        for (int i = 0; i < 48; i++)
        {
            double mid = (lower + upper) * .5;
            if (Cubic(mid, X1, X2) < progress) lower = mid;
            else upper = mid;
        }
        return Math.Clamp(Cubic((lower + upper) * .5, Y1, Y2), 0, 1);
    }

    private static double Cubic(double t, double p1, double p2)
    {
        double inverse = 1 - t;
        return 3 * inverse * inverse * t * p1 + 3 * inverse * t * t * p2 + t * t * t;
    }
}
