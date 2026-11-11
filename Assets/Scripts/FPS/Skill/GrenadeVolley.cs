using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Grenades already fired by a queen. Lives on its own object, so the grenades explode on time
/// even if the queen dies meanwhile; they keep the queen's side (never hurt her allies).
/// Explosion: damage falling off with distance (enemies only, the strongest zone counts),
/// a blast knock-back (everyone), both blocked by walls / terrain between the zone and the piece.
/// </summary>
public class GrenadeVolley : MonoBehaviour, ITeamOwned
{
    public Team Team { get; private set; }

    public struct Settings
    {
        public float radius;
        public float heightRange;
        public float minDamage;
        public float maxDamage;
        public AnimationCurve damageCurve;
        public float explosionForce;
        public float bounceFactor;
        public LayerMask obstacleLayer;
        public float markSeconds;   // enemies caught in a blast are revealed to the queen's side this long
    }

    private Settings settings;
    private ChessPieceFPSController owner;
    private bool hasTeam; // fired by a queen (her side is known)
    private GameObject damageSource;
    private float explodeAt;
    private readonly List<RedZone> zones = new();
    private Action<RedZone> returnZone;
    private Action onExploded;
    private bool exploded;

    public IReadOnlyList<RedZone> Zones => zones;

    public static GrenadeVolley Create(ChessPieceFPSController owner, Settings settings, float explodeAt,
        Action<RedZone> returnZone, Action onExploded)
    {
        var volley = new GameObject("GrenadeVolley").AddComponent<GrenadeVolley>();
        volley.owner = owner;
        volley.Team = owner != null ? owner.Team : Team.White;
        volley.hasTeam = owner != null;
        volley.damageSource = owner != null ? owner.gameObject : null;
        volley.settings = settings;
        volley.explodeAt = explodeAt;
        volley.returnZone = returnZone;
        volley.onExploded = onExploded;
        return volley;
    }

    public void Add(RedZone zone)
    {
        if (zone != null)
            zones.Add(zone);
    }

    /// <summary>The queen is gone: the grenades already fired still explode; their zones are destroyed afterwards.</summary>
    public void Detach()
    {
        returnZone = null;
        onExploded = null;
        if (zones.Count == 0)
            Destroy(gameObject);
    }

    private void Update()
    {
        if (!exploded && Time.time >= explodeAt)
            Explode();
    }

    private void Explode()
    {
        exploded = true;

        var targets = UnityEngine.Object.FindObjectsByType<ChessPieceFPSController>(FindObjectsSortMode.None);
        Dictionary<ChessPieceFPSController, float> damageMap = new();
        Dictionary<ChessPieceFPSController, (float t, Vector3 from)> blastMap = new();

        foreach (var rz in zones)
        {
            if (rz == null) continue;
            Vector3 center = rz.transform.position;

            foreach (var target in targets)
            {
                if (!SkillTargeting.IsAlive(target) || target == owner)
                    continue;

                Vector3 delta = target.transform.position - center;
                if (Mathf.Abs(delta.y) > settings.heightRange)
                    continue;

                float dist = new Vector2(delta.x, delta.z).magnitude;
                if (dist > settings.radius)
                    continue;

                if (BlockedByWallOrTerrain(center, target))
                    continue;

                float t = Mathf.Clamp01(dist / settings.radius);
                float curveValue = settings.damageCurve != null && settings.damageCurve.length > 0
                    ? Mathf.Clamp01(settings.damageCurve.Evaluate(1f - t))
                    : 1f - t;

                float dmg = Mathf.Lerp(settings.minDamage, settings.maxDamage, curveValue);
                if (!damageMap.TryGetValue(target, out var cur) || dmg > cur)
                    damageMap[target] = dmg;

                // The nearest zone centre gives the strongest blast.
                if (!blastMap.TryGetValue(target, out var b) || t < b.t)
                    blastMap[target] = (t, center);
            }
        }

        foreach (var pair in damageMap)
        {
            // Travels from the nearest zone centre to the piece (a raised king shield facing it reduces it).
            Vector3 from = blastMap[pair.Key].from;
            Vector3 travel = pair.Key.transform.position - from;
            travel.y = 0f;
            SkillTargeting.TryDealDamage(this, pair.Key.GetComponent<IDamageable>(), new DamageInfo
            {
                amount = pair.Value,
                hitPoint = pair.Key.transform.position,
                hitDirection = travel.sqrMagnitude > 1e-6f ? travel.normalized : Vector3.up,
                source = damageSource,
                type = DamageType.Explosive
            });
        }

        // Blast like the knight's landing: away from the zone centre, fading with distance, small bounce
        // (everyone, allies too; a piece moved by another skill is knocked too: the later skill wins).
        foreach (var pair in blastMap)
        {
            if (!SkillTargeting.IsAlive(pair.Key))
                continue;
            if (hasTeam && pair.Key.Team != Team)
                RevealSystem.Reveal(pair.Key, Team, settings.markSeconds);
            pair.Key.AddExternalImpulse(SkillTargeting.RadialKnock(pair.Value.from, pair.Key.transform.position,
                settings.radius, settings.explosionForce, settings.bounceFactor));
        }

        foreach (var rz in zones)
        {
            if (rz == null) continue;
            if (returnZone != null)
                returnZone(rz);
            else
            {
                rz.Deactivate();
                Destroy(rz.gameObject); // the queen and her pool are gone
            }
        }
        zones.Clear();

        onExploded?.Invoke();
        Destroy(gameObject);
    }

    /// <summary>A wall or the terrain between the blast (just above the zone's surface) and the piece's body.</summary>
    private bool BlockedByWallOrTerrain(Vector3 zoneCenter, ChessPieceFPSController target)
    {
        Vector3 from = zoneCenter + Vector3.up * 0.5f;
        var cc = target.GetComponent<CharacterController>();
        Vector3 to = cc != null ? cc.bounds.center : target.transform.position + Vector3.up;
        Vector3 d = to - from;
        float len = d.magnitude;
        return len > 0.01f && Physics.Raycast(from, d / len, len, settings.obstacleLayer, QueryTriggerInteraction.Ignore);
    }
}
