using UnityEngine;

/// <summary>Horizontal play area of the current arena (from ArenaController.arenaBoundary).</summary>
public static class ArenaArea
{
    public static bool TryGetBounds(out Bounds bounds)
    {
        var arena = Object.FindFirstObjectByType<ArenaController>();
        if (arena == null || arena.arenaBoundary == null)
        {
            bounds = default;
            return false;
        }

        Transform t = arena.arenaBoundary;
        Vector3 s = t.lossyScale;
        bounds = new Bounds(t.position, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
        return true;
    }

    public static bool ContainsXZ(Bounds b, Vector3 p, float margin = 0f) =>
        p.x >= b.min.x + margin && p.x <= b.max.x - margin &&
        p.z >= b.min.z + margin && p.z <= b.max.z - margin;

    public static Vector3 ClampXZ(Bounds b, Vector3 p, float margin = 0f)
    {
        float mx = Mathf.Min(margin, b.extents.x), mz = Mathf.Min(margin, b.extents.z);
        p.x = Mathf.Clamp(p.x, b.min.x + mx, b.max.x - mx);
        p.z = Mathf.Clamp(p.z, b.min.z + mz, b.max.z - mz);
        return p;
    }

    /// <summary>
    /// Where a body launched with <paramref name="velocity"/> lands back at its start height
    /// (gravity <paramref name="gravity"/> &lt; 0). Used to keep thrown pieces inside the arena.
    /// </summary>
    public static Vector3 PredictLanding(Vector3 start, Vector3 velocity, float gravity)
    {
        float g = Mathf.Max(0.01f, -gravity);
        float flight = velocity.y > 0f ? 2f * velocity.y / g : 0f;
        return start + new Vector3(velocity.x, 0f, velocity.z) * flight;
    }
}
