using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Collections;

// =========================
// RED ZONE (no cage): drawn by the overlay shader on ground / water; on any other surface
// (trees, rocks, roofs, rook walls) by a decal projected from above.
// =========================
public class RedZone : Zone
{
    [Header("Decal (surfaces without the overlay shader)")]
    [Tooltip("Decal material (Shader Graphs/RedZoneDecal); its texture is made from cageColor.")]
    [SerializeField] private Material decalMaterial;
    [Tooltip("The decal marks surfaces this far above / below the zone's centre.")]
    [SerializeField] private float decalDepth = 2f;
    [Tooltip("Surfaces steeper than this (tree trunks, walls' sides, undersides) are not marked.")]
    [Range(0f, 180f)]
    [SerializeField] private float decalMaxAngle = 60f;

    private const string OverlayShaderName = "FPS/ZoneOverlay";
    private const float DecalAngleFadeWidth = 10f;
    private const int DecalTextureSize = 128;

    private Coroutine blinkRoutine;
    private DecalProjector decal;
    private Material decalMaterialInstance;
    private Texture2D decalTexture;
    private Color decalTextureColor;

    /// <summary>True when the zone is drawn by its decal (it landed on a surface without the overlay shader).</summary>
    public bool UsesDecal => decal != null && decal.enabled;

    /// <summary>How far above / below its centre it is drawn (the grenade's blast height); set before Activate.</summary>
    public float HeightRange { get; set; } = 4f;

    // =========================
    // PUBLIC API
    // =========================
    public override void Activate(Vector3 pos, float radius, float duration)
    {
        base.Activate(pos, radius, duration);

        // Drawn only on surfaces within the blast's height (not on the ground far below).
        overlayMinY = pos.y - HeightRange;
        overlayMaxY = pos.y + HeightRange;
        if (cageId >= 0)
            ZoneOverlaySystem.UpdateTransform(cageId, pos, overlayMinY, overlayMaxY);

        // The overlay only shows on ground / water: anywhere else the decal draws the zone.
        SetDecal(!LandsOnOverlaySurface(pos), radius);

        // đảm bảo alpha đúng ngay frame đầu
        ApplyOverlayAlpha(pulseMaxAlpha);

        blinkRoutine = StartCoroutine(BlinkRoutine(duration));
    }

    public override void Deactivate()
    {
        base.Deactivate();
        StopBlink();
        if (decal != null)
            decal.enabled = false;
    }

    private void OnDestroy()
    {
        if (decalMaterialInstance != null) Destroy(decalMaterialInstance);
        if (decalTexture != null) Destroy(decalTexture);
    }

    // =========================
    // DECAL
    // =========================
    /// <summary>Does the surface right under the zone use the overlay shader (ground / water)?</summary>
    private static bool LandsOnOverlaySurface(Vector3 pos)
    {
        if (!Physics.Raycast(pos + Vector3.up * 0.25f, Vector3.down, out RaycastHit hit, 0.75f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return true; // nothing under it: leave it to the overlay as before

        Renderer r = hit.collider.GetComponent<Renderer>();
        if (r == null) r = hit.collider.GetComponentInParent<Renderer>();
        if (r == null) r = hit.collider.GetComponentInChildren<Renderer>();
        if (r == null)
            return false;

        foreach (var m in r.sharedMaterials)
            if (m != null && m.shader != null && m.shader.name == OverlayShaderName)
                return true;
        return false;
    }

    private void SetDecal(bool on, float radius)
    {
        if (!on || decalMaterial == null)
        {
            if (decal != null)
                decal.enabled = false;
            return;
        }

        if (decal == null)
        {
            var go = new GameObject("RedZoneDecal");
            go.transform.SetParent(transform, false);
            decal = go.AddComponent<DecalProjector>();
            decalMaterialInstance = new Material(decalMaterial);
            decal.material = decalMaterialInstance;
        }

        if (decalTexture == null || decalTextureColor != cageColor)
            BuildDecalTexture();

        // Projected straight down, a box as wide as the zone and decalDepth above / below its centre.
        decal.transform.localPosition = Vector3.zero;
        decal.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        decal.size = new Vector3(radius * 2f, radius * 2f, decalDepth * 2f);
        decal.pivot = Vector3.zero;
        decal.startAngleFade = Mathf.Max(0f, decalMaxAngle - DecalAngleFadeWidth);
        decal.endAngleFade = decalMaxAngle;
        decal.fadeFactor = pulseMaxAlpha;
        decal.enabled = true;
    }

    /// <summary>A filled disc in the zone's colour (sharp edge, like the overlay).</summary>
    private void BuildDecalTexture()
    {
        if (decalTexture == null)
            decalTexture = new Texture2D(DecalTextureSize, DecalTextureSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };

        var pixels = new Color32[DecalTextureSize * DecalTextureSize];
        float half = DecalTextureSize * 0.5f;
        for (int y = 0; y < DecalTextureSize; y++)
            for (int x = 0; x < DecalTextureSize; x++)
            {
                float dx = x + 0.5f - half, dy = y + 0.5f - half;
                float a = Mathf.Clamp01(half - Mathf.Sqrt(dx * dx + dy * dy)); // 1px soft edge
                pixels[y * DecalTextureSize + x] = new Color(cageColor.r, cageColor.g, cageColor.b, a);
            }
        decalTexture.SetPixels32(pixels);
        decalTexture.Apply(false);
        decalTextureColor = cageColor;
        decalMaterialInstance.SetTexture("Base_Map", decalTexture);
    }

    // =========================
    // BLINK (AUTHORITATIVE): between pulseMinAlpha and pulseMaxAlpha (prefab), faster and faster
    // =========================
    private IEnumerator BlinkRoutine(float duration)
    {
        const float startPeriod = 0.7f;
        const float endPeriod = 0.1f;

        float elapsed = 0f;
        float phase = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float lifeT = Mathf.Clamp01(elapsed / duration);

            float period = Mathf.Lerp(startPeriod, endPeriod, lifeT);

            phase += Time.deltaTime / period;
            float t = Mathf.PingPong(phase, 1f);
            t = t * t * (3f - 2f * t);

            float alpha = Mathf.Lerp(pulseMinAlpha, pulseMaxAlpha, t);

            ApplyOverlayAlpha(alpha);

            yield return null;
        }

        ApplyOverlayAlpha(pulseMaxAlpha);
    }

    private void StopBlink()
    {
        if (blinkRoutine != null)
            StopCoroutine(blinkRoutine);

        blinkRoutine = null;
    }

    // =========================
    // CORE FIX: trực tiếp gọi overlay system
    // =========================
    private void ApplyOverlayAlpha(float a)
    {
        if (cageId < 0)
            return;

        Color c = cageColor;
        c.a = a;

        ZoneOverlaySystem.UpdateColor(cageId, c);
        if (decal != null && decal.enabled)
            decal.fadeFactor = a;
    }

    // =========================
    // OVERRIDES
    // =========================

    protected override void UpdatePulse()
    {
    }

    protected override void LayoutQuads(float radius)
    {
    }
    protected override void BuildIfNeeded()
    {
    }
    protected override void StopAllInternalCoroutines()
    {
        base.StopAllInternalCoroutines();
        StopBlink();
    }
}