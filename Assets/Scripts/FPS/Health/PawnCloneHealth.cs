using System;
using UnityEngine;

/// <summary>
/// A Pawn decoy: thrown on spawn, then stands still; has a little health; deals no damage.
/// It moves with the same physics as a real piece (ChessPieceFPSController: gravity, knock-back,
/// being pushed / pulled), so every skill effect treats it exactly like the real pawn.
/// Reports who killed it (the Pawn reveals that piece to its side). A clone that leaves the
/// arena (falls off the floor) is defeated.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PawnCloneHealth : MonoBehaviour, IDamageable
{
    private float lifetime;
    private float dissolveTime;
    private float timer;
    private bool dying;

    private float maxHealth = 30f;
    private float currentHealth;

    private GameObject destroyVFX;
    private PawnClonePool pool;
    private Action<PawnCloneHealth, DamageInfo> onKilled;

    private bool hasArena;
    private Bounds arena;

    private ChessPieceFPSController fps;

    /// <summary>Hit to death and dissolving: no longer a valid target.</summary>
    public bool IsDying => dying;

    public float CurrentHealth => currentHealth;
    public float MaxHealth => maxHealth;

    private void Awake()
    {
        fps = GetComponent<ChessPieceFPSController>();

        var marker = GetComponent<PawnCloneMarker>();
        if (!marker)
            marker = gameObject.AddComponent<PawnCloneMarker>();
        marker.IsClone = true;
    }

    public void Activate(
        float life,
        float dissolve,
        GameObject vfx,
        PawnClonePool ownerPool,
        Vector3 initialVelocity,
        float health = 30f,
        Action<PawnCloneHealth, DamageInfo> killedCallback = null)
    {
        CancelInvoke();

        lifetime = Mathf.Max(0.01f, life);
        dissolveTime = dissolve;
        destroyVFX = vfx;
        pool = ownerPool;
        onKilled = killedCallback;

        maxHealth = Mathf.Max(1f, health);
        currentHealth = maxHealth;

        hasArena = ArenaController.Current != null && ArenaController.Current.TryGetBounds(out arena);

        timer = 0f;
        dying = false;

        PieceDeathEffect.Restore(gameObject); // a reused clone that broke apart last time
        gameObject.SetActive(true);
        if (fps != null)
        {
            fps.RestoreCloneBody();
            RevealSystem.Forget(fps); // a new decoy: not marked like the last one
        }
        DisableWeaponDamage();

        // Thrown like the real pawn (same flight, same landing).
        if (fps != null)
            fps.AddExternalImpulse(initialVelocity);
    }

    private void Update()
    {
        if (dying)
            return;

        timer += Time.deltaTime;
        if (timer >= lifetime)
        {
            Die(killedBy: null, shatter: true); // its time is up: it breaks apart too (not a kill: nobody revealed)
            return;
        }

        // Out of the arena (past its edge or fallen below it): defeated, nobody to reveal.
        if (hasArena && (!ArenaController.ContainsXZ(arena, transform.position) || transform.position.y < arena.min.y))
            Die(killedBy: null, shatter: true);
    }

    public void TakeDamage(DamageInfo info)
    {
        if (dying || info.amount <= 0f)
            return;

        currentHealth -= info.amount;
        if (currentHealth <= 0f)
        {
            currentHealth = 0f;
            Die(killedBy: info, shatter: true);
        }
    }

    private void Die(DamageInfo? killedBy, bool shatter)
    {
        if (dying)
            return;

        dying = true;

        // Defeated: it breaks apart like a real piece (its hidden body no longer moves nor blocks anyone).
        if (shatter)
        {
            PieceDeathEffect.Shatter(gameObject);
            if (fps != null)
                fps.BreakCloneBody();
        }

        if (destroyVFX)
            Instantiate(destroyVFX, transform.position, Quaternion.identity);

        if (killedBy.HasValue)
            onKilled?.Invoke(this, killedBy.Value);

        Invoke(nameof(ReturnToPool), dissolveTime);
    }

    private void ReturnToPool()
    {
        CancelInvoke();
        if (ArenaController.Current != null && fps != null)
            ArenaController.Current.UnregisterClone(this, fps.Team);
        if (pool != null) pool.Return(this);
        else gameObject.SetActive(false);
    }

    /// <summary>Removed by its pawn (skill over / used again): breaks apart like when its time is up.</summary>
    public void Vanish() => Die(killedBy: null, shatter: true);

    public void ForceDespawn()
    {
        CancelInvoke();
        dying = false;
        timer = 0f;
        gameObject.SetActive(false);
        if (pool != null) pool.Return(this); // idempotent: safe for a clone already returned
    }

    public void CancelAll()
    {
        CancelInvoke();
        dying = false;
        timer = 0f;
        gameObject.SetActive(false);
    }

    /// <summary>Any weapon a clone carries is visual only.</summary>
    private void DisableWeaponDamage()
    {
        foreach (var g in GetComponentsInChildren<GunController>(true))
            g.setDamage(0f);

        foreach (var m in GetComponentsInChildren<MeleeWeaponController>(true))
            m.damage = 0f;
    }
}
