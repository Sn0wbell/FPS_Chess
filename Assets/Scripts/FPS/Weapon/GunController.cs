using UnityEngine;
using System.Collections;

public enum FireMode
{
    Auto,
    Burst,
    Single
}

public class GunController : WeaponController
{
    [Header("Weapon Settings")]
    public float firePointDistance = 0f;
    public float fireRate = 10f;
    public float damage = 18f;
    public float range = 120f;

    [Header("Fire Settings")]
    public FireMode currentFireMode = FireMode.Auto;
    public bool allowAuto = true;
    public bool allowBurst = true;
    public bool allowSingle = true;

    [Header("Burst Settings")]
    public float burstDelay = 0.1f;
    public float burstCooldown = 0.5f;
    public int burstCount = 3;

    [Header("Recoil Settings")]
    public bool applyRecoil = true;
    public float recoilVertical = 3.2f;
    public float recoilHorizontal = 1.5f;
    public float recoilApplySpeed = 18f;
    public float recoilReturnSpeed = 22f;
    public float aimRecoilMultiplier = 0.5f;

    [Header("Spread Settings")]
    public bool applySpread = true;
    public float minSpreadAngle = 0.8f;
    public float maxSpreadAngle = 4f;
    public float spreadIncreasePerShot = 0.35f;
    public float spreadApplySpeed = 0f;
    public float spreadReturnSpeed = 6f;
    public float spreadCrosshairSizePerAngle = 0f;

    [Header("Ammo Settings")]
    public int magazineSize = 30;
    public int currentAmmo = 30;
    public int totalAmmo = 180;
    public float reloadTime = 2f;

    [Header("Aim Settings")]
    public bool hasScope = false;
    public float aimFOV = 40f;
    public float normalFOV = 60f;
    public float aimSpeed = 10f;
    public Vector3 aimOffset = Vector3.zero;

    [Header("Crosshair Settings")]
    public float smoothSpreadSpeed = 0f;

    [Header("Bullet Settings")]
    public GameObject bulletPrefab;
    public float bulletSpeed = 80f;
    public float bulletLifeTime = 3f;

    [Header("Effects")]
    public ParticleSystem muzzleFlash;
    public AudioSource fireSound;

    // Internal state
    protected float nextTimeToFire;
    protected float nextBurstShotTime;
    protected float nextBurstTime;
    protected int shotsRemainingInBurst;
    protected bool triggerReleasedSinceLastShot = true;
    protected bool isReloading = false;
    protected bool isShooting = false;
    protected bool isAiming = false;

    protected float currentSpreadAngle;

    protected Vector2 currentRecoil;
    protected Vector2 appliedRecoil;
    protected Vector2 recoilVelocity;

    protected Vector3 defaultPosition;
    protected Vector3 aimPosition;

    protected RectTransform pointCenterScope;
    protected RectTransform pointTopScope;
    protected RectTransform pointBottomScope;
    protected RectTransform pointLeftScope;
    protected RectTransform pointRightScope;

    public delegate void OnGunStatChange();
    public event OnGunStatChange onGunStatChange;

    private void NotifyHUD()
    {
        onGunStatChange?.Invoke();
    }
    private void Awake()
    {
        defaultPosition = transform.localPosition;
        aimPosition = transform.localPosition + aimOffset;
    }
    private void Start()
    {
        currentSpreadAngle = minSpreadAngle;

        if (!IsFireModeAllowed(currentFireMode))
            currentFireMode = GetFirstAllowedMode();
    }
    FireMode GetFirstAllowedMode()
    {
        if (allowAuto) return FireMode.Auto;
        if (allowBurst) return FireMode.Burst;
        if (allowSingle) return FireMode.Single;

        Debug.LogWarning("No fire modes enabled!");
        return FireMode.Single; // Fallback
    }
    bool IsFireModeAllowed(FireMode mode)
    {
        return (mode == FireMode.Auto && allowAuto) ||
               (mode == FireMode.Burst && allowBurst) ||
               (mode == FireMode.Single && allowSingle);
    }
    bool CanFire()
    {
        return !isBlocked && !isReloading && currentAmmo > 0 && Time.time >= nextTimeToFire;
    }
    // --- EXTERNAL API ---

    public FireMode GetFireMode() => currentFireMode;
    public (int current, int total) GetAmmoStatus() => (currentAmmo, totalAmmo);
    public Vector2 GetAppliedRecoil() => appliedRecoil;
    public bool IsShooting() => isShooting;
    public bool IsReloading() => isReloading;

    public void SetTriggerReleasedSinceLastShot(bool pressed) => triggerReleasedSinceLastShot = pressed;

    public void SetAiming(bool aim) => isAiming = aim;
    public void SetCrosshair(RectTransform centerScope, RectTransform topScope, RectTransform bottomScope, RectTransform leftScope, RectTransform rightScope)
    {
        pointCenterScope = centerScope;
        pointTopScope = topScope;
        pointBottomScope = bottomScope;
        pointLeftScope = leftScope;
        pointRightScope = rightScope;
    }
    void UpdateCrosshairPoints()
    {
        if (!applySpread) return;

        float maxUISpreadCrosshairSize = maxSpreadAngle * spreadCrosshairSizePerAngle * 2f;
        float minUISpreadCrosshairSize = spreadCrosshairSizePerAngle;

        float spreadPercent = Mathf.Clamp01(currentSpreadAngle / maxSpreadAngle);
        float spreadDistance = Mathf.Lerp(minUISpreadCrosshairSize, maxUISpreadCrosshairSize, spreadPercent);

        spreadDistance = Mathf.Max(spreadDistance, minUISpreadCrosshairSize);

        if (pointTopScope)
            pointTopScope.anchoredPosition = Vector2.Lerp(pointTopScope.anchoredPosition, new Vector2(0f, spreadDistance), Time.deltaTime * smoothSpreadSpeed);

        if (pointBottomScope)
            pointBottomScope.anchoredPosition = Vector2.Lerp(pointBottomScope.anchoredPosition, new Vector2(0f, -spreadDistance), Time.deltaTime * smoothSpreadSpeed);

        if (pointLeftScope)
            pointLeftScope.anchoredPosition = Vector2.Lerp(pointLeftScope.anchoredPosition, new Vector2(-spreadDistance, 0f), Time.deltaTime * smoothSpreadSpeed);

        if (pointRightScope)
            pointRightScope.anchoredPosition = Vector2.Lerp(pointRightScope.anchoredPosition, new Vector2(spreadDistance, 0f), Time.deltaTime * smoothSpreadSpeed);
    }
    public override void Tick(float deltaTime)
    {
        if (firePoint == null || isBlocked)
            return;

        isShooting = false;

        UpdateAiming();
        UpdateCrosshairPoints();
        UpdateRecoil(deltaTime);
        UpdateSpread(deltaTime);

        if (currentAmmo <= 0 && !isReloading)
        {
            StartReload();
            return;
        }
    }
    void UpdateAiming()
    {
        if (Camera.main == null) return;

        float targetFOV = isAiming ? aimFOV : normalFOV;
        Camera.main.fieldOfView = Mathf.Lerp(
            Camera.main.fieldOfView,
            targetFOV,
            Time.deltaTime * aimSpeed
        );

        transform.localPosition = Vector3.Lerp(
            transform.localPosition,
            isAiming ? aimPosition : defaultPosition,
            Time.deltaTime * aimSpeed
        );

        if (pointCenterScope != null) pointCenterScope.gameObject.SetActive(!isAiming);
    }

    public override void TryAttack()
    {
        if (!CanFire()) return;

        float time = Time.time;
        switch (currentFireMode)
        {
            case FireMode.Auto:
                if (time >= nextTimeToFire)
                {
                    nextTimeToFire = time + 1f / fireRate;
                    Fire();
                }
                break;

            case FireMode.Burst:
                // Start a new burst if trigger pressed and burst ready
                if (shotsRemainingInBurst == 0 && triggerReleasedSinceLastShot && time >= nextBurstTime)
                {
                    shotsRemainingInBurst = Mathf.Min(burstCount, currentAmmo);
                    triggerReleasedSinceLastShot = false;
                    nextBurstShotTime = time;
                    nextBurstTime = time + burstCooldown;
                    nextTimeToFire = nextBurstTime;
                }

                if (shotsRemainingInBurst > 0 && time >= nextBurstShotTime)
                {
                    nextBurstShotTime = time + burstDelay;
                    Fire();
                    shotsRemainingInBurst--;
                }
                break;

            case FireMode.Single:
                if (triggerReleasedSinceLastShot && time >= nextTimeToFire)
                {
                    Fire();
                    nextTimeToFire = time + 1f / fireRate;
                    triggerReleasedSinceLastShot = false;
                }
                break;
        }
    }
    public void SwitchFireMode()
    {
        int modeCount = System.Enum.GetValues(typeof(FireMode)).Length;

        for (int i = 1; i <= modeCount; i++) // Loop at most once through all options
        {
            FireMode nextMode = (FireMode)(((int)currentFireMode + i) % modeCount);

            if ((nextMode == FireMode.Auto && allowAuto) ||
                (nextMode == FireMode.Burst && allowBurst) ||
                (nextMode == FireMode.Single && allowSingle))
            {
                currentFireMode = nextMode;
                NotifyHUD();
                Debug.Log("Switched to: " + currentFireMode);
                break;
            }
        }
    }

    // --- FIRE LOGIC ---
    protected virtual void Fire()
    {
        if (bulletPrefab == null || firePoint == null) return;
        if (currentAmmo <= 0 || isReloading) return;

        currentAmmo--;
        isShooting = true;

        NotifyHUD();
        // Recoil
        if (applyRecoil)
        {
            float vertical = recoilVertical;
            float horizontal = Random.Range(-recoilHorizontal, recoilHorizontal);
            if (isAiming) vertical *= aimRecoilMultiplier;
            currentRecoil += new Vector2(horizontal, vertical);
        }

        // Spread
        Quaternion bulletRotation = firePoint.rotation;
        if (applySpread)
        {
            currentSpreadAngle = Mathf.Clamp(currentSpreadAngle + spreadIncreasePerShot, minSpreadAngle, maxSpreadAngle);

            float spreadRad = currentSpreadAngle * Mathf.Deg2Rad;
            Vector2 rand = Random.insideUnitCircle * Mathf.Tan(spreadRad);

            Vector3 spreadDir = firePoint.forward + firePoint.up * rand.y + firePoint.right * rand.x;
            bulletRotation = Quaternion.LookRotation(spreadDir.normalized);
        }

        // Spawn bullet
        var bullet = BulletPoolManager.Instance.GetBullet(firePoint.position, bulletRotation);
        if (bullet.TryGetComponent<Bullet>(out var bulletScript))
        {
            bulletScript.Speed = bulletSpeed;
            bulletScript.LifeTime = bulletLifeTime;
            bulletScript.BaseDamage = damage;
            bulletScript.MaxRange = range;
            bulletScript.Source = gameObject;

            bulletScript.Fire(firePoint.forward);
        }

        // Effects
        if (muzzleFlash != null) muzzleFlash.Play();
        if (fireSound != null) fireSound.Play();
    }

    private void UpdateRecoil(float deltaTime)
    {
        if (!applyRecoil) return;
        appliedRecoil = Vector2.SmoothDamp(appliedRecoil, currentRecoil, ref recoilVelocity, 1f / recoilApplySpeed, Mathf.Infinity, deltaTime);
        Quaternion recoilRotation = Quaternion.Euler(-appliedRecoil.y, appliedRecoil.x, 0f);
        transform.localRotation = recoilRotation;
        currentRecoil = Vector2.Lerp(currentRecoil, Vector2.zero, recoilReturnSpeed * deltaTime);
    }

    private void UpdateSpread(float deltaTime)
    {
        if (!applySpread) return;
        if (!isShooting)
            currentSpreadAngle = Mathf.MoveTowards(currentSpreadAngle, minSpreadAngle, spreadReturnSpeed * deltaTime);
    }

    // --- RELOAD ---
    public void StartReload()
    {
        if (!isReloading && currentAmmo < magazineSize && totalAmmo > 0)
            StartCoroutine(Reload());
    }

    private IEnumerator Reload()
    {
        if (isReloading || currentAmmo == magazineSize || totalAmmo <= 0) yield break;

        isReloading = true;
        Debug.Log("Reloading...");

        yield return new WaitForSeconds(reloadTime);

        int neededAmmo = magazineSize - currentAmmo;
        int ammoToLoad = Mathf.Min(neededAmmo, totalAmmo);

        currentAmmo += ammoToLoad;
        totalAmmo -= ammoToLoad;

        isReloading = false;
        NotifyHUD();
        nextTimeToFire = Time.time;
    }


    // --- INITIALIZATION ---
    public override void BindFirePoint(Transform point)
    {
        firePoint = point;
    }
}
