using UnityEngine;

/// <summary>How a gun's bolt / charging handle (or a shotgun's pump) cycles after each shot.</summary>
public enum BoltCycleMode
{
    /// <summary>Semi-auto / automatic: snaps back and slams home very fast, done well before the next shot.</summary>
    Auto,
    /// <summary>Pump-action / bolt-action: worked by hand over exactly the time between two shots.</summary>
    Manual
}

/// <summary>
/// The bolt's motion: how far back it is (0 = at rest, 1 = at its back point) along a cycle's progress (0..1), and its
/// pose there along the path rest -> (lift point) -> back point. Pure maths, no state.
/// </summary>
public static class BoltMotion
{
    /// <summary>An Auto bolt's cycle never takes more than this share of the time between two shots.</summary>
    public const float AutoMaxShareOfInterval = 0.8f;
    /// <summary>With a lift point: the share of the path from rest to the lift point (the handle turning up).</summary>
    public const float LiftShare = 0.3f;

    private const float AutoBackShare = 0.35f;   // Auto: snaps back over the first 35%, slams home over the rest
    private const float ManualPullShare = 0.4f;  // Manual: pulled back over the first 40%...
    private const float ManualHoldUntil = 0.55f; // ...held back a moment, pushed home over the rest
    private const int PeakSamples = 100;

    /// <summary>
    /// How far back the bolt is at <paramref name="progress"/> through its cycle: <paramref name="custom"/> when it
    /// has keys (time 0..1 -> 0 rest .. 1 back point), else the built-in shape of <paramref name="mode"/>.
    /// </summary>
    public static float Shape(AnimationCurve custom, BoltCycleMode mode, float progress)
    {
        progress = Mathf.Clamp01(progress);
        if (custom != null && custom.length > 0)
            return Mathf.Clamp01(custom.Evaluate(progress));

        if (mode == BoltCycleMode.Auto)
        {
            if (progress < AutoBackShare)
            {
                float u = 1f - progress / AutoBackShare;
                return 1f - u * u; // snaps back, slowing as it gets there
            }
            float v = (progress - AutoBackShare) / (1f - AutoBackShare);
            return 1f - v * v; // slams home, fastest at the end
        }

        if (progress < ManualPullShare)
            return Mathf.SmoothStep(0f, 1f, progress / ManualPullShare);
        if (progress < ManualHoldUntil)
            return 1f;
        return 1f - Mathf.SmoothStep(0f, 1f, (progress - ManualHoldUntil) / (1f - ManualHoldUntil));
    }

    /// <summary>The progress at which the bolt is first furthest back (where a shell flies out / an empty gun's bolt locks).</summary>
    public static float PeakProgress(AnimationCurve custom, BoltCycleMode mode)
    {
        float best = -1f, at = 0f;
        for (int i = 0; i <= PeakSamples; i++)
        {
            float p = (float)i / PeakSamples;
            float s = Shape(custom, mode, p);
            if (s > best + 1e-4f)
            {
                best = s;
                at = p;
            }
        }
        return at;
    }

    /// <summary>
    /// The bolt's pose <paramref name="back"/> (0..1) along its path: rest -> back point, or rest -> lift point
    /// (the first <see cref="LiftShare"/>) -> back point. Positions and rotations in the bolt's parent's space.
    /// </summary>
    public static void Pose(float back, Vector3 restPos, Quaternion restRot, bool hasLift, Vector3 liftPos, Quaternion liftRot,
        Vector3 backPos, Quaternion backRot, out Vector3 pos, out Quaternion rot)
    {
        back = Mathf.Clamp01(back);
        if (!hasLift)
        {
            pos = Vector3.Lerp(restPos, backPos, back);
            rot = Quaternion.Slerp(restRot, backRot, back);
            return;
        }
        if (back < LiftShare)
        {
            float t = back / LiftShare;
            pos = Vector3.Lerp(restPos, liftPos, t);
            rot = Quaternion.Slerp(restRot, liftRot, t);
            return;
        }
        float k = (back - LiftShare) / (1f - LiftShare);
        pos = Vector3.Lerp(liftPos, backPos, k);
        rot = Quaternion.Slerp(liftRot, backRot, k);
    }
}
