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
    [SerializeField] protected float firePointDistance = 0f;
    [SerializeField] protected float fireRate = 10f;
    [SerializeField] protected float damage = 18f;
    [SerializeField] protected float range = 120f;

    [Header("Fire Settings")]
    [SerializeField] protected FireMode currentFireMode = FireMode.Auto;
    [SerializeField] protected bool allowAuto = true;
    [SerializeField] protected bool allowBurst = true;
    [SerializeField] protected bool allowSingle = true;

    [Header("Burst Settings")]
    [SerializeField] protected float burstDelay = 0.1f;
    [SerializeField] protected float burstCooldown = 0.5f;
    [SerializeField] protected int burstCount = 3;

    [Header("Recoil Settings")]
    [SerializeField] protected bool applyRecoil = true;
    [SerializeField] protected float recoilVertical = 3.2f;
    [SerializeField] protected float recoilHorizontal = 1.5f;
    [SerializeField] protected float recoilApplySpeed = 18f;
    [SerializeField] protected float recoilReturnSpeed = 22f;
    public float aimRecoilMultiplier = 0.5f;

    [Header("Spread Settings")]
    [SerializeField] protected bool applySpread = true;
    [SerializeField] protected float minSpreadAngle = 0.8f;
    [SerializeField] protected float maxSpreadAngle = 4f;
    [SerializeField] protected float spreadIncreasePerShot = 0.35f;
    [SerializeField] protected float spreadApplySpeed = 0f;
    [SerializeField] protected float spreadReturnSpeed = 6f;
    [SerializeField] protected float spreadCrosshairSizePerAngle = 0f;

    [Header("Ammo Settings")]
    [SerializeField] protected int magazineSize = 30;
    [SerializeField] protected int currentAmmo = 30;
    [SerializeField] protected int totalAmmo = 180;
    [SerializeField] protected float reloadTime = 2f;

    [Header("Aim Settings")]
    [SerializeField] protected bool hasScope = false;
    [SerializeField] protected float aimFOV = 40f;
    [SerializeField] protected float normalFOV = 60f;
    [SerializeField] protected float aimSpeed = 10f;
    [SerializeField] protected Vector3 aimOffset = Vector3.zero;

    [Header("Crosshair Settings")]
    [SerializeField] protected float smoothSpreadSpeed = 0f;

    [Header("Bullet Settings")]
    [SerializeField] protected GameObject bulletPrefab;
    [SerializeField] protected float bulletSpeed = 80f;
    [SerializeField] protected float bulletLifeTime = 3f;

    [Header("Effects")]
    [SerializeField] protected ParticleSystem muzzleFlash;
    [SerializeField] protected AudioSource fireSound;

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

    private FireMode pendingFireMode;
    private bool hasPendingFireMode;
    private bool IsBurstActive()
    {
        return currentFireMode == FireMode.Burst &&
               shotsRemainingInBurst > 0;
    }
    private void ApplyPendingFireMode()
    {
        if (!hasPendingFireMode)
            return;

        currentFireMode = pendingFireMode;
        hasPendingFireMode = false;

        NotifyHUD();

        Debug.Log("Applied pending fire mode: " + currentFireMode);
    }
    public void CompleteBurst()
    {
        shotsRemainingInBurst = 0;
        nextBurstShotTime = 0;
        ApplyPendingFireMode();
    }
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

    public float GetFirePointDistance()
    {
        return firePointDistance;
    }
    public int GetCurrentAmmo()
    {
        return currentAmmo;
    }
    public int GetTotalAmmo()
    {
        return totalAmmo;
    }
    public FireMode GetCurrentFireMode()
    {
        return currentFireMode;
    }
    public void setDamage(float newDamage)
    {
        damage = newDamage;
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
        return !isBlocked && !isReloading && currentAmmo > 0;
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

        HandleBurstFire();
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
                    if (Fire()) nextTimeToFire = time + 1f / fireRate;
                }
                break;

            case FireMode.Burst:
                // Start a new burst if trigger pressed and burst ready
                if (shotsRemainingInBurst == 0 && triggerReleasedSinceLastShot && time >= nextBurstTime)
                {
                    triggerReleasedSinceLastShot = false;
                    if(time >= nextBurstTime)
                    {
                        shotsRemainingInBurst = Mathf.Min(burstCount, currentAmmo);
                        nextBurstShotTime = time;
                        nextBurstTime = time + burstCooldown;

                        HandleBurstFire();
                    }
                }
                break;

            case FireMode.Single:
                if (triggerReleasedSinceLastShot && time >= nextTimeToFire)
                {
                    if (Fire())
                    {
                        nextTimeToFire = time + 1f / fireRate;
                        triggerReleasedSinceLastShot = false;
                    }    
                }
                break;
        }
    }
    public void SwitchFireMode()
    {
        int modeCount = System.Enum.GetValues(typeof(FireMode)).Length;

        FireMode nextMode = currentFireMode;

        for (int i = 1; i <= modeCount; i++)
        {
            FireMode candidate =
                (FireMode)(((int)currentFireMode + i) % modeCount);

            if ((candidate == FireMode.Auto && allowAuto) ||
                (candidate == FireMode.Burst && allowBurst) ||
                (candidate == FireMode.Single && allowSingle))
            {
                nextMode = candidate;
                break;
            }
        }

        if (nextMode == currentFireMode)
            return;

        // If a burst is currently active, do not interrupt it.
        // Queue the requested fire mode instead.
        if (IsBurstActive())
        {
            pendingFireMode = nextMode;
            hasPendingFireMode = true;

            Debug.Log(
                $"Fire mode change queued: {currentFireMode} -> {pendingFireMode}"
            );

            return;
        }

        currentFireMode = nextMode;
        hasPendingFireMode = false;

        NotifyHUD();

        Debug.Log("Switched to: " + currentFireMode);
    }

    // --- FIRE LOGIC ---
    protected virtual bool Fire()
    {
        if (bulletPrefab == null || firePoint == null) return false;
        if (currentAmmo <= 0 || isReloading) return false;

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

        return true;
    }

    private void HandleBurstFire()
    {
        if (currentFireMode != FireMode.Burst)
            return;

        if (shotsRemainingInBurst <= 0)
            return;

        if (Time.time < nextBurstShotTime)
            return;

        if (Fire())
        {
            shotsRemainingInBurst--;

            if (currentAmmo <= 0 || shotsRemainingInBurst <= 0)
            {
                CompleteBurst();
                return;
            }

            nextBurstShotTime = Time.time + burstDelay;
        }
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
        {
            if (currentFireMode == FireMode.Burst)
            {
                CompleteBurst();
            }

            StartCoroutine(Reload());
        }
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
