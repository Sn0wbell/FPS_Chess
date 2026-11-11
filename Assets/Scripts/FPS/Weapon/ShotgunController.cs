using UnityEngine;
using static Unity.VisualScripting.Member;

public class ShotgunController : GunController
{
    [Header("Shotgun Settings")]
    [Tooltip("Number of pellets fired per shot")]
    public int pelletCount = 8;

    [Tooltip("Optional damage multiplier per pellet (usually < 1)")]
    public float pelletDamageMultiplier = 1f;

    [Tooltip("How wide the pellets fan out around the shot's line (half-angle, degrees): the size of the pattern. " +
             "Fixed: it does not grow with firing nor shrink when aiming. Where that line goes (how far off the " +
             "crosshair the whole pattern lands) is the gun's own spread: min / max / increase per shot / aiming.")]
    public float pelletSpreadAngle = 5f;

    private bool hasOneShotModifier = false;
    private float oneShotDamageMultiplier = 1f;
    private float oneShotSpreadMultiplier = 1f;

    public void ApplyOneShotModifier(float damageMultiplier, float spreadMultiplier)
    {
        hasOneShotModifier = true;
        oneShotDamageMultiplier = Mathf.Max(0f, damageMultiplier);
        oneShotSpreadMultiplier = Mathf.Max(0f, spreadMultiplier);
    }

    protected override bool Fire()
    {
        if (bulletPrefab == null || attackPoint == null) return false;
        if (currentAmmo <= 0 || isReloading) return false;

        currentAmmo--;
        isShooting = true;

        NotifyHUD();

        // -----------------------------
        // Resolve one-shot modifiers
        // -----------------------------
        float finalDamage = damage * ConsumeShotDamageMultiplier(); // one shot = all pellets
        float patternAngle = pelletSpreadAngle;

        if (hasOneShotModifier)
        {
            finalDamage *= oneShotDamageMultiplier;
            patternAngle *= oneShotSpreadMultiplier;
        }

        // Where the whole pattern goes: the gun's spread, exactly like any gun (grows per shot up to max, back to
        // min over time, tighter while aiming). Once per shot, not per pellet.
        Vector3 shotLine = attackPoint.forward;
        if (applySpread)
        {
            currentSpreadAngle = Mathf.Clamp(currentSpreadAngle + spreadIncreasePerShot, minSpreadAngle, maxSpreadAngle);
            if (isAiming)
                currentSpreadAngle *= aimSpreadMultiplier;
            shotLine = RandomInCone(attackPoint.forward, attackPoint.up, attackPoint.right, currentSpreadAngle);
        }
        Quaternion lineRotation = Quaternion.LookRotation(shotLine, attackPoint.up);

        // -----------------------------
        // Recoil: exactly like the other guns, once per shot (not per pellet)
        // -----------------------------
        ApplyShotRecoil();

        // -----------------------------
        // Fire pellets
        // -----------------------------
        for (int i = 0; i < pelletCount; i++)
        {
            // The pattern: each pellet fans out around the shot's line (fixed size, not narrowed while aiming).
            Quaternion pelletRotation = lineRotation;

            if (applySpread)
            {
                Vector3 pelletDir = RandomInCone(lineRotation * Vector3.forward, lineRotation * Vector3.up, lineRotation * Vector3.right, patternAngle);
                pelletRotation = Quaternion.LookRotation(pelletDir);
            }

            var bullet = BulletPoolManager.Instance.GetBullet(attackPoint.position, pelletRotation);
            if (bullet.TryGetComponent<Bullet>(out var bulletScript))
            {
                bulletScript.Speed = bulletSpeed;
                bulletScript.LifeTime = bulletLifeTime;
                bulletScript.BaseDamage = finalDamage * pelletDamageMultiplier;
                bulletScript.MaxRange = range;
                bulletScript.Source = gameObject;

                bulletScript.Fire(pelletRotation * Vector3.forward); // each pellet flies its own way in the cone
            }
        }

        // -----------------------------
        // Effects (play once per shot)
        // -----------------------------
        if (muzzleFlash != null) muzzleFlash.Play();
        if (fireSound != null) fireSound.Play();

        // -----------------------------
        // Consume one-shot modifier
        // -----------------------------
        if (hasOneShotModifier)
        {
            hasOneShotModifier = false;
            oneShotDamageMultiplier = 1f;
            oneShotSpreadMultiplier = 1f;
        }

        return true;
    }

    /// <summary>A random direction within <paramref name="halfAngle"/> degrees of <paramref name="forward"/>.</summary>
    private static Vector3 RandomInCone(Vector3 forward, Vector3 up, Vector3 right, float halfAngle)
    {
        Vector2 rand = Random.insideUnitCircle * Mathf.Tan(Mathf.Max(0f, halfAngle) * Mathf.Deg2Rad);
        return (forward + up * rand.y + right * rand.x).normalized;
    }
}