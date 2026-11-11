using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using static ZoneOverlaySystem;

public class Zone : MonoBehaviour
{
    [Header("Cage Shape")]
    [SerializeField] protected int quadCount = 36;
    [SerializeField] protected float cageHeight = 4f;

    [Header("Timing")]
    [SerializeField] protected float growTime = 0.15f;
    [SerializeField] protected float collapseTime = 0.2f;

    [Header("Visual")]
    [SerializeField] protected Color cageColor = new Color(1f, 0f, 0f, 0.3f);
    [SerializeField] protected ZoneOverlayType overlayType;

    [Header("Pulse")]
    [SerializeField] protected float pulseSpeed = 3f;
    [SerializeField] protected float pulseMinAlpha = 0.15f;
    [SerializeField] protected float pulseMaxAlpha = 0.35f;

    [Header("Decal (surfaces without the overlay shader: trees, rocks, roofs, rook walls)")]
    [Tooltip("Decal material (ZoneDecal_Mat); its texture is made from cageColor. Empty = no decal.")]
    [SerializeField] protected Material decalMaterial;
    [Tooltip("The decal marks surfaces this far above / below the zone's centre.")]
    [SerializeField] protected float decalDepth = 2f;
    [Tooltip("Surfaces steeper than this (tree trunks, walls' sides, undersides) are not marked.")]
    [Range(0f, 180f)]
    [SerializeField] protected float decalMaxAngle = 60f;

    private const float DecalAngleFadeWidth = 10f;
    private const int DecalTextureSize = 128;

    private DecalProjector decal;
    private Material decalMaterialInstance;
    private Texture2D decalTexture;
    private Color decalTextureColor;

    /// <summary>True while the zone is (also) drawn by its decal.</summary>
    public bool UsesDecal => decal != null && decal.enabled;

    /// <summary>How far above / below its centre the decal marks surfaces; set before Activate.</summary>
    public float DecalDepth { get => decalDepth; set => decalDepth = value; }

    // =========================
    // INTERNAL
    // =========================
    protected readonly List<Transform> quads = new();
    protected Material sharedMaterial;
    protected Coroutine lifeRoutine;

    protected float currentRadius;
    protected int cageId = -1;

    protected static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");

    // 🔴 FIX: base color immutable
    protected Color baseCageColor;

    // overlay height
    protected float overlayMinY;
    protected float overlayMaxY;

    public float CageHeight { get => cageHeight; set => cageHeight = value; }

    // =========================
    // UNITY
    // =========================
    protected void Awake()
    {
        baseCageColor = cageColor; // cache gốc

        BuildIfNeeded();
        ApplyColor(baseCageColor);
        SetCageHeight(0f);
    }

    protected void Update()
    {
        if (cageId < 0)
            return;

        Vector3 pos = transform.position;

        ZoneOverlaySystem.UpdateTransform(
            cageId,
            pos,
            overlayMinY,
            overlayMaxY
        );

        UpdatePulse();
    }

    protected void LateUpdate()
    {
        ZoneOverlaySystem.FlushIfDirty();
    }

    // Destroyed or disabled without Deactivate() (scene unload, owner destroyed):
    // never leave a stale zone registered in the static overlay list.
    protected void OnDisable()
    {
        if (cageId >= 0)
        {
            ZoneOverlaySystem.Unregister(cageId);
            cageId = -1;
        }
    }

    // =========================
    // PUBLIC API
    // =========================
    /// <summary>Its overlay slot was reserved in advance (ZoneOverlaySystem.Reserve); used by the next Activate.</summary>
    public bool UsesReservedSlot { get; set; }

    public virtual void Activate(Vector3 pos, float radius, float duration)
    {
        // Activated again while still shown: give its old slot back first (never leak one).
        if (cageId >= 0)
        {
            ZoneOverlaySystem.Unregister(cageId);
            cageId = -1;
        }

        currentRadius = radius;

        transform.localScale = Vector3.one;
        transform.position = pos;

        BuildIfNeeded();
        LayoutQuads(radius);

        gameObject.SetActive(true);
        StopAllInternalCoroutines();
        SetCageHeight(0f);

        overlayMinY = pos.y - 5f;
        overlayMaxY = pos.y + 5f;

        // 🔴 FIX: dùng base color (không bị mutate)
        cageId = ZoneOverlaySystem.Register(
            pos,
            currentRadius,
            overlayMinY,
            overlayMaxY,
            baseCageColor,
            overlayType,
            UsesReservedSlot
        );
        UsesReservedSlot = false;

        lifeRoutine = StartCoroutine(LifeRoutine(duration));

        // Every other surface (trees, rocks, roofs, rook walls) is marked by the decal; the overlay
        // ground / water does not take it (DBuffer decals skip the custom overlay shader).
        SetDecal(true, radius);
    }

