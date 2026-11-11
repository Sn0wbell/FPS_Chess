using UnityEngine;
using System.Collections.Generic;

public static class ZoneOverlaySystem
{
    // Must match "#define MAX_CAGE" in Shaders/FPS/ZoneOverlay.shader.
    public const int MAX_CAGE = 32;

    /// <summary>How many more zones can be shown right now.</summary>
    public static int FreeSlots => MAX_CAGE - cages.Count - reserved;

    // Slots promised to zones that will be shown a moment later (a queen's barrage, zone by zone).
    private static int reserved;

    /// <summary>Holds up to <paramref name="count"/> free slots for zones registered later; returns how many it got.</summary>
    public static int Reserve(int count)
    {
        int n = Mathf.Clamp(count, 0, Mathf.Max(0, FreeSlots));
        reserved += n;
        return n;
    }

    /// <summary>Gives back reserved slots that will not be used.</summary>
    public static void ReleaseReservation(int count)
    {
        if (count > 0)
            reserved = Mathf.Max(0, reserved - count);
    }

    private static bool dirty;
    private static int lastFlushFrame = -1;

    // Static state survives between play sessions when domain reload is off: start clean.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        cages.Clear();
        nextId = 1;
        reserved = 0;
        dirty = false;
        lastFlushFrame = -1;
        UploadToShader();
    }

    /// <summary>Uploads pending position/colour changes once per frame (called by active zones).</summary>
    public static void FlushIfDirty()
    {
        if (!dirty || lastFlushFrame == Time.frameCount)
            return;
        lastFlushFrame = Time.frameCount;
        dirty = false;
        UploadToShader();
    }

    public enum ZoneOverlayType
    {
        Pull = 0,
        Gravity = 1,
        Red = 2
    }

    struct CageData
    {
        public int id;
        public Vector3 center;
        public float radius;
        public float minY;
        public float maxY;
        public Color color;
        public int type;
    }

    static readonly List<CageData> cages = new();
    static int nextId = 1;

    static readonly int CageCountID = Shader.PropertyToID("_CageCount");
    static readonly int CageDataID = Shader.PropertyToID("_CageData");
    static readonly int CageHeightID = Shader.PropertyToID("_CageHeight");
    static readonly int CageColorID = Shader.PropertyToID("_CageColor");
    static readonly int CageTypeID = Shader.PropertyToID("_CageType");

    static Vector4[] cageTypeArray = new Vector4[MAX_CAGE];
    static Vector4[] cageDataArray = new Vector4[MAX_CAGE];
    static Vector4[] cageHeightArray = new Vector4[MAX_CAGE];
    static Vector4[] cageColorArray = new Vector4[MAX_CAGE];

    // =========================
    // PUBLIC API
    // =========================

    public static int Register(
        Vector3 center,
        float radius,
        float minY,
        float maxY,
        Color color,
        ZoneOverlayType type,
        bool usesReservedSlot = false
    )
    {
        if (usesReservedSlot)
            ReleaseReservation(1); // its slot was held for it: it always fits

        if (cages.Count >= MAX_CAGE)
        {
            Debug.LogWarning($"ZoneOverlaySystem: {MAX_CAGE} zones already shown; this zone is not drawn.");
            return -1;
        }

        int id = nextId++;

        // 🔴 FIX: đảm bảo luôn bao phủ ground
        ExpandHeight(ref minY, ref maxY);

        cages.Add(new CageData
        {
            id = id,
            center = center,
            radius = radius,
            minY = minY,
            maxY = maxY,
            color = color,
            type = (int)type
        });

        UploadToShader();
        return id;
    }

    public static void UpdateTransform(
        int id,
        Vector3 center,
        float minY,
        float maxY
    )
    {
        for (int i = 0; i < cages.Count; i++)
        {
            if (cages[i].id != id)
                continue;

            // 🔴 FIX: luôn đảm bảo height hợp lệ
            ExpandHeight(ref minY, ref maxY);

            var c = cages[i];
            c.center = center;
            c.minY = minY;
            c.maxY = maxY;
            cages[i] = c;

            dirty = true; // uploaded once per frame by FlushIfDirty
            return;
        }
    }

    public static void Unregister(int id)
    {
        for (int i = cages.Count - 1; i >= 0; i--)
        {
            if (cages[i].id == id)
                cages.RemoveAt(i);
        }

        UploadToShader();
    }

    public static void UpdateColor(int id, Color color)
    {
        for (int i = 0; i < cages.Count; i++)
        {
            if (cages[i].id != id)
                continue;

            var c = cages[i];
            c.color = color;
            cages[i] = c;

            dirty = true; // uploaded once per frame by FlushIfDirty
            return;
        }
    }

    // =========================
    // INTERNAL FIX
    // =========================

    static void ExpandHeight(ref float minY, ref float maxY)
    {
        // đảm bảo luôn có thickness
        if (Mathf.Abs(maxY - minY) < 0.5f)
        {
            float center = (minY + maxY) * 0.5f;
            minY = center - 5f;
            maxY = center + 5f;
        }
    }

    // =========================
    // SHADER UPLOAD
    // =========================

    static void UploadToShader()
    {
        int count = cages.Count;

        for (int i = 0; i < count; i++)
        {
            var c = cages[i];

            cageDataArray[i] = new Vector4(
                c.center.x,
                c.center.y,
                c.center.z,
                c.radius
            );

            cageHeightArray[i] = new Vector4(
                c.minY,
                c.maxY,
                0f,
                0f
            );

            cageColorArray[i] = new Vector4(
                c.color.r,
                c.color.g,
                c.color.b,
                c.color.a
            );

            cageTypeArray[i] = new Vector4(c.type, 0f, 0f, 0f);
        }

        for (int i = count; i < MAX_CAGE; i++)
        {
            cageDataArray[i] = Vector4.zero;
            cageHeightArray[i] = Vector4.zero;
            cageColorArray[i] = Vector4.zero;
            cageTypeArray[i] = Vector4.zero;
        }

        Shader.SetGlobalInt(CageCountID, count);
        Shader.SetGlobalVectorArray(CageDataID, cageDataArray);
        Shader.SetGlobalVectorArray(CageHeightID, cageHeightArray);
        Shader.SetGlobalVectorArray(CageColorID, cageColorArray);
        Shader.SetGlobalVectorArray(CageTypeID, cageTypeArray);
    }
}