// Euler order selection, singularity guard and reflection adapted from babylon-mmd ikSolver.ts
// (https://github.com/noname0310/babylon-mmd) @ ccb750db2998ed1067017eaf67f3fc987cfba6f8.
// MIT License, Copyright (c) 2024 noname. Full notice: LICENSE.BabylonMmd.txt.
// Upstream solver is based on saba (https://github.com/benikabocha/saba),
// MIT License, Copyright (c) 2016-2017 benikabocha. Full notice: LICENSE.Saba.txt.
using System.Numerics;

namespace Mmd.Modern.Animation;

/// <summary>Constrains the complete local rotation using the PMX link's Euler ranges.</summary>
internal static class MmdIkRotationLimits
{
    // Exact float constants retained by the audited original build (approximately 90 and 88 degrees).
    private const float HalfPi = 1.570796f;
    private const float SingularityLimit = 1.535889f;

    /// <remarks>The caller supplies a valid normalized rotation and validated ordered bounds.</remarks>
    internal static Quaternion Constrain(Quaternion rotation, Vector3 minimum, Vector3 maximum,
        int iteration, int iterations)
    {
        var matrix = Matrix4x4.CreateFromQuaternion(rotation);
        bool reflect = iteration < (iterations >> 1);
        float x, y, z;
        Matrix4x4 constrained;

        if (-HalfPi < minimum.X && maximum.X < HalfPi)
        {
            // YXZ in quaternion notation is Rz * Rx * Ry for row-vector matrices.
            x = AsinNearSingularity(-matrix.M32);
            float cosine = MathF.Cos(x);
            y = MathF.Atan2(matrix.M31 / cosine, matrix.M33 / cosine);
            z = MathF.Atan2(matrix.M12 / cosine, matrix.M22 / cosine);
            x = ConstrainAngle(x, minimum.X, maximum.X, reflect);
            y = ConstrainAngle(y, minimum.Y, maximum.Y, reflect);
            z = ConstrainAngle(z, minimum.Z, maximum.Z, reflect);
            constrained = Matrix4x4.CreateRotationZ(z) * Matrix4x4.CreateRotationX(x)
                * Matrix4x4.CreateRotationY(y);
        }
        else if (-HalfPi < minimum.Y && maximum.Y < HalfPi)
        {
            // ZYX: the original D3DX matrix sequence is Rx * Ry * Rz.
            y = AsinNearSingularity(-matrix.M13);
            float cosine = MathF.Cos(y);
            x = MathF.Atan2(matrix.M23 / cosine, matrix.M33 / cosine);
            z = MathF.Atan2(matrix.M12 / cosine, matrix.M11 / cosine);
            x = ConstrainAngle(x, minimum.X, maximum.X, reflect);
            y = ConstrainAngle(y, minimum.Y, maximum.Y, reflect);
            z = ConstrainAngle(z, minimum.Z, maximum.Z, reflect);
            constrained = Matrix4x4.CreateRotationX(x) * Matrix4x4.CreateRotationY(y)
                * Matrix4x4.CreateRotationZ(z);
        }
        else
        {
            // XZY: the original D3DX matrix sequence is Ry * Rz * Rx.
            z = AsinNearSingularity(-matrix.M21);
            float cosine = MathF.Cos(z);
            x = MathF.Atan2(matrix.M23 / cosine, matrix.M22 / cosine);
            y = MathF.Atan2(matrix.M31 / cosine, matrix.M11 / cosine);
            x = ConstrainAngle(x, minimum.X, maximum.X, reflect);
            y = ConstrainAngle(y, minimum.Y, maximum.Y, reflect);
            z = ConstrainAngle(z, minimum.Z, maximum.Z, reflect);
            constrained = Matrix4x4.CreateRotationY(y) * Matrix4x4.CreateRotationZ(z)
                * Matrix4x4.CreateRotationX(x);
        }

        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(constrained));
    }

    private static float AsinNearSingularity(float value) => Math.Clamp(
        MathF.Asin(Math.Clamp(value, -1f, 1f)), -SingularityLimit, SingularityLimit);

    private static float ConstrainAngle(float angle, float minimum, float maximum, bool reflect)
    {
        if (angle < minimum)
        {
            float reflected = 2f * minimum - angle;
            return reflect && reflected <= maximum ? reflected : minimum;
        }
        if (angle > maximum)
        {
            float reflected = 2f * maximum - angle;
            return reflect && reflected >= minimum ? reflected : maximum;
        }
        return angle;
    }
}
