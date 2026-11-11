using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Glow for shots so the player sees them: the bullet, a tracer behind it, and where it hit (the bullet hole and the
/// sparks it throws) while the splash lasts. Additive, bright, hidden behind walls (shader FPSChess/ShotGlow).
/// The colour tells the sides apart: the player's own side, or the others.
/// </summary>
public static class ShotHighlight
{
    public const string ShaderName = "FPSChess/ShotGlow";
    private const float SurfaceOffset = -2f; // a bullet hole's glow sits just in front of its decal (no flicker)
    private const int SparkTextureSize = 64;

    private static readonly Dictionary<(Color, Texture, bool, bool), Material> materials = new();
    private static Shader shader;
    private static Texture2D sparkTexture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        materials.Clear();
        shader = null;
        sparkTexture = null;
    }

    /// <summary>
    /// The glow material (one shared per combination). <paramref name="shape"/>: its alpha is the glow's shape (null: the
    /// whole mesh). <paramref name="vertexAlpha"/>: particles / lines fade with their vertex colour.
    /// <paramref name="onASurface"/>: drawn just in front of a surface (a decal). Null when the shader is missing.
    /// </summary>
    public static Material MaterialFor(Color color, Texture shape = null, bool vertexAlpha = false, bool onASurface = false)
    {
        var key = (color, shape, vertexAlpha, onASurface);
        if (materials.TryGetValue(key, out Material m) && m != null)
            return m;

        if (shader == null)
            shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogWarning($"ShotHighlight: shader '{ShaderName}' not found: shots are not highlighted.");
            return null;
        }

        m = new Material(shader) { name = "ShotGlow (runtime)" };
        m.SetColor("_Color", color);
        if (shape != null)
            m.SetTexture("_MainTex", shape);
        m.SetFloat("_UseVertexAlpha", vertexAlpha ? 1f : 0f);
        if (onASurface)
        {
            m.SetFloat("_OffsetFactor", SurfaceOffset);
            m.SetFloat("_OffsetUnits", SurfaceOffset);
        }
        materials[key] = m;
        return m;
    }

    /// <summary>The player's own side's colour, or the others' (no player view: the others').</summary>
    public static Color ColorFor(GameObject shooter, Color ownSide, Color otherSide)
    {
        ChessPieceFPSController piece = shooter != null ? shooter.GetComponentInParent<ChessPieceFPSController>() : null;
        Team? viewer = RevealSystem.LocalViewerTeam;
        return piece != null && viewer.HasValue && piece.Team == viewer.Value ? ownSide : otherSide;
    }

    /// <summary>The texture giving a material its shape (URP's _BaseMap, else _MainTex), or null.</summary>
    public static Texture ShapeOf(Material material)
    {
        if (material == null)
            return null;
        Texture t = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
        if (t == null && material.HasProperty("_MainTex"))
            t = material.GetTexture("_MainTex");
        return t;
    }

    /// <summary>A soft round dot: the shape of a spark whose particle has no texture of its own.</summary>
    public static Texture2D SparkTexture
    {
        get
        {
            if (sparkTexture != null)
                return sparkTexture;

            sparkTexture = new Texture2D(SparkTextureSize, SparkTextureSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[SparkTextureSize * SparkTextureSize];
            float half = SparkTextureSize * 0.5f;
            for (int y = 0; y < SparkTextureSize; y++)
                for (int x = 0; x < SparkTextureSize; x++)
                {
                    float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    pixels[y * SparkTextureSize + x] = new Color(1f, 1f, 1f, a * a); // bright core, soft edge
                }
            sparkTexture.SetPixels32(pixels);
            sparkTexture.Apply(false);
            return sparkTexture;
        }
    }

    /// <summary>A glowing copy of <paramref name="source"/>'s mesh, on top of it (a child, scaled up a little).</summary>
    public static MeshRenderer AddGlowCopy(MeshRenderer source, string name, float scale)
    {
        MeshFilter filter = source != null ? source.GetComponent<MeshFilter>() : null;
        if (filter == null || filter.sharedMesh == null)
            return null;

        var go = new GameObject(name);
        go.transform.SetParent(source.transform, false);
        go.transform.localScale = Vector3.one * scale;
        go.layer = source.gameObject.layer;
        go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
        var r = go.AddComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return r;
    }
}
