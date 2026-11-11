using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bullet impact effects (bullet holes, blood), pooled per prefab instead of one new object per hit:
/// at most <c>capacity</c> of each kind at once (when full, the oldest one is reused), each shown for
/// <c>lifetime</c> seconds. An effect stays stuck to what was hit; if that is destroyed, the effect goes with it
/// and the pool simply forgets it.
/// </summary>
public static class ImpactEffectPool
{
    private sealed class Pool
    {
        public readonly List<PooledImpact> items = new();
    }

    private static readonly Dictionary<GameObject, Pool> pools = new();
    private static Transform root;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        pools.Clear();
        root = null;
    }

    /// <param name="highlight">The hole glows in this colour while its splash lasts (null: no glow).</param>
    public static void Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform attachTo, float lifetime, int capacity,
        Color? highlight = null)
    {
        if (prefab == null || capacity <= 0)
            return;

        if (!pools.TryGetValue(prefab, out Pool pool))
            pools[prefab] = pool = new Pool();

        pool.items.RemoveAll(e => e == null); // destroyed together with what it was stuck to

        float now = Time.time;
        PooledImpact effect = null;
        foreach (PooledImpact e in pool.items)
            if (!e.gameObject.activeSelf || now >= e.ShownUntil)
            {
                effect = e; // free
                break;
            }

        if (effect == null)
        {
            if (pool.items.Count < capacity)
            {
                effect = Create(prefab);
                pool.items.Add(effect);
            }
            else
            {
                effect = pool.items[0]; // full: the oldest one is reused
                foreach (PooledImpact e in pool.items)
                    if (e.ShownUntil < effect.ShownUntil)
                        effect = e;
            }
        }

        Transform t = effect.transform;
        t.SetParent(Root(), false);
        t.SetPositionAndRotation(position, rotation);
        t.localScale = prefab.transform.localScale;
        if (attachTo != null)
            t.SetParent(attachTo, true); // stays stuck to what was hit (as before)

        effect.Show(now + Mathf.Max(0f, lifetime), highlight);
    }

    private static PooledImpact Create(GameObject prefab)
    {
        GameObject go = Object.Instantiate(prefab, Root());
        go.SetActive(false);

        // The effect's own self-destruct timer (e.g. DecalDestroyer) would destroy a pooled object: turned off
        // before it ever starts (a disabled script's Start never runs).
        foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetType().Name == "DecalDestroyer")
                mb.enabled = false;

        return go.AddComponent<PooledImpact>();
    }

    private static Transform Root()
    {
        if (root == null)
            root = new GameObject("[ImpactEffectPool]").transform;
        return root;
    }
}

/// <summary>
/// One pooled impact effect: plays from the start each time it is shown, hides itself when its time is up.
/// While the splash lasts, the hole (the effect's meshes, e.g. "Decal", in the shape of its texture) glows - full, then
/// fading over the last 30%, off as soon as the last particle is gone - and its particles are drawn as glowing sparks.
/// The still hole that stays does not glow.
/// </summary>
public sealed class PooledImpact : MonoBehaviour
{
    private const float FadeShare = 0.3f;
    private const float GlowShareOfSplash = 0.25f; // the hole glows for a quarter of the splash

    private ParticleSystem[] particles;
    private readonly List<MeshRenderer> glows = new();
    private readonly List<Texture> glowShapes = new();          // each glow's shape: its decal's texture
    private ParticleSystemRenderer[] sparkRenderers;
    private Material[] sparkOriginals;                         // the particles' own look (back when not highlighted)
    private MaterialPropertyBlock block;
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private Color glowColor;
    private float glowStart;
    private float splashSeconds;
    private bool glowing;

    public float ShownUntil { get; private set; }
    public bool IsGlowing => glowing;

    public void Show(float until, Color? highlight = null)
    {
        ShownUntil = until;
        gameObject.SetActive(true);

        particles ??= GetComponentsInChildren<ParticleSystem>(true);
        foreach (ParticleSystem ps in particles)
        {
            if (ps == null) continue;
            ps.Clear(true);
            ps.Play(true);
        }

        StartGlow(highlight);
    }

