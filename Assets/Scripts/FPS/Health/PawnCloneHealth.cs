using System;
using UnityEngine;

/// <summary>
/// A Pawn decoy: scatters on spawn, then stands still; has a little health; deals no damage.
/// Reports who killed it (the Pawn reveals that piece to its side). Stays inside the arena.
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
    private const float ArenaMargin = 0.5f;

    private CharacterController controller;

    // Scatter simulation
    private Vector3 scatterVelocity;
    private const float Gravity = -25f;

    /// <summary>Hit to death and dissolving: no longer a valid target.</summary>
    public bool IsDying => dying;

    public float CurrentHealth => currentHealth;
    public float MaxHealth => maxHealth;

    /// <summary>Gravity used by the scatter flight (for landing prediction).</summary>
    public static float ScatterGravity => Gravity;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();

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

        scatterVelocity = initialVelocity;
        hasArena = ArenaArea.TryGetBounds(out arena);

        timer = 0f;
        dying = false;

        gameObject.SetActive(true);
        DisableWeaponDamage();
    }

    private void Update()
    {
        if (dying)
            return;

        timer += Time.deltaTime;
        if (timer >= lifetime)
        {
            Die(killedBy: null);
            return;
        }

        if (controller == null)
            return;

        scatterVelocity.y += Gravity * Time.deltaTime;

        CollisionFlags flags = controller.Move(scatterVelocity * Time.deltaTime);

        if ((flags & CollisionFlags.Below) != 0 && scatterVelocity.y < 0f)
        {
            scatterVelocity.y = 0f;
            scatterVelocity.x *= 0.15f;
            scatterVelocity.z *= 0.15f;
        }

        // Never leave the arena: stop at its edge like at a wall.
        if (hasArena && !ArenaArea.ContainsXZ(arena, transform.position, ArenaMargin))
        {
            Vector3 clamped = ArenaArea.ClampXZ(arena, transform.position, ArenaMargin);
            controller.enabled = false;
            transform.position = clamped;
            controller.enabled = true;
            scatterVelocity.x = 0f;
            scatterVelocity.z = 0f;
        }
    }

    public void TakeDamage(DamageInfo info)
    {
        if (dying || info.amount <= 0f)
            return;

        currentHealth -= info.amount;
        if (currentHealth <= 0f)
        {
            currentHealth = 0f;
            Die(killedBy: info);
        }
    }

    private void Die(DamageInfo? killedBy)
    {
        if (dying)
            return;

        dying = true;

        if (destroyVFX)
            Instantiate(destroyVFX, transform.position, Quaternion.identity);

        if (killedBy.HasValue)
            onKilled?.Invoke(this, killedBy.Value);

        Invoke(nameof(ReturnToPool), dissolveTime);
    }

    private void ReturnToPool()
    {
        CancelInvoke();
        if (pool != null) pool.Return(this);
        else gameObject.SetActive(false);
    }

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