    public virtual void Deactivate()
    {
        StopAllInternalCoroutines();
        SetCageHeight(0f);

        if (cageId >= 0)
        {
            ZoneOverlaySystem.Unregister(cageId);
            cageId = -1;
        }

        if (decal != null)
            decal.enabled = false;

        gameObject.SetActive(false);
    }

    protected void OnDestroy()
    {
        if (decalMaterialInstance != null) Destroy(decalMaterialInstance);
        if (decalTexture != null) Destroy(decalTexture);
    }

    // =========================
    // DECAL
    // =========================
    /// <summary>Turns the zone's decal on (decalMaterial set) or off; projected straight down from its centre.</summary>
    protected void SetDecal(bool on, float radius)
    {
        if (!on || decalMaterial == null)
        {
            if (decal != null)
                decal.enabled = false;
            return;
        }

        if (decal == null)
        {
            var go = new GameObject("ZoneDecal");
            go.transform.SetParent(transform, false);
            decal = go.AddComponent<DecalProjector>();
            decalMaterialInstance = new Material(decalMaterial);
            decal.material = decalMaterialInstance;
        }

        if (decalTexture == null || decalTextureColor != cageColor)
            BuildDecalTexture();

        // Projected straight down, a box as wide as the zone and DecalDepth above / below its centre.
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

    /// <summary>The decal fades with the overlay's alpha.</summary>
    protected void SetDecalAlpha(float a)
    {
        if (decal != null && decal.enabled)
            decal.fadeFactor = a;
    }


    // =========================
    // LIFECYCLE
    // =========================
    protected IEnumerator LifeRoutine(float duration)
    {
        yield return GrowRoutine(0f, cageHeight, growTime);

        float idleTime = Mathf.Max(0f, duration - collapseTime);
        yield return new WaitForSeconds(idleTime);

        yield return GrowRoutine(cageHeight, 0f, collapseTime);

        Deactivate();
    }

    // =========================
    // 🔴 PULSE FIX (CORE)
    // =========================
    protected virtual void UpdatePulse()
    {
        if (cageId < 0)
            return;

        float t = Mathf.Sin(Time.time * pulseSpeed) * 0.5f + 0.5f;
        float alpha = Mathf.Lerp(pulseMinAlpha, pulseMaxAlpha, t);

        // 🔴 ALWAYS derive from base color
        Color c = baseCageColor;
        c.a = alpha;

        // ===== Overlay (ground) =====
        ZoneOverlaySystem.UpdateColor(cageId, c);
        SetDecalAlpha(alpha);

        // ===== Cage (mesh) =====
        if (sharedMaterial != null)
        {
            sharedMaterial.SetColor(BaseColorID, c);
        }
    }

    // =========================
    // ANIMATION
    // =========================
    protected IEnumerator GrowRoutine(float from, float to, float time)
    {
        if (time <= 0f)
        {
            SetCageHeight(to);
            yield break;
        }

        float t = 0f;
        while (t < time)
        {
            t += Time.deltaTime;
            float h = Mathf.Lerp(from, to, t / time);
            SetCageHeight(h);
            yield return null;
        }

        SetCageHeight(to);
    }

    // =========================
    // BUILD & LAYOUT
    // =========================
    protected virtual void BuildIfNeeded()
    {
        if (quads.Count > 0)
            return;

        if (sharedMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            sharedMaterial = new Material(shader);

            sharedMaterial.SetColor(BaseColorID, baseCageColor);
            sharedMaterial.SetFloat("_Surface", 1f);
            sharedMaterial.SetFloat("_Blend", 0f);
            sharedMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            sharedMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            sharedMaterial.SetInt("_ZWrite", 0);
            sharedMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            sharedMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        for (int i = 0; i < quadCount; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.transform.SetParent(transform, false);
            Destroy(go.GetComponent<Collider>());

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = sharedMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            quads.Add(go.transform);
        }
    }

    protected virtual void LayoutQuads(float radius)
    {
        float circumference = 2f * Mathf.PI * radius;
        float quadWidth = (circumference / quadCount) * 1.0055f;

        for (int i = 0; i < quadCount; i++)
        {
            float angle = (360f / quadCount) * i;
            float rad = angle * Mathf.Deg2Rad;

            Vector3 dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
            Transform q = quads[i];

            q.localPosition = dir * radius;
            q.localRotation = Quaternion.LookRotation(-dir, Vector3.up);
            q.localScale = new Vector3(quadWidth, 1f, 1f);
        }
    }

    // =========================
    // VISUAL
    // =========================
    protected void SetCageHeight(float height)
    {
        foreach (var q in quads)
        {
            Vector3 s = q.localScale;
            s.y = height;
            q.localScale = s;
        }
    }

    protected void ApplyColor(Color c)
    {
        if (sharedMaterial != null)
            sharedMaterial.SetColor(BaseColorID, c);
    }

    protected virtual void StopAllInternalCoroutines()
    {
        if (lifeRoutine != null)
            StopCoroutine(lifeRoutine);

        lifeRoutine = null;
    }
}