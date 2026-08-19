using System.Collections;
using System.Collections.Generic;
using UnityEngine;
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

    // =========================
    // PUBLIC API
    // =========================
    public virtual void Activate(Vector3 pos, float radius, float duration)
    {
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
            overlayType
        );

        lifeRoutine = StartCoroutine(LifeRoutine(duration));
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

        gameObject.SetActive(false);
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