using System.Collections;
using UnityEngine;

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
    [SerializeField] protected float burstCooldown = 0.5f;
    [SerializeField] protected int burstCount = 3;

    [Header("Recoil Settings")]
    [SerializeField] protected bool applyRecoil = true;
    [SerializeField] protected float recoilVertical = 3.2f;
    [SerializeField] protected float recoilHorizontal = 1.5f;
    [SerializeField] protected float recoilApplySpeed = 18f;
    [SerializeField] protected float recoilReturnSpeed = 22f;
    [SerializeField] protected float recoilRecoveryDelay = 0.15f;
    [SerializeField]
    protected AnimationCurve verticalRecoilPattern =
        new AnimationCurve(
            new Keyframe(0f, 1.00f),
            new Keyframe(1f, 1.05f),
            new Keyframe(2f, 1.10f),
            new Keyframe(3f, 1.15f),
            new Keyframe(4f, 1.18f),
            new Keyframe(5f, 1.20f),
            new Keyframe(6f, 1.18f),
            new Keyframe(7f, 1.15f)
        );
    [SerializeField, Range(0f, 1f)]
    protected float horizontalRecoilRandomness = 0.35f;
    [SerializeField] protected float recoilPatternResetDelay = 0.25f;
    [SerializeField] protected float horizontalRecoilDirectionChangeChanceIncrease;
    [SerializeField] protected float noRecoveryDownwardRecoveryAmount = 5.0f;
    [SerializeField] protected float aimRecoilMultiplier = 0.5f;

    [Header("Spread Settings")]
    [SerializeField] protected bool applySpread = true;
    [SerializeField] protected float minSpreadAngle = 0.8f;
    [SerializeField] protected float maxSpreadAngle = 4f;
    [SerializeField] protected float spreadIncreasePerShot = 0.35f;
    [SerializeField] protected float spreadReturnSpeed = 6f;
    [SerializeField] protected float spreadCrosshairSizePerAngle = 0f;
    [SerializeField] protected float aimSpreadMultiplier = 0.5f;

    [Header("Ammo Settings")]
    [SerializeField] protected int magazineSize = 30;
    [SerializeField] protected int currentAmmo = 30;
    [SerializeField] protected int totalAmmo = 180;
    [SerializeField] protected float reloadTime = 2f;

    [Header("Aim Settings")]
    [SerializeField] protected bool hasScope = false;
    [SerializeField] protected float aimNoScopeFOV = 40f;
    [SerializeField] protected float aimScopeFOV = 20f;
    [SerializeField] protected float normalFOV = 60f;
    [SerializeField] protected float aimSpeed = 10f;
    [SerializeField] protected Vector3 aimNoScopeOffset = Vector3.zero;
    [SerializeField] protected Vector3 aimScopeOffset = Vector3.zero;

    [Header("Crosshair Settings")]
    [SerializeField] protected float smoothSpreadSpeed = 0f;

    [Header("Bullet Settings")]
    [SerializeField] protected GameObject bulletPrefab;
    [SerializeField] protected float bulletSpeed = 80f;
    [SerializeField] protected float bulletLifeTime = 3f;

    [Header("Effects")]
    [SerializeField] protected ParticleSystem muzzleFlash;
    [SerializeField] protected AudioSource fireSound;

    [Header("Bolt / Charging Handle")]
    [Tooltip("The moving part (bolt, charging handle, or a shotgun's pump), at its rest pose. Empty: no bolt motion.")]
    [SerializeField] protected Transform boltPart;
    [Tooltip("An empty object where the bolt is fully back (position and rotation), best under the same parent as the bolt.")]
    [SerializeField] protected Transform boltBackPoint;
    [Tooltip("Optional (bolt-action): where the handle has turned up, before it is pulled back. The bolt then goes " +
             "rest -> here -> back point and returns the same way (turn up, pull, push, turn down).")]
    [SerializeField] protected Transform boltLiftPoint;
    [Tooltip("Auto: snaps back and home very fast after each shot. Manual (pump / bolt-action): worked by hand over " +
             "exactly the time between two shots.")]
    [SerializeField] protected BoltCycleMode boltCycle = BoltCycleMode.Auto;
    [Tooltip("Auto: how long one cycle takes (s); never more than 80% of the time between two shots.")]
    [SerializeField] protected float boltCycleTime = 0.06f;
    [Tooltip("Manual: share of the time between two shots before the action is worked (the shooter rides the recoil first).")]
    [SerializeField, Range(0f, 0.9f)] protected float manualActionDelay = 0.15f;
    [Tooltip("Optional shape of a cycle: time 0..1 through it -> 0 at rest .. 1 at the back point. Empty: the built-in " +
             "shape of the mode (Auto: snap back, slam home; Manual: pull, hold a moment, push).")]
    [SerializeField] protected AnimationCurve boltCurve = new AnimationCurve();
    [Tooltip("The last round fired: the bolt stays back. The reload's RackBolt step then releases it (it slams home) " +
             "instead of racking it; without such a step it closes once the reload is done.")]
    [SerializeField] protected bool boltLockOnEmpty = false;
    [Tooltip("Off: a charging handle that does not move with the bolt when firing (M4, FAMAS, M249...). Shots leave it " +
             "still (shells fly out at once, no lock on empty); only the reload's RackBolt / OpenBolt / CloseBolt steps move it.")]
    [SerializeField] protected bool boltMovesWhenFiring = true;

    [Header("Shell Ejection")]
    [Tooltip("The spent shell thrown out when the bolt is fully back (any model; purely visual). Empty: no shells.")]
    [SerializeField] protected GameObject shellPrefab;
    [Tooltip("Where shells come out (the ejection port), oriented as the shell should leave. Its axes give the speed's direction.")]
    [SerializeField] protected Transform shellEjectPoint;
    [Tooltip("The shell's speed (m/s) along the eject point's own axes (x right, y up, z forward).")]
    [SerializeField] protected Vector3 shellEjectVelocity = new Vector3(1.5f, 1f, 0f);
    [Tooltip("Random change of each shell's speed (share, 0..1).")]
    [SerializeField, Range(0f, 1f)] protected float shellEjectRandomness = 0.2f;
    [Tooltip("How fast shells tumble (degrees/s).")]
    [SerializeField] protected float shellSpin = 720f;
    [Tooltip("How long a shell stays before it is gone (s).")]
    [SerializeField] protected float shellLifetime = 3f;
    [Tooltip("The shell's size, times the shell prefab's own scale (each gun model is scaled differently).")]
    [SerializeField, Min(0.01f)] protected float shellScale = 1f;

    // Internal state
    protected float nextTimeToFire;
    protected float nextBurstTime;
    protected int shotsRemainingInBurst;
    protected bool triggerReleasedSinceLastShot = true;
    protected bool requireTriggerReleaseAfterReload = false;
    protected bool isReloading = false;
    protected bool isShooting = false;
    protected bool isAiming = false;
    private float reloadStartTime;
    protected float currentSpreadAngle;

    protected Vector2 currentRecoil;
    protected Vector2 appliedRecoil;
    protected Vector2 recoilVelocity;
    protected int recoilShotIndex;
    protected float horizontalRecoilDirectionChangeChance;
    protected float currentHorizontalRecoilDirection;
    protected float lastRecoilShotTime = -Mathf.Infinity;
    protected bool hasSequenceRecoveryTarget = false;
    protected float sequenceRecoveryTargetY = 0f;
    protected Vector3 defaultPosition;
    protected Vector3 aimNoScopePosition;
    protected Vector3 aimScopePosition;
    protected Quaternion defaultRotation;

    [Header("Model Recoil")]
    [SerializeField]
    protected Vector3 modelRecoilPositionKick = new Vector3(0f, 0.005f, -0.035f);
    [SerializeField]
    protected Vector3 modelRecoilRotationKick = new Vector3(-2.0f, 0.35f, 0.5f);
    [SerializeField] protected float modelRecoilPositionRecovery = 18f;
    [SerializeField] protected float modelRecoilRotationRecovery = 20f;
    [SerializeField] protected float modelRecoilAimMultiplier = 0.6f;

    protected Vector3 modelRecoilPosition;
    protected Vector3 modelRecoilPositionVelocity;

    protected Vector3 modelRecoilRotation;
    protected Vector3 modelRecoilRotationVelocity;

    protected Vector3 currentModelPosition;
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
        nextBurstTime = Time.time + burstCooldown;
        ApplyPendingFireMode();
    }
    public delegate void OnGunStatChange();
    public event OnGunStatChange onGunStatChange;

    protected void NotifyHUD()
    {
        onGunStatChange?.Invoke();
    }
    private void Awake()
    {
        defaultPosition = transform.localPosition;
        aimNoScopePosition = defaultPosition + aimNoScopeOffset;
        aimScopePosition = defaultPosition + aimScopeOffset;

        defaultRotation = transform.localRotation;
        currentModelPosition = defaultPosition;
    }
    private void Start()
    {
        currentSpreadAngle = minSpreadAngle;

        if (!IsFireModeAllowed(currentFireMode))
            currentFireMode = GetFirstAllowedMode();

        ResetHorizontalRecoilDirection();
    }
    public float GetNoRecoveryDownwardRecoveryAmount()
    {
        return noRecoveryDownwardRecoveryAmount;
    }    
    public float GetFirePointDistance()
    {
        return firePointDistance;
    }
    public int GetCurrentAmmo()
    {
        return currentAmmo;
    }

    /// <summary>
    /// The rounds loaded are thrown away (e.g. a short ammo belt flicked off during the reload): none left, so the
    /// reload then loads a whole magazine from the rounds carried.
    /// </summary>
    public void DiscardLoadedRounds()
    {
        currentAmmo = 0;
        NotifyHUD();
    }
    public int GetTotalAmmo()
    {
        return totalAmmo;
    }
    public int GetMagazineSize() => magazineSize;
    public Vector3 DefaultLocalPosition => defaultPosition;
    public Quaternion DefaultLocalRotation => defaultRotation;
    public float GetReloadTime() => reloadTime;
    public FireMode GetCurrentFireMode()
    {
        return currentFireMode;
    }
    private ChessPieceFPSController ownerPiece;
    private IOutgoingDamageBonus[] ownerBonuses;

    /// <summary>
    /// Damage multiplier for the shot fired now, from the owner's skills (e.g. the Knight's
    /// landing bonus). Call ONCE per shot: one-shot bonuses are consumed.
    /// </summary>
    protected float ConsumeShotDamageMultiplier()
    {
        if (ownerPiece == null)
        {
            ownerPiece = GetComponentInParent<ChessPieceFPSController>();
            ownerBonuses = ownerPiece != null ? ownerPiece.GetComponents<IOutgoingDamageBonus>() : null;
        }
        if (ownerBonuses == null)
            return 1f;

        float multiplier = 1f;
        for (int i = 0; i < ownerBonuses.Length; i++)
            if (ownerBonuses[i] != null)
                multiplier *= ownerBonuses[i].ConsumeShotDamageMultiplier();
        return multiplier;
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

    public Vector2 GetAppliedRecoil() => appliedRecoil;
    public bool IsShooting() => isShooting;
    public bool IsReloading() => isReloading;
    public void SetSequenceRecoveryTarget(float targetY)
    {
        hasSequenceRecoveryTarget = true;
        sequenceRecoveryTargetY = Mathf.Max(targetY, 0f);
    }
    public bool IsRecoilRecoveryActive()
    {
        return ShouldRecoverRecoil();
    }
    public bool IsRecoilSequenceContinuationActive()
    {
        if (!applyRecoil)
            return false;

        return
            Time.time - lastRecoilShotTime <=
            recoilPatternResetDelay;
    }
    public float GetCurrentVerticalRecoil()
    {
        return Mathf.Max(currentRecoil.y, 0f);
    }
    public bool IsSequenceRecoveryAtTarget(float targetY, float epsilon)
    {
        float actualTarget = hasSequenceRecoveryTarget
            ? Mathf.Min(sequenceRecoveryTargetY, Mathf.Max(currentRecoil.y, 0f))
            : 0f;

        return
            Mathf.Abs(currentRecoil.y - actualTarget) <= epsilon &&
            Mathf.Abs(appliedRecoil.y - actualTarget) <= epsilon;
    }
    public float ConsumeSequenceVerticalRecoil()
    {
        float consumed = appliedRecoil.y;

        currentRecoil.y = 0f;
        appliedRecoil.y = 0f;
        recoilVelocity.y = 0f;

        hasSequenceRecoveryTarget = false;
        sequenceRecoveryTargetY = 0f;

        return consumed;
    }
    public void ClearSequenceRecoveryTarget()
    {
        hasSequenceRecoveryTarget = false;
        sequenceRecoveryTargetY = 0f;
    }
    public bool GetTriggerReleasedSinceLastShot()
    {
        return triggerReleasedSinceLastShot;
    }
    public void SetTriggerReleasedSinceLastShot(bool pressed) => triggerReleasedSinceLastShot = pressed;
    public void SetRequireTriggerReleaseAfterReload(bool require) => requireTriggerReleaseAfterReload = require;
    public void ResetHorizontalRecoilDirection()
    {
        horizontalRecoilDirectionChangeChance = 0.5f;
        currentHorizontalRecoilDirection = Random.value < 0.5 ? 1f : -1f;
    }
    public void SetAiming(bool aim)
    {
        isAiming = aim;
    }

    /// <summary>The piece died / lost control while aiming: aiming ends and the camera is back to its normal view at once.</summary>
    public void StopAiming()
    {
        isAiming = false;
        if (Camera.main != null)
            Camera.main.fieldOfView = normalFOV;
    }
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
    public override void SetBlocked(bool blocked)
    {
        isBlocked = blocked;
        if (isBlocked) CompleteBurst();
    }
    public override void Tick(float deltaTime)
    {

        if (attackPoint == null)
            return;

        if (currentAmmo <= 0 && !isReloading)
        {
            StartReload();
        }

        UpdateRecoil(deltaTime);
        UpdateSpread(deltaTime);

        UpdateCrosshairPoints();
        UpdateReloadMotion();
        UpdateModelPosition();
        UpdateBolt(Time.time);

        isShooting = false;

        UpdateAiming();

        if (isBlocked)
            return;

        HandleBurstFire();

    }
    void UpdateAiming()
    {
        if (Camera.main == null) return;

        isAiming = isAiming && !isReloading;

        float aimFOV = hasScope ? aimScopeFOV : aimNoScopeFOV;

        float targetFOV = isAiming ? aimFOV : normalFOV;
        Camera.main.fieldOfView = Mathf.Lerp(
            Camera.main.fieldOfView,
            targetFOV,
            Time.deltaTime * aimSpeed
        );

        Vector3 aimPosition = hasScope ? aimScopePosition : aimNoScopePosition;

        currentModelPosition = Vector3.Lerp(
            currentModelPosition,
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
                if (requireTriggerReleaseAfterReload)
                    break;
                // Several shots in one frame when the frame is longer than the interval (low FPS).
                for (int shot = 0; shot < MaxShotsPerFrame && time >= nextTimeToFire; shot++)
                {
                    if (!Fire())
                        break;
                    ScheduleNextShot(time);
                    triggerReleasedSinceLastShot = false;
                }
                break;

            case FireMode.Burst:
                // Start a new burst if trigger pressed and burst ready
                if (shotsRemainingInBurst == 0 && triggerReleasedSinceLastShot && time >= nextBurstTime)
                {
                    triggerReleasedSinceLastShot = false;
                    if (time >= nextBurstTime)
                    {
                        shotsRemainingInBurst = Mathf.Min(burstCount, currentAmmo);

                        HandleBurstFire();
                    }
                }
                break;

            case FireMode.Single:
                if (triggerReleasedSinceLastShot && time >= nextTimeToFire)
                {
                    if (Fire())
                    {
                        ScheduleNextShot(time);
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
    private const int MaxShotsPerFrame = 4;

    /// <summary>
    /// When the next shot may go: one interval after this shot's scheduled time while firing on (the rest of the
    /// frame past it is kept, so the rated fire rate holds at any FPS), or one interval after now when the gun was
    /// not firing (no shots are ever banked while the trigger rests).
    /// </summary>
    protected void ScheduleNextShot(float time)
    {
        float interval = 1f / Mathf.Max(0.01f, fireRate);
        nextTimeToFire = time - nextTimeToFire <= Time.deltaTime ? nextTimeToFire + interval : time + interval;
    }
    private void ResetRecoilPatternIfNeeded()
    {
        if (Time.time - lastRecoilShotTime > recoilPatternResetDelay)
        {
            recoilShotIndex = 0;
        }
    }

    private float EvaluateVerticalRecoilMultiplier()
    {
        if (verticalRecoilPattern == null || verticalRecoilPattern.length == 0)
            return 1f;

        return Mathf.Max(
            0f,
            verticalRecoilPattern.Evaluate(recoilShotIndex)
        );
    }
    private float GetHorizontalRecoilImpulse()
    {
        bool shouldChangeDirection =
        Random.value < horizontalRecoilDirectionChangeChance;

        if (shouldChangeDirection)
        {
            horizontalRecoilDirectionChangeChance = 0.5f;
        }
        else
        {
            horizontalRecoilDirectionChangeChance =
                Mathf.Min(
                    horizontalRecoilDirectionChangeChance + horizontalRecoilDirectionChangeChanceIncrease,
                    1f
                );
        }

        float direction = shouldChangeDirection ? (currentHorizontalRecoilDirection * - 1f) : currentHorizontalRecoilDirection;

        currentHorizontalRecoilDirection = direction;

        float randomStrength =
            Random.Range(
                1f - horizontalRecoilRandomness,
                1f + horizontalRecoilRandomness
            );

        return recoilHorizontal * currentHorizontalRecoilDirection * randomStrength;
    }
    /// <summary>
    /// One shot's recoil (camera kick from the pattern, horizontal kick, weapon model kick, recovery timing):
    /// the same for every gun, applied once per shot (a shotgun's pellets are one shot).
    /// </summary>
    protected void ApplyShotRecoil()
    {
        if (!applyRecoil)
            return;

        ResetRecoilPatternIfNeeded();

        float verticalMultiplier =
            EvaluateVerticalRecoilMultiplier();

        float vertical =
            recoilVertical * verticalMultiplier;

        if (isAiming)
        {
            vertical *= aimRecoilMultiplier;
        }

        float horizontal =
            GetHorizontalRecoilImpulse();

        currentRecoil += new Vector2(
            horizontal,
            vertical
        );

        ApplyModelRecoil();

        recoilShotIndex++;
        lastRecoilShotTime = Time.time;
    }

    protected virtual bool Fire()
    {
        if (bulletPrefab == null || attackPoint == null) return false;
        if (currentAmmo <= 0 || isReloading) return false;

        currentAmmo--;
        isShooting = true;

        NotifyHUD();

        // Recoil
        ApplyShotRecoil();

        // Spread
        Quaternion bulletRotation = attackPoint.rotation;
        if (applySpread)
        {
            currentSpreadAngle = Mathf.Clamp(currentSpreadAngle + spreadIncreasePerShot, minSpreadAngle, maxSpreadAngle);

            if (isAiming)
            {
                currentSpreadAngle *= aimSpreadMultiplier;
            }

            float spreadRad = currentSpreadAngle * Mathf.Deg2Rad;
            Vector2 rand = Random.insideUnitCircle * Mathf.Tan(spreadRad);

            Vector3 spreadDir = attackPoint.forward + attackPoint.up * rand.y + attackPoint.right * rand.x;
            bulletRotation = Quaternion.LookRotation(spreadDir.normalized);
        }

        // Spawn bullet
        float shotMultiplier = ConsumeShotDamageMultiplier();
        var bullet = BulletPoolManager.Instance.GetBullet(attackPoint.position, bulletRotation);
        if (bullet.TryGetComponent<Bullet>(out var bulletScript))
        {
            bulletScript.Speed = bulletSpeed;
            bulletScript.LifeTime = bulletLifeTime;
            bulletScript.BaseDamage = damage * shotMultiplier;
            bulletScript.MaxRange = range;
            bulletScript.Source = gameObject;

            if (applySpread)
            {
                Vector3 shotDirection = bulletRotation * Vector3.forward;
                bulletScript.Fire(shotDirection);
            }
            else bulletScript.Fire(attackPoint.forward);
        }

        // Effects
        if (muzzleFlash != null) muzzleFlash.Play();
        if (fireSound != null) fireSound.Play();
        CycleBolt();

        return true;
    }

    private void HandleBurstFire()
    {
        if (currentFireMode != FireMode.Burst)
            return;

        // Several shots in one frame when the frame is longer than the interval (low FPS).
        for (int shot = 0; shot < MaxShotsPerFrame && shotsRemainingInBurst > 0 && Time.time >= nextTimeToFire; shot++)
        {
            if (!Fire())
                return;

            shotsRemainingInBurst--;

            if (currentAmmo <= 0 || shotsRemainingInBurst <= 0)
            {
                CompleteBurst();
                return;
            }

            ScheduleNextShot(Time.time);
        }
    }
    private bool ShouldRecoverRecoil()
    {
        if (!applyRecoil)
            return false;

        return Time.time - lastRecoilShotTime >= recoilRecoveryDelay;
    }
    private void ApplyModelRecoil()
    {
        Vector3 positionKick = modelRecoilPositionKick;
        Vector3 rotationKick = modelRecoilRotationKick;

        if (isAiming)
        {
            float multiplier = Mathf.Max(modelRecoilAimMultiplier, 0f);

            positionKick *= multiplier;
            rotationKick *= multiplier;
        }

        modelRecoilPosition += positionKick;
        modelRecoilRotation += rotationKick;
    }
    /// <summary>Only the aiming view (camera zoom, gun model position): for a piece whose control has ended.</summary>
    public void UpdateAimView()
    {
        if (attackPoint == null)
            return;
        UpdateAiming();
        UpdateReloadMotion();
        UpdateModelPosition();
        UpdateBolt(Time.time);
    }

    // --- RELOAD MOTION (GunReloadMotion on the same object; visual only) ---
    private GunReloadMotion reloadMotion;
    private bool reloadMotionLooked;
    protected Vector3 reloadPoseOffsetPosition;
    protected Quaternion reloadPoseOffsetRotation = Quaternion.identity;

    /// <summary>The gun has its own reload motion: it is not lowered into the blocked pose while reloading.</summary>
    public bool HasReloadMotion
    {
        get
        {
            FindReloadMotion();
            return reloadMotion != null && reloadMotion.isActiveAndEnabled && reloadMotion.HasSteps;
        }
    }

    /// <summary>The gun's own tilt while reloading, on top of its aim / recoil pose (in its parent's space).</summary>
    public void SetReloadPoseOffset(Vector3 position, Quaternion rotation)
    {
        reloadPoseOffsetPosition = position;
        reloadPoseOffsetRotation = rotation;
    }

    private void FindReloadMotion()
    {
        if (reloadMotionLooked)
            return;
        reloadMotionLooked = true;
        reloadMotion = GetComponent<GunReloadMotion>();
    }

    private void UpdateReloadMotion()
    {
        FindReloadMotion();
        if (reloadMotion != null && reloadMotion.isActiveAndEnabled)
            reloadMotion.Tick();
    }

    // --- BOLT / SHELLS (visual only: never changes fire rate, damage or ammo) ---
    private bool boltRestKnown;
    private Vector3 boltRestPosition;
    private Quaternion boltRestRotation;
    private bool boltCycling;
    private float boltCycleStart;
    private float boltCycleDuration;
    private float boltCycleFrom;
    private BoltCycleMode boltCycleShape;
    private float boltPeak;
    private bool boltLockAtPeak;
    private bool boltLocked;
    private float boltApplied;
    private bool shellPending;
    private const float BoltReleaseSeconds = 0.1f; // a locked-back bolt let go: it slams home this fast
    private bool boltPreviewing; // the editor's reload preview: no reload is running, the bolt waits for its step

    private bool HasBolt => boltPart != null && boltBackPoint != null;

    /// <summary>The bolt is held back (locked open on an empty gun until the reload is done).</summary>
    public bool IsBoltLockedBack => boltLocked;

    /// <summary>One shot: the bolt cycles (Auto: very fast; Manual: over exactly the time to the next shot).</summary>
    protected void CycleBolt()
    {
        if (shellPending)
            EjectShell(); // the shot before's shell, its bolt not back yet (several shots in one frame)

        if (!HasBolt || !boltMovesWhenFiring)
        {
            EjectShell(); // no bolt to wait for: out at once
            return;
        }

        float interval = 1f / Mathf.Max(0.01f, fireRate);
        float now = Time.time;
        if (boltCycle == BoltCycleMode.Manual)
        {
            float delay = interval * Mathf.Clamp(manualActionDelay, 0f, 0.9f);
            StartBoltCycle(now + delay, interval - delay, 0f, BoltCycleMode.Manual);
        }
        else
        {
            StartBoltCycle(now, Mathf.Min(Mathf.Max(0f, boltCycleTime), interval * BoltMotion.AutoMaxShareOfInterval), 0f, BoltCycleMode.Auto);
        }
        boltLocked = false;
        boltLockAtPeak = boltLockOnEmpty && currentAmmo <= 0;
        shellPending = shellPrefab != null && shellEjectPoint != null;
    }

    private void StartBoltCycle(float start, float duration, float from, BoltCycleMode shape)
    {
        boltCycling = true;
        boltCycleStart = start;
        boltCycleDuration = duration;
        boltCycleFrom = from;
        boltCycleShape = shape;
        boltPeak = BoltMotion.PeakProgress(boltCurve, shape);
    }

    private void UpdateBolt(float now)
    {
        if (!HasBolt)
            return;
        if (!boltRestKnown)
        {
            boltRestPosition = boltPart.localPosition;
            boltRestRotation = boltPart.localRotation;
            boltRestKnown = true;
            boltApplied = 0f;
        }

        CloseBoltIfLoaded(now);

        float back = 0f;
        if (boltLocked)
        {
            back = 1f;
        }
        else if (boltCycling)
        {
            float elapsed = boltCycleDuration > 0f ? (now - boltCycleStart) / boltCycleDuration : 1f;
            float progress = Mathf.LerpUnclamped(boltCycleFrom, 1f, Mathf.Max(0f, elapsed));
            if (shellPending && progress >= boltPeak)
                EjectShell(); // fully back: the shell flies out
            if (boltLockAtPeak && progress >= boltPeak)
            {
                boltCycling = false;
                boltLocked = true;
                back = 1f;
            }
            else if (progress >= 1f)
            {
                boltCycling = false;
            }
            else
            {
                back = BoltMotion.Shape(boltCurve, boltCycleShape, progress);
            }
        }

        if (back == 0f && boltApplied == 0f)
            return; // at rest, already shown there
        ApplyBoltPose(back);
    }

    /// <summary>
    /// Still locked back once the reload is done (no RackBolt step let it go): released now, so it never stays open
    /// on a loaded gun.
    /// </summary>
    private void CloseBoltIfLoaded(float now)
    {
        if (boltLocked && !isReloading && currentAmmo > 0 && !boltPreviewing)
            ReleaseBolt(now);
    }

    /// <summary>A locked-back bolt let go (the bolt release pressed): it slams home from its back point.</summary>
    private void ReleaseBolt(float start)
    {
        boltLocked = false;
        boltLockAtPeak = false;
        StartBoltCycle(start, BoltReleaseSeconds, BoltMotion.PeakProgress(boltCurve, BoltCycleMode.Auto), BoltCycleMode.Auto);
    }

    /// <summary>
    /// A reload's RackBolt step, from <paramref name="start"/> (clock time) over <paramref name="duration"/>: a bolt
    /// locked back is released at once (it slams home); a closed one is racked (pulled back, held, let go) over it all.
    /// </summary>
    public void RackBolt(float start, float duration)
    {
        if (!HasBolt)
            return;
        if (shellPending)
            EjectShell();
        if (boltLocked)
        {
            ReleaseBolt(start);
            return;
        }
        boltLockAtPeak = false;
        StartBoltCycle(start, Mathf.Max(0f, duration), 0f, BoltCycleMode.Manual);
    }

    /// <summary>
    /// A reload's OpenBolt step: the bolt is pulled back from <paramref name="start"/> over <paramref name="duration"/>
    /// and stays back (rounds loaded through the open action) until a CloseBolt step, or the reload's end.
    /// </summary>
    public void OpenBolt(float start, float duration)
    {
        if (!HasBolt)
            return;
        if (shellPending)
            EjectShell();
        if (boltLocked)
            return; // already open
        float peak = Mathf.Max(0.01f, BoltMotion.PeakProgress(boltCurve, BoltCycleMode.Manual));
        StartBoltCycle(start, Mathf.Max(0f, duration) / peak, 0f, BoltCycleMode.Manual);
        boltLockAtPeak = true;
    }

    /// <summary>A reload's CloseBolt step: a bolt held open is pushed home from <paramref name="start"/> over <paramref name="duration"/>.</summary>
    public void CloseBolt(float start, float duration)
    {
        if (!HasBolt)
            return;
        if (boltCycling && boltLockAtPeak)
        {
            boltCycling = false; // still opening: taken as open
            boltLocked = true;
        }
        if (!boltLocked)
            return;
        boltLocked = false;
        boltLockAtPeak = false;
        float from = boltCurve != null && boltCurve.length > 0
            ? BoltMotion.PeakProgress(boltCurve, BoltCycleMode.Manual)
            : BoltMotion.ManualPushStart;
        StartBoltCycle(start, Mathf.Max(0f, duration), from, BoltCycleMode.Manual);
    }

#if UNITY_EDITOR
    /// <summary>
    /// Editor preview of a reload (no Play): the bolt starts from where it is now (locked back for an empty
    /// magazine when it locks on empty)...
    /// </summary>
    public void EditorBoltBegin(bool empty)
    {
        if (!HasBolt)
            return;
        boltRestPosition = boltPart.localPosition;
        boltRestRotation = boltPart.localRotation;
        boltRestKnown = true;
        boltApplied = 0f;
        boltCycling = false;
        boltLocked = empty && boltLockOnEmpty && boltMovesWhenFiring;
        boltLockAtPeak = false;
        shellPending = false;
        boltPreviewing = true;
    }

    /// <summary>...moves on the preview's clock...</summary>
    public void EditorBoltTick(float now)
    {
        if (HasBolt && boltRestKnown)
            UpdateBolt(now);
    }

    /// <summary>...and is put back exactly where it was.</summary>
    public void EditorBoltEnd()
    {
        if (!HasBolt || !boltRestKnown)
            return;
        boltPart.SetLocalPositionAndRotation(boltRestPosition, boltRestRotation);
        boltRestKnown = false;
        boltCycling = false;
        boltLocked = false;
        boltPreviewing = false;
    }
#endif

    private void ApplyBoltPose(float back)
    {
        Transform parent = boltPart.parent;
        Vector3 backPos = parent != null ? parent.InverseTransformPoint(boltBackPoint.position) : boltBackPoint.position;
        Quaternion backRot = parent != null ? Quaternion.Inverse(parent.rotation) * boltBackPoint.rotation : boltBackPoint.rotation;
        bool hasLift = boltLiftPoint != null;
        Vector3 liftPos = Vector3.zero;
        Quaternion liftRot = Quaternion.identity;
        if (hasLift)
        {
            liftPos = parent != null ? parent.InverseTransformPoint(boltLiftPoint.position) : boltLiftPoint.position;
            liftRot = parent != null ? Quaternion.Inverse(parent.rotation) * boltLiftPoint.rotation : boltLiftPoint.rotation;
        }

        BoltMotion.Pose(back, boltRestPosition, boltRestRotation, hasLift, liftPos, liftRot, backPos, backRot,
            out Vector3 pos, out Quaternion rot);
        boltPart.localPosition = pos;
        boltPart.localRotation = rot;
        boltApplied = back;
    }

    private void EjectShell()
    {
        shellPending = false;
        if (shellPrefab == null || shellEjectPoint == null)
            return;
        float randomness = Mathf.Clamp01(shellEjectRandomness);
        Vector3 local = shellEjectVelocity * Random.Range(1f - randomness, 1f + randomness)
                        + Random.insideUnitSphere * (shellEjectVelocity.magnitude * randomness * 0.5f);
        Vector3? scale = Mathf.Approximately(shellScale, 1f) ? (Vector3?)null : shellPrefab.transform.localScale * shellScale;
        EjectedShell.Throw(shellPrefab, shellEjectPoint.position, shellEjectPoint.rotation,
            shellEjectPoint.TransformDirection(local), shellSpin, shellLifetime, scale);
    }

    /// <summary>Right-click the component: plays one bolt cycle (in the editor too, without Play) to check the back point.</summary>
    [ContextMenu("Test Bolt Cycle")]
    private void TestBoltCycle()
    {
        if (!HasBolt)
        {
            Debug.LogWarning($"{name}: set Bolt Part and Bolt Back Point first.");
            return;
        }
        if (Application.isPlaying)
        {
            CycleBolt();
            return;
        }
#if UNITY_EDITOR
        EditorPreviewBoltCycle();
#endif
    }

#if UNITY_EDITOR
    private UnityEditor.EditorApplication.CallbackFunction boltPreview;

    private void EditorPreviewBoltCycle()
    {
        if (boltPreview != null)
            UnityEditor.EditorApplication.update -= boltPreview;

        boltRestPosition = boltPart.localPosition;
        boltRestRotation = boltPart.localRotation;
        boltRestKnown = true;
        boltApplied = 0f;
        boltLocked = false;
        boltLockAtPeak = false;
        GameObject shell = shellPrefab;
        shellPrefab = null; // no shells thrown into the edited scene
        if (boltMovesWhenFiring)
            CycleBolt();
        else
            StartBoltCycle(Time.time, 0.4f, 0f, BoltCycleMode.Manual); // a still handle: shown racked by hand
        shellPrefab = shell;
        shellPending = false;
        float offset = Time.time - (float)UnityEditor.EditorApplication.timeSinceStartup; // edit-time clock as Time.time
        float end = boltCycleStart + boltCycleDuration;

        boltPreview = () =>
        {
            if (this == null || boltPart == null || boltBackPoint == null)
            {
                UnityEditor.EditorApplication.update -= boltPreview;
                boltPreview = null;
                return;
            }
            float now = (float)UnityEditor.EditorApplication.timeSinceStartup + offset;
            UpdateBolt(now);
            UnityEditor.SceneView.RepaintAll();
            if (now >= end)
            {
                boltPart.localPosition = boltRestPosition; // exactly back where it was
                boltPart.localRotation = boltRestRotation;
                boltRestKnown = false;
                boltCycling = false;
                UnityEditor.EditorApplication.update -= boltPreview;
                boltPreview = null;
            }
        };
        UnityEditor.EditorApplication.update += boltPreview;
    }
#endif

    private void UpdateModelPosition()
    {
        transform.localPosition =
        currentModelPosition + modelRecoilPosition + reloadPoseOffsetPosition;

        transform.localRotation =
            defaultRotation *
            reloadPoseOffsetRotation *
            Quaternion.Euler(modelRecoilRotation);
    }    
    private void UpdateRecoil(float deltaTime)
    {
        if (!applyRecoil)
            return;

        appliedRecoil = Vector2.SmoothDamp(
            appliedRecoil,
            currentRecoil,
            ref recoilVelocity,
            1f / Mathf.Max(recoilApplySpeed, 0.01f),
            Mathf.Infinity,
            deltaTime
        );

        if (ShouldRecoverRecoil())
        {
            float recoilRecovery =
                1f - Mathf.Exp(
                    -Mathf.Max(recoilReturnSpeed, 0f) * deltaTime
                );

            Vector2 recoveryTarget = Vector2.zero;

            if (hasSequenceRecoveryTarget)
            {
                recoveryTarget.y =
                    Mathf.Min(
                        Mathf.Max(sequenceRecoveryTargetY, 0f),
                        Mathf.Max(currentRecoil.y, 0f)
                    );
            }

            currentRecoil = Vector2.Lerp(
                currentRecoil,
                recoveryTarget,
                recoilRecovery
            );
        }
        modelRecoilPosition = Vector3.SmoothDamp(
            modelRecoilPosition,
            Vector3.zero,
            ref modelRecoilPositionVelocity,
            1f / Mathf.Max(modelRecoilPositionRecovery, 0.01f),
            Mathf.Infinity,
            deltaTime
        );

        modelRecoilRotation = Vector3.SmoothDamp(
            modelRecoilRotation,
            Vector3.zero,
            ref modelRecoilRotationVelocity,
            1f / Mathf.Max(modelRecoilRotationRecovery, 0.01f),
            Mathf.Infinity,
            deltaTime
        );
    }

    private void UpdateSpread(float deltaTime)
    {
        if (!applySpread) return;
        if (!isShooting)
            currentSpreadAngle = Mathf.MoveTowards(currentSpreadAngle, minSpreadAngle, spreadReturnSpeed * deltaTime);
    }

    // --- RELOAD ---
    public float GetReloadProgress()
    {
        if (!isReloading)
            return 0f;

        if (reloadTime <= 0f)
            return 1f;

        return Mathf.Clamp01(
            (Time.time - reloadStartTime) / reloadTime
        );
    }
    public float GetReloadRemainingTime()
    {
        if (!isReloading)
            return 0f;

        return Mathf.Max(
            0f,
            reloadTime - (Time.time - reloadStartTime)
        );
    }
    public void StartReload()
    {
        if (!isReloading && currentAmmo < magazineSize && totalAmmo > 0)
        {
            if (currentFireMode == FireMode.Burst)
            {
                CompleteBurst();
            }
            requireTriggerReleaseAfterReload = true;
            StartCoroutine(Reload());
        }
    }

    private IEnumerator Reload()
    {
        if (isReloading || currentAmmo == magazineSize || totalAmmo <= 0) yield break;

        isReloading = true;
        reloadStartTime = Time.time;
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
    public override void BindAttackPoint(Transform point)
    {
        attackPoint = point;
    }
}
