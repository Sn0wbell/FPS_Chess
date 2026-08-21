using UnityEngine;
using static Unity.VisualScripting.Member;

public class ShotgunController : GunController
{
    [Header("Shotgun Settings")]
    [Tooltip("Number of pellets fired per shot")]
    public int pelletCount = 8;

    [Tooltip("Optional damage multiplier per pellet (usually < 1)")]
    public float pelletDamageMultiplier = 1f;

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
        if (bulletPrefab == null || firePoint == null) return false;
        if (currentAmmo <= 0 || isReloading) return false;

        currentAmmo--;
        isShooting = true;

        // -----------------------------
        // Resolve one-shot modifiers
        // -----------------------------
        float finalDamage = damage;
        float spreadAngle = currentSpreadAngle;

        if (hasOneShotModifier)
        {
            finalDamage *= oneShotDamageMultiplier;
            spreadAngle *= oneShotSpreadMultiplier;
        }

        // Shotgun DOES NOT accumulate spread per pellet
        // (do NOT increase currentSpreadAngle here)

        // -----------------------------
        // Recoil (same as base gun)
        // -----------------------------
        if (applyRecoil)
        {
            float vertical = recoilVertical;
            float horizontal = Random.Range(-recoilHorizontal, recoilHorizontal);
            if (isAiming) vertical *= aimRecoilMultiplier;
            currentRecoil += new Vector2(horizontal, vertical);
        }

        // -----------------------------
        // Fire pellets
        // -----------------------------
        for (int i = 0; i < pelletCount; i++)
        {
            Quaternion pelletRotation = firePoint.rotation;

            if (applySpread)
            {
                float spreadRad = spreadAngle * Mathf.Deg2Rad;
                Vector2 rand = Random.insideUnitCircle * Mathf.Tan(spreadRad);

                Vector3 spreadDir =
                    firePoint.forward +
                    firePoint.up * rand.y +
                    firePoint.right * rand.x;

                pelletRotation = Quaternion.LookRotation(spreadDir.normalized);
            }

            var bullet = BulletPoolManager.Instance.GetBullet(firePoint.position, pelletRotation);
            if (bullet.TryGetComponent<Bullet>(out var bulletScript))
            {
                bulletScript.Speed = bulletSpeed;
                bulletScript.LifeTime = bulletLifeTime;
                bulletScript.BaseDamage = finalDamage * pelletDamageMultiplier;
                bulletScript.MaxRange = range;
                bulletScript.Source = gameObject;

                bulletScript.Fire(firePoint.forward);
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
}