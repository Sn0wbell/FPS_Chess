using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// How every piece dies in the arena (player, AI pieces and pawn clones): its model breaks into
/// fragments, like a captured piece on the chess board (same BrokenShatterGenerator, sized for FPS pieces).
/// The body is hidden (weapon included); Restore() shows it again (revive, pooled clone reused).
/// </summary>
public static class PieceDeathEffect
{
    [System.Serializable]
    public struct Settings
    {
        [Tooltip("Bursts spread up the body (feet to head); each one throws 'fragments per burst' pieces.")]
        public int bursts;
        public int fragmentsPerBurst;
        public float fragmentSize;
        [Tooltip("Impulse of the burst on each fragment (a fragment weighs 0.25 kg).")]
        public float explosionForce;
        public float explosionRadius;
        public float upwardModifier;
        [Tooltip("Seconds before the fragments disappear.")]
        public float lifetime;
    }

    public static Settings Default => new Settings
    {
        bursts = 3,
        fragmentsPerBurst = 10,
        fragmentSize = 0.2f,
        explosionForce = 1f,
        explosionRadius = 1f,
        upwardModifier = 0.2f,
        lifetime = 3f
    };

    private static BrokenShatterGenerator generator;
    private static readonly Dictionary<GameObject, List<Renderer>> hidden = new Dictionary<GameObject, List<Renderer>>();

    /// <summary>The fragments thrown by the last Shatter (tests and effects can follow them).</summary>
    public static int LastFragmentCount { get; private set; }

    /// <summary>Breaks the piece's model into fragments where it stands and hides its body.</summary>
    public static void Shatter(GameObject piece)
    {
        if (piece == null || hidden.ContainsKey(piece))
            return;

        // Forget pieces destroyed since (arena cleared, scene reloaded).
        var gone = new List<GameObject>();
        foreach (var key in hidden.Keys)
            if (key == null)
                gone.Add(key);
        foreach (var key in gone)
            hidden.Remove(key);

        Settings s = ArenaController.Current != null ? ArenaController.Current.DeathEffect : Default;

        // The body's volume: its CharacterController (else its renderers).
        Bounds body;
        var cc = piece.GetComponent<CharacterController>();
        if (cc != null)
            body = cc.bounds;
        else
            body = new Bounds(piece.transform.position + Vector3.up, new Vector3(0.8f, 2f, 0.8f));

        // Fragments take the body's material (the biggest renderer: the model, not the weapon).
        var renderers = new List<Renderer>();
        Renderer main = null;
        float biggest = -1f;
        foreach (var r in piece.GetComponentsInChildren<Renderer>())
        {
            if (r == null || !r.enabled)
                continue;
            renderers.Add(r);
            if (r.gameObject.name == "XRayOverlay")
                continue;
            float size = r.bounds.size.sqrMagnitude;
            if (size > biggest)
            {
                biggest = size;
                main = r;
            }
        }

        BrokenShatterGenerator gen = Generator(s);
        int bursts = Mathf.Max(1, s.bursts);
        for (int i = 0; i < bursts; i++)
        {
            float k = (i + 0.5f) / bursts;
            Vector3 at = new Vector3(body.center.x, Mathf.Lerp(body.min.y, body.max.y, k), body.center.z);
            _ = gen.ShatterAt(at, main != null ? main.sharedMaterial : null);
        }
        LastFragmentCount = bursts * gen.fragments;

        foreach (var r in renderers)
            r.enabled = false;
        hidden[piece] = renderers;
    }

    public static bool IsShattered(GameObject piece) => piece != null && hidden.ContainsKey(piece);

    /// <summary>Shows the body hidden by Shatter again.</summary>
    public static void Restore(GameObject piece)
    {
        if (piece == null || !hidden.TryGetValue(piece, out var renderers))
            return;
        foreach (var r in renderers)
            if (r != null)
                r.enabled = true;
        hidden.Remove(piece);
    }

    private static BrokenShatterGenerator Generator(Settings s)
    {
        if (generator == null)
        {
            var go = new GameObject("PieceDeathShatter");
            generator = go.AddComponent<BrokenShatterGenerator>();
            generator.addColliders = true; // fragments land on the ground instead of falling through it
            generator.SetFragmentLayer(2); // Ignore Raycast: never blocks shots, aims or spawn checks
        }

        generator.fragments = Mathf.Clamp(s.fragmentsPerBurst, 1, 64);
        generator.fragmentSize = s.fragmentSize;
        generator.explosionForce = s.explosionForce;
        generator.explosionRadius = s.explosionRadius;
        generator.upwardModifier = s.upwardModifier;
        generator.lifetime = s.lifetime;
        return generator;
    }
}