    private void StartGlow(Color? highlight)
    {
        splashSeconds = SplashSeconds();
        glowing = highlight.HasValue && splashSeconds > 0f && ShotHighlight.MaterialFor(highlight.Value) != null;
        if (glowing && glows.Count == 0)
            foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>(true))
            {
                MeshRenderer copy = ShotHighlight.AddGlowCopy(r, "Glow", 1f);
                if (copy == null) continue;
                glows.Add(copy);
                glowShapes.Add(ShotHighlight.ShapeOf(r.sharedMaterial)); // the hole's own shape, not its square
            }

        glowColor = highlight ?? Color.clear;
        glowStart = Time.time;
        for (int i = 0; i < glows.Count; i++)
        {
            MeshRenderer g = glows[i];
            if (g == null) continue;
            g.enabled = glowing;
            if (glowing)
                g.sharedMaterial = ShotHighlight.MaterialFor(glowColor, glowShapes[i], onASurface: true);
        }
        SetGlowAlpha(1f);
        SetSparks(glowing);
    }

    /// <summary>The particles glow as sparks in the shot's colour (their own shape, else a soft dot), or look as they were made.</summary>
    private void SetSparks(bool on)
    {
        if (sparkRenderers == null)
        {
            sparkRenderers = GetComponentsInChildren<ParticleSystemRenderer>(true);
            sparkOriginals = new Material[sparkRenderers.Length];
            for (int i = 0; i < sparkRenderers.Length; i++)
                sparkOriginals[i] = sparkRenderers[i] != null ? sparkRenderers[i].sharedMaterial : null;
        }

        for (int i = 0; i < sparkRenderers.Length; i++)
        {
            ParticleSystemRenderer r = sparkRenderers[i];
            if (r == null) continue;
            if (!on)
            {
                r.sharedMaterial = sparkOriginals[i];
                continue;
            }
            Texture shape = ShotHighlight.ShapeOf(sparkOriginals[i]);
            r.sharedMaterial = ShotHighlight.MaterialFor(glowColor, shape != null ? shape : ShotHighlight.SparkTexture, vertexAlpha: true);
        }
    }

    /// <summary>How long the hole glows: a quarter of the splash (its longest emitter's duration + its particles' lifetime).</summary>
    private float SplashSeconds()
    {
        float longest = 0f;
        foreach (ParticleSystem ps in particles)
        {
            if (ps == null) continue;
            var main = ps.main;
            longest = Mathf.Max(longest, (main.loop ? 0f : main.duration) + main.startLifetime.constantMax);
        }
        return longest * GlowShareOfSplash;
    }

    private void SetGlowAlpha(float share)
    {
        block ??= new MaterialPropertyBlock();
        Color c = glowColor;
        c.a *= share;
        block.SetColor(ColorId, c);
        foreach (MeshRenderer g in glows)
            if (g != null)
                g.SetPropertyBlock(block);
    }

    private void StopGlow()
    {
        glowing = false;
        foreach (MeshRenderer g in glows)
            if (g != null)
                g.enabled = false;
    }

    private void Update()
    {
        if (Time.time >= ShownUntil)
        {
            StopGlow();
            gameObject.SetActive(false);
            return;
        }

        if (!glowing)
            return;

        float t = Time.time - glowStart;
        bool splashOver = t >= splashSeconds || (t > 0.1f && !AnyParticleAlive());
        if (splashOver)
        {
            StopGlow(); // the hole is still now: no more glow
            return;
        }

        float fadeFrom = splashSeconds * (1f - FadeShare);
        SetGlowAlpha(t <= fadeFrom ? 1f : 1f - (t - fadeFrom) / (splashSeconds - fadeFrom));
    }

    private bool AnyParticleAlive()
    {
        foreach (ParticleSystem ps in particles)
            if (ps != null && ps.IsAlive(true))
                return true;
        return false;
    }
}
