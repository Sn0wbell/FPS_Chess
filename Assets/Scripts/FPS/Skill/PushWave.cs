using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The queen's shockwave, on its own object: once started it expands to its full radius and pushes whatever it
/// reaches, even if the queen dies or her skill is cancelled meanwhile. Its centre stays where it was cast.
/// A piece is pushed the frame the sphere's surface reaches its body (the drawn sphere and the push share one
/// clock), away from the centre: full force at the centre, fading to minForceShare at the edge, small bounce.
/// Pieces moved by another skill are pushed too (the later skill wins; a bishop's rope keeps its piece swinging).
/// Rook walls it reaches fall over sideways, away from it.
/// </summary>
public class PushWave : MonoBehaviour
{
    public struct Settings
    {
        public float radius;
        public float force;
        public float minForceShare; // share of the force left at the edge of the radius
        public float bounceFactor;
        public float scaleSpeed;    // the sphere's diameter grows this much per second
        public LayerMask mask;      // pieces + rook walls
        public GameObject spherePrefab;
        public float maxAlpha;      // sphere alpha at the start...
        public float minAlpha;      // ...fading to this at the full radius
    }

    private Settings settings;
    private ChessPieceFPSController owner;
    private Vector3 center;
    private float elapsed;
    private bool done;
    private readonly HashSet<ChessPieceFPSController> reachedPieces = new();
    private readonly HashSet<WallSegment> reachedWalls = new();
    private Action<ChessPieceFPSController> onPiecePushed;
    private Action onFinished;

    private GameObject sphere;
    private readonly List<Material> sphereMaterials = new();

    /// <summary>Radius the sphere has reached so far.</summary>
    public float CurrentRadius => Mathf.Min(elapsed * settings.scaleSpeed * 0.5f, settings.radius);

    public static PushWave Create(ChessPieceFPSController owner, Vector3 center, Settings settings,
        Action<ChessPieceFPSController> onPiecePushed, Action onFinished)
    {
        var wave = new GameObject("PushWave").AddComponent<PushWave>();
        wave.transform.position = center;
        wave.owner = owner;
        wave.center = center;
        wave.settings = settings;
        wave.onPiecePushed = onPiecePushed;
        wave.onFinished = onFinished;
        wave.SpawnSphere();
        return wave;
    }

    /// <summary>The queen is gone or her skill ended: the wave goes on by itself, nobody is told about it.</summary>
    public void Detach()
    {
        onPiecePushed = null;
        onFinished = null;
    }

    private void Update()
    {
        if (done)
            return;

        elapsed += Time.deltaTime;
        float r = CurrentRadius;
        UpdateSphere(r);
        PushReached(r);

        if (r >= settings.radius)
        {
            done = true;
            DespawnSphere();
            onFinished?.Invoke();
            Destroy(gameObject);
        }
    }

    private void PushReached(float r)
    {
        if (r <= 0f)
            return;

        foreach (var hit in Physics.OverlapSphere(center, r, settings.mask, QueryTriggerInteraction.Ignore))
        {
            ChessPieceFPSController target = SkillTargeting.PieceOf(hit);
            if (target != null)
            {
                if (!SkillTargeting.IsAffectable(owner, target) || !reachedPieces.Add(target))
                    continue;
                PushPiece(target, hit);
                continue;
            }

            WallSegment wall = hit.GetComponentInParent<WallSegment>();
            if (wall != null && reachedWalls.Add(wall) && wall.gameObject.activeInHierarchy)
                wall.Push(Knock(wall.transform.position, Vector3.Distance(center, hit.ClosestPoint(center))) * SkillTargeting.PieceMass);
        }
    }

    /// <summary>Knock-back away from the centre (it cancels a leap / dash; a pull is left for this push).</summary>
    private void PushPiece(ChessPieceFPSController target, Collider body)
    {
        float distance = Vector3.Distance(center, SkillTargeting.ClosestPointOnBody(body, center));
        target.AddExternalImpulse(Knock(target.transform.position, distance));
        onPiecePushed?.Invoke(target);
    }

    /// <summary>Away from the centre (horizontal): force x (1 at the centre .. minForceShare at the edge), plus the bounce.</summary>
    private Vector3 Knock(Vector3 at, float distance)
    {
        Vector3 flat = at - center;
        flat.y = 0f;
        flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;

        float k = Mathf.Clamp01(distance / Mathf.Max(0.01f, settings.radius));
        float strength = settings.force * Mathf.Lerp(1f, settings.minForceShare, k);
        return flat * strength + Vector3.up * (settings.bounceFactor * strength);
    }

    // =========================
    // SPHERE VISUAL
    // =========================
    private void SpawnSphere()
    {
        if (settings.spherePrefab == null)
            return;

        sphere = Instantiate(settings.spherePrefab, center, Quaternion.identity);
        foreach (var r in sphere.GetComponentsInChildren<Renderer>())
            foreach (var mat in r.materials) // instanced once here, not every frame
            {
                mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                sphereMaterials.Add(mat);
            }
        sphere.transform.localScale = Vector3.zero;
    }

    private void UpdateSphere(float r)
    {
        if (sphere == null)
            return;

        float alpha = Mathf.Lerp(settings.maxAlpha, settings.minAlpha, r / Mathf.Max(0.01f, settings.radius));
        foreach (var mat in sphereMaterials)
        {
            if (mat == null) continue;
            Color c = mat.color;
            c.a = alpha;
            mat.color = c;
        }
        sphere.transform.localScale = Vector3.one * (r * 2f);
    }

    private void DespawnSphere()
    {
        foreach (var mat in sphereMaterials)
            if (mat != null)
                Destroy(mat);
        sphereMaterials.Clear();

        if (sphere)
            Destroy(sphere);
        sphere = null;
    }

    private void OnDestroy() => DespawnSphere();
}
