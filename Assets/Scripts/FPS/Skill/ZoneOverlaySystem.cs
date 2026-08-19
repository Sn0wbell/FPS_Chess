using UnityEngine;
using System.Collections.Generic;

public static class ZoneOverlaySystem
{
    const int MAX_CAGE = 16;

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
        ZoneOverlayType type
    )
    {
        if (cages.Count >= MAX_CAGE)
            return -1;

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

            UploadToShader();
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

            UploadToShader();
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