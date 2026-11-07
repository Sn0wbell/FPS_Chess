using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public enum MovementOverrideSource
{
    None = 0,
    Skill = 1,
    GravityZone = 2
}

[RequireComponent(typeof(CharacterController))]
public class ChessPieceFPSController : MonoBehaviour, ITeamOwned
{
    // =========================
    // TEAM / OWNERSHIP
    // =========================
    [Header("Team")]
    [Tooltip("Side of this piece. Set by the arena on spawn; skills never damage allies.")]
    [SerializeField] private Team team = Team.White;

    public Team Team => team;
    public void SetTeam(Team value) => team = value;

    /// <summary>Only the player's piece reads keyboard/mouse. AI pieces are driven by code.</summary>
    public bool IsPlayerControlled { get; private set; }
    public void SetPlayerControlled(bool value) => IsPlayerControlled = value;

    private bool isDeadRaw;
    private bool bodyBroken; // died: the model broke into fragments, the body is hidden and no longer moves
    private PawnCloneHealth cloneHealth;

    /// <summary>Dead piece, or a pawn clone that is dissolving.</summary>
    public bool IsDead => isDeadRaw || (cloneHealth != null && cloneHealth.IsDying);

    /// <summary>Pawn decoys move themselves (PawnCloneHealth); this controller must not add physics.</summary>
    public bool IsPawnClone => cloneHealth != null;

    // Keyed action locks (e.g. the Queen while firing): no shooting / skill input while any is held.
    private readonly HashSet<object> actionLocks = new HashSet<object>();

    public void SetActionLocked(object owner, bool locked)
    {
        if (owner == null) return;
        if (locked) actionLocks.Add(owner);
        else actionLocks.Remove(owner);
    }

    public bool IsActionLocked => actionLocks.Count > 0;

    private ChessPieceHealth health;

    // =========================
    // CORE
    // =========================
    private CharacterController controller;
    private bool canMove;
    private bool canAct;

    // =========================
    // CAMERA
    // =========================
    [Header("Camera")]
    public Transform cameraPoint;

    // =========================
    // MOVEMENT
    // =========================
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float jumpHeight = 3.5f;
    [SerializeField] private float gravity = -9.81f;

    private float verticalVelocity;
    private bool isGrounded;

    // Gravity is off while ANY owner (a skill) disables it: overlapping skills never fight over one bool.
    private readonly HashSet<object> gravityDisablers = new HashSet<object>();
    private bool useGravity => gravityDisablers.Count == 0;

    // Forced displacement (Queen push, King pull, Bishop pull): one owner at a time.
    private object forcedMoveOwner;
    private bool isJumping;
    private Vector3 jumpMoveDirection;

    // =========================
    // SCATTER / MULTI-FRAME IMPULSE
    // =========================
    private Vector3 scatterVelocityOwner;
    /// <summary>Gravity of a knock-back / scatter flight (also used to predict where a throw lands).</summary>
    public const float ScatterGravity = -25f;
    private float scatterGravity = ScatterGravity;
    private bool scatting = false;
    private bool scatterAirborne;
    private bool fireHeldThroughBlock;

    [Header("Knock-back")]
    [Tooltip("Ground friction for horizontal knock-backs (higher = shorter slide).")]
    [SerializeField] private float knockbackGroundFriction = 8f;
    [Tooltip("A sliding knock-back ends below this speed (m/s).")]
    [SerializeField] private float knockbackStopSpeed = 0.5f;

    // =========================
    // MOUSE LOOK
    // =========================
    [Header("Mouse Look")]
    [SerializeField] private float mouseSensitivity = 15f;
    [SerializeField] private float minPitch = -80f;
    [SerializeField] private float maxPitch = 60f;

    private float yaw;
    private float pitch;
    // =========================
    // RECOIL SEQUENCE
    // =========================
    [Header("Recoid Sequence")]
    [SerializeField] private float recoilSequenceInputEpsilon = 0.05f;

    private bool recoilSequenceActive = false;
    private bool recoilSequenceHasUpwardIntent = false;
    private bool recoilSequenceSuppressRecovery = false;
    private bool recoilSequenceVirtualRecoveryActive = false;
    private float recoilSequenceVirtualRecoveryTargetY = 0f;
    private float recoilSequenceBaselinePitch;
    private float recoilSequenceUpwardPitch;
    private float recoilSequenceFrameStartPitch;
    private float recoilSequenceFramePitchDelta;
    private float recoilSequenceFrameMouseX;
    private float recoilSequenceFrameMouseY;
    private float recoilSequenceRecoveryTargetY;

    // =========================
    // WEAPON
    // =========================
    [Header("Weapon")]
    [SerializeField] private Transform attackPoint;
    [SerializeField] private Transform weaponHolder;
    [SerializeField] private Transform weaponBlockedPosition;
    [SerializeField] private Vector3 weaponHolderOffset = Vector3.zero;
    [SerializeField] private LayerMask weaponBlockLayer;
    [SerializeField] private float weaponAdjustSpeed = 10f;

    private WeaponController currentWeapon;
    private GunController currentGun;
    private GameObject weaponObject;
    private BoxCollider weaponBoxCollider;
    private float weaponBlockDelayTime = 0f;
    private float weaponBlockCheckDistance;
    private readonly Collider[] weaponBlockOverlapBuffer = new Collider[32];
    private Vector3 weaponBoxLocalCenter;
    private Quaternion weaponBoxLocalRotation;
    private Vector3 weaponBoxHalfExtents;

    private Vector3 defaultWeaponLocalPos;
    private Quaternion defaultWeaponLocalRot;
    private Vector3 currentWeaponLocalPos;
    private Quaternion currentWeaponLocalRot;

    // =========================
    // MOVEMENT OVERRIDE
    // =========================
    private float moveOverrideMul = 1f;
    private float jumpOverrideMul = 1f;
    private bool hasMoveOverride;

    private readonly Dictionary<object, Vector2> overrides
        = new Dictionary<object, Vector2>();

    // =========================
    // SKILL
    // =========================
    [HideInInspector]
    public List<ActiveSkill> boundSkills = new List<ActiveSkill>();

    private readonly HashSet<object> movementSuppressors = new();
    private bool IsMovementSuppressed => movementSuppressors.Count > 0;

    private readonly List<IAbortableMovementSkill> abortableMovementSkills = new();

    // =========================
    // UNITY
    // =========================
    void Awake()
    {
        controller = GetComponent<CharacterController>();
        DisableControl();

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // The piece owns its skills: one initialization, one tick per frame (see TickSkills).
        boundSkills.Clear();
        foreach (var skill in GetComponentsInChildren<ActiveSkill>(true))
        {
            if (skill == null) continue;
            boundSkills.Add(skill);
            if (skill is IAbortableMovementSkill abortable)
                abortableMovementSkills.Add(abortable);
        }
        RefreshSkillContext();

        health = GetComponent<ChessPieceHealth>();
        if (health != null)
            health.OnDeath += HandleDeath;

        cloneHealth = GetComponent<PawnCloneHealth>();
    }

    void OnDestroy()
    {
        if (health != null)
            health.OnDeath -= HandleDeath;
    }

    /// <summary>(Re)initializes every skill with the full context (call again when the weapon changes).</summary>
    public void RefreshSkillContext()
    {
        SkillContext ctx = new SkillContext
        {
            owner = gameObject,
            transform = transform,
            fps = this,
            weapon = currentWeapon,
            controller = controller,
            deltaTime = Time.deltaTime
        };

        foreach (var skill in boundSkills)
            if (skill != null)
                skill.Initialize(ctx);
    }

    /// <summary>Union of the block masks of this piece's active skills.</summary>
    public SkillBlockMask ActiveBlockMask
    {
        get
        {
            SkillBlockMask mask = SkillBlockMask.None;
            foreach (var s in boundSkills)
                if (s != null && s.State == SkillState.Active)
                    mask |= s.blockMask;
            return mask;
        }
    }

    private bool IsBlockedBySkill(SkillBlockMask m) => (ActiveBlockMask & m) != 0;

    void TickSkills()
    {
        SkillContext ctx = new SkillContext
        {
            owner = gameObject,
            transform = transform,
            fps = this,
            weapon = currentWeapon,
            controller = controller,
            deltaTime = Time.deltaTime
        };

        foreach (var skill in boundSkills)
            if (skill != null)
                skill.Tick(ctx);
    }

    void HandleDeath()
    {
        if (isDeadRaw) return;
        isDeadRaw = true;

        foreach (var skill in boundSkills)
            if (skill != null)
                skill.ForceCancel(); // releases every effect it put on other pieces

        // The body breaks into fragments where it is and stops moving (same death for every piece).
        Vector3 bodyCenter = controller != null ? controller.bounds.center : transform.position + Vector3.up;
        PieceDeathEffect.Shatter(gameObject);
        bodyBroken = true;
        if (controller != null)
            controller.enabled = false;

        // The player watches it from a step back (death cam).
        if (IsPlayerControlled)
        {
            ArenaController arena = ArenaController.Current;
            Vector3 look = cameraPoint != null ? cameraPoint.forward : transform.forward;
            DeathCamera.Begin(Camera.main, bodyCenter, look,
                arena != null ? arena.DeathCamBack : 4f,
                arena != null ? arena.DeathCamUp : 2f,
                arena != null ? arena.DeathCamMoveTime : 0.6f);
        }
    }

    /// <summary>Health came back after a death (ResetHealth / RestoreBaseMaxHealth): the body is whole again.</summary>
    void HandleRevive()
    {
        isDeadRaw = false;
        if (!bodyBroken)
            return;

        bodyBroken = false;
        PieceDeathEffect.Restore(gameObject);
        if (controller != null)
            controller.enabled = true;

        Camera cam = Camera.main;
        if (IsPlayerControlled && cam != null && cameraPoint != null && cam.GetComponent<DeathCamera>() != null)
        {
            DeathCamera.End(cam);
            cam.transform.SetParent(cameraPoint);
            cam.transform.localPosition = Vector3.zero;
            cam.transform.localRotation = Quaternion.identity;
        }
    }

    void Update()
    {
        if (isDeadRaw && health != null && health.IsAlive)
            HandleRevive(); // health was restored (ResetHealth / RestoreBaseMaxHealth)

        // Skills run even while the piece cannot be controlled (e.g. AI pieces), exactly once per frame.
        TickSkills();

        // Physics (gravity, knock-back flight) applies to EVERY living piece, AI included;
        // only the player's piece adds keyboard input to it.
        // A skill blocking Movement blocks the INPUT only; gravity / knock-back keep running
        // (charging skills turn gravity off themselves).
        // Dead pieces keep falling (no input) instead of freezing in mid-air, until their body breaks apart.
        // Pawn clones use exactly the same physics, so every skill effect treats them like real pieces.
        if (!IsMovementSuppressed && !bodyBroken)
            HandleMovement(readInput: !IsDead && canMove && IsPlayerControlled && !IsBlockedBySkill(SkillBlockMask.Movement));

        if (!canMove && !canAct)
            return;

        // A dead piece takes no input at all (no skills, no looking around, no shooting).
        if (canAct && IsPlayerControlled && !IsActionLocked && !IsDead)
        {
            HandleSkillInput();
        }

        if (IsPlayerControlled && !IsDead)
            HandleMouseLook();
        HandleWeaponBlock();
        UpdateWeaponSystemPosition();

        if (canAct && IsPlayerControlled && !IsDead)
        {
            // When attacking is blocked the gun still sees "trigger released / not aiming".
            HandleWeaponInput(attackBlocked: IsActionLocked || IsBlockedBySkill(SkillBlockMask.Attack));
        }

        bool shotThisFrame = currentGun != null &&  currentGun.IsShooting();

        if (currentGun != null)
        {
            UpdateRecoilSequenceBeforeWeaponTick(
                shotThisFrame
            );
        }

        if (currentWeapon != null)
        {
            currentWeapon.Tick(Time.deltaTime);
        }

        if (currentGun != null)
        {
            FinalizeRecoilSequenceAfterWeaponTick(
                shotThisFrame
            );
        }
    }

    // =========================
    // CONTROL API
    // =========================
    public void EnableControl() { canMove = true; canAct = true; }
    public void DisableControl()
    {
        canMove = false;
        canAct = false;
        verticalVelocity = 0f;
    }

    public void DisableAction() { canAct = false; }
    public void EnableAction() { canAct = true; }

    /// <summary>Gravity stays off while at least one owner disables it.</summary>
    public void SetGravityEnabled(object owner, bool enabled)
    {
        if (owner == null) return;

        if (enabled)
        {
            gravityDisablers.Remove(owner);
        }
        else
        {
            gravityDisablers.Add(owner);
            verticalVelocity = 0f;
        }
    }

    public bool IsGravityEnabled => useGravity;

    // ---- forced displacement ownership ----
    public bool IsUnderForcedMove => forcedMoveOwner != null;

    /// <summary>Claims this piece for a forced displacement. False when another owner already moves it.</summary>
    public bool TryBeginForcedMove(object owner)
    {
        if (owner == null) return false;
        if (forcedMoveOwner != null && !ReferenceEquals(forcedMoveOwner, owner)) return false;

        bool newClaim = forcedMoveOwner == null;
        forcedMoveOwner = owner;

        // Another skill now moves this piece: its own leap / dash / grapple stops
        // (two owners never move one piece). A skill claiming its own piece is not cancelled.
        if (newClaim)
        {
            foreach (var skill in boundSkills)
                if (skill != null && !ReferenceEquals(skill, owner) && skill.State == SkillState.Active && skill is IMovementOverride)
                    skill.ForceCancel();
        }
        return true;
    }

    /// <summary>
    /// Takes this piece over whatever moves it now: the skill currently moving it is cancelled
    /// (it releases what it held), any flight in progress stops, then <paramref name="owner"/> claims it.
    /// </summary>
    public bool ClaimForcedMove(object owner)
    {
        if (owner == null) return false;
        if (forcedMoveOwner != null && !ReferenceEquals(forcedMoveOwner, owner))
        {
            if (forcedMoveOwner is ActiveSkill holder)
                holder.ForceCancel();
            forcedMoveOwner = null;
        }
        ForceStopAllMotion();
        return TryBeginForcedMove(owner);
    }

    public void EndForcedMove(object owner)
    {
        if (ReferenceEquals(forcedMoveOwner, owner))
            forcedMoveOwner = null;
    }
    public void ForceStopAllMotion()
    {
        verticalVelocity = 0f;
        scatterVelocityOwner = Vector3.zero;
        scatting = false;
    }
    public WeaponController GetCurrentWeapon()
    {
        return currentWeapon;
    }
    public void SuppressMovement(object owner)
    {
        if (owner == null) return;
        movementSuppressors.Add(owner);
        ForceStopAllMotion();
    }

    public void ReleaseMovementSuppression(object owner)
    {
        if (owner == null) return;
        movementSuppressors.Remove(owner);
    }

    public void AbortActiveMovementSkill()
    {
        foreach (var s in abortableMovementSkills)
            s?.AbortMovement();
    }

    public void WarpPosition(Vector3 pos)
    {
        controller.enabled = false;
        transform.position = pos;
        controller.enabled = true;
    }

    // =========================
    // SKILL INPUT
    // =========================
    public void InterruptActiveMovementSkills()
    {
        foreach (var skill in boundSkills)
        {
            if (skill == null || skill.State != SkillState.Active)
                continue;

            if (skill is IMovementOverride)
                skill.ForceCancel();
        }

        ForceStopAllMotion();
    }
    /// <summary>
    /// Knock-back. A piece that is knocked loses its own leap / dash / grapple first
    /// (one mover at a time); the new hit replaces any flight in progress.
    /// </summary>
    public void AddExternalImpulse(Vector3 impulse)
    {
        InterruptActiveMovementSkills();
        scatterVelocityOwner = impulse;
        scatting = true;
        scatterAirborne = false;
    }

    /// <summary>Flying / sliding from a knock-back right now.</summary>
    public bool IsKnockedBack => scatting;

    public void ApplyExternalVerticalVelocity(float velocity)
    {
        verticalVelocity = velocity;
    }

    void HandleSkillInput()
    {
        if (!canAct || boundSkills.Count == 0 || Keyboard.current == null) return;

        if (Keyboard.current.fKey.wasPressedThisFrame)
        {
            foreach (var skill in boundSkills)
                skill.TryActivate();
        }
    }

    // =========================
    // WEAPON
    // =========================
    public void UpdateWeaponSystemPosition()
    {
        if (attackPoint == null || cameraPoint == null) return;
        if (currentGun != null)
        {
            attackPoint.position = cameraPoint.position + cameraPoint.forward * currentGun.GetFirePointDistance();
            attackPoint.forward = cameraPoint.forward;
            weaponBlockCheckDistance = currentGun.GetFirePointDistance();
            currentGun.BindAttackPoint(attackPoint);

            Vector3 normalWeaponPosition = cameraPoint.position + cameraPoint.forward * weaponHolderOffset.z + cameraPoint.up * weaponHolderOffset.y + cameraPoint.right * weaponHolderOffset.x;

            Quaternion normalWeaponRotation = cameraPoint.rotation;

            Transform holderParent = weaponHolder.parent;

            if (holderParent != null)
            {
                defaultWeaponLocalPos = holderParent.InverseTransformPoint(normalWeaponPosition);

                defaultWeaponLocalRot = Quaternion.Inverse(holderParent.rotation) * normalWeaponRotation;
            }
            else
            {
                defaultWeaponLocalPos = normalWeaponPosition;

                defaultWeaponLocalRot = normalWeaponRotation;
            }
            if (weaponBlockDelayTime <= 0f && !currentWeapon.GetBlocked())
            {
                currentWeaponLocalPos = defaultWeaponLocalPos;

                currentWeaponLocalRot = defaultWeaponLocalRot;

                weaponHolder.SetLocalPositionAndRotation( currentWeaponLocalPos, currentWeaponLocalRot );
            }
        }
    }

    public void BindWeapon(GameObject weapon, RectTransform centerScope, RectTransform topScope, RectTransform bottomScope, RectTransform leftScope, RectTransform rightScope)
    {
        if (!weapon) return;
        if (weaponHolder == null)
        {
            // Pieces without a holder in their prefab (e.g. AI pieces) get one at the eye.
            weaponHolder = new GameObject("WeaponHolder").transform;
            weaponHolder.SetParent(cameraPoint != null ? cameraPoint : transform, false);
        }
        weaponObject = Instantiate(weapon, weaponHolder);
        weaponObject.SetActive(true);
        if (weaponObject.TryGetComponent(out Rigidbody weaponBody))
        {
            weaponBody.useGravity = false;
            weaponBody.isKinematic = true;
        }
        weaponObject.transform.SetParent(weaponHolder);
        weaponObject.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

        weaponBoxCollider = weaponObject.GetComponent<BoxCollider>();

        if (weaponBoxCollider != null)
        {
            Transform boxTransform = weaponBoxCollider.transform;

            Vector3 weaponBoxCenter = boxTransform.TransformPoint( weaponBoxCollider.center );

            weaponBoxLocalCenter = weaponHolder.InverseTransformPoint(weaponBoxCenter);

            weaponBoxLocalRotation = Quaternion.Inverse(weaponHolder.rotation) * boxTransform.rotation;

            Vector3 scale = boxTransform.lossyScale;

            weaponBoxHalfExtents = Vector3.Scale( weaponBoxCollider.size * 0.5f, new Vector3( Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z) ) );
        }

        currentWeapon = weaponObject.GetComponent<WeaponController>();
        RefreshSkillContext(); // skills see the new weapon
        if (currentWeapon is GunController gun)
        {
            currentGun = gun;

            currentGun.SetCrosshair(centerScope, topScope, bottomScope, leftScope, rightScope);
            currentGun.BindAttackPoint(attackPoint);
        }

        defaultWeaponLocalPos = weaponHolder.localPosition;
        defaultWeaponLocalRot = weaponHolder.localRotation;

        currentWeaponLocalPos = defaultWeaponLocalPos;
        currentWeaponLocalRot = defaultWeaponLocalRot;
    }

    protected virtual void HandleWeaponInput(bool attackBlocked)
    {
        if (!canAct || currentWeapon == null) return;
        if (Mouse.current == null) return;

        bool fireHeld = Mouse.current.leftButton.isPressed;
        if (!fireHeld)
            fireHeldThroughBlock = false;
        else if (attackBlocked)
            fireHeldThroughBlock = true; // must release and press again once the block ends

        bool firePressed = !attackBlocked && fireHeld && !fireHeldThroughBlock;
        bool aimPressed = !attackBlocked && Mouse.current.rightButton.isPressed;

        if (firePressed)
        {
            currentWeapon.TryAttack();
        }

        if (currentGun != null)
        {
            if (aimPressed && !currentGun.GetBlocked() && (weaponBlockDelayTime <= 0))
            {
                currentGun.SetAiming(true);
            }
            else
            {
                currentGun.SetAiming(false);
            }

            if (!fireHeld)
            {
                if(!currentGun.GetTriggerReleasedSinceLastShot()) currentGun.ResetHorizontalRecoilDirection();
                currentGun.SetTriggerReleasedSinceLastShot(true);
                currentGun.SetRequireTriggerReleaseAfterReload(false);
            }

            if (Keyboard.current == null) return;

            if (Keyboard.current.bKey.wasPressedThisFrame)
            {
                currentGun.SwitchFireMode();
                Debug.Log("Switched fire mode to: " + currentGun.GetCurrentFireMode());
            }

            if (Keyboard.current.rKey.wasPressedThisFrame)
            {
                currentGun.StartReload();
            }
        }    
    }

    // =========================
    // CAMERA
    // =========================
    void HandleMouseLook()
    {
        if (Mouse.current == null) return;

        Vector2 delta = Mouse.current.delta.ReadValue();

        float mouseX = delta.x * mouseSensitivity * Time.deltaTime;
        float mouseY = delta.y * mouseSensitivity * Time.deltaTime;

        recoilSequenceFrameStartPitch = pitch;

        recoilSequenceFrameMouseX = mouseX;
        recoilSequenceFrameMouseY = mouseY;

        yaw += mouseX;
        pitch -= mouseY;

        float verticalAppliedRecoil = 0f;

        if (currentGun != null)
        {
            verticalAppliedRecoil =
                Mathf.Max(
                    0f,
                    currentGun.GetAppliedRecoil().y
                );
        }

        pitch = Mathf.Clamp(
            pitch,
            minPitch + verticalAppliedRecoil,
            maxPitch + verticalAppliedRecoil
        );

        recoilSequenceFramePitchDelta =
            pitch - recoilSequenceFrameStartPitch;

        transform.rotation =
            Quaternion.Euler(0f, yaw, 0f);

        Quaternion lookRot =
            Quaternion.Euler(pitch, yaw, 0f);

        if (currentWeapon is GunController gun)
        {
            Vector2 recoil =
                gun.GetAppliedRecoil();

            lookRot *= Quaternion.Euler(
                -recoil.y,
                recoil.x,
                0f
            );
        }

        cameraPoint.rotation = lookRot;
    }
    void UpdateRecoilSequenceBeforeWeaponTick(bool shotThisFrame)
    {
        if (currentGun == null)
            return;

        bool weaponInterrupted =
            currentGun.IsReloading() ||
            currentGun.GetBlocked();

        bool recoveryActive =
            currentGun.IsRecoilRecoveryActive();

        bool continuationActive =
            currentGun.IsRecoilSequenceContinuationActive();

        if (
            shotThisFrame &&
            !recoilSequenceActive &&
            !weaponInterrupted
        )
        {
            currentGun.ClearSequenceRecoveryTarget();

            recoilSequenceActive = true;

            recoilSequenceBaselinePitch =
                recoilSequenceFrameStartPitch;

            recoilSequenceUpwardPitch = 0f;

            recoilSequenceHasUpwardIntent = false;
            recoilSequenceSuppressRecovery = false;

            recoilSequenceVirtualRecoveryActive = false;
            recoilSequenceVirtualRecoveryTargetY = 0f;
        }

        if (!recoilSequenceActive)
            return;

        // Weapon interruption freezes new player recoil intent,
        // but does NOT terminate recovery lifecycle.
        if (!weaponInterrupted)
        {
            if (
                recoilSequenceFramePitchDelta <
                -recoilSequenceInputEpsilon
            )
            {
                recoilSequenceUpwardPitch +=
                    -recoilSequenceFramePitchDelta;

                recoilSequenceHasUpwardIntent = true;
            }
            else if (
                recoilSequenceFramePitchDelta >
                    recoilSequenceInputEpsilon &&
                recoilSequenceHasUpwardIntent &&
                pitch >
                    recoilSequenceBaselinePitch +
                    recoilSequenceInputEpsilon
            )
            {
                recoilSequenceSuppressRecovery = true;
            }
        }

        float currentVerticalRecoil =
            currentGun.GetCurrentVerticalRecoil();

        float desiredBaselinePitch =
            recoilSequenceBaselinePitch -
            recoilSequenceUpwardPitch;

        float normalRecoveryTargetY =
            Mathf.Max(
                0f,
                pitch - desiredBaselinePitch
            );

        bool wouldHaveNoRecovery =
            recoilSequenceSuppressRecovery ||
            normalRecoveryTargetY >
                currentVerticalRecoil +
                recoilSequenceInputEpsilon;

        if (
            recoveryActive &&
            !continuationActive
        )
        {
            if (wouldHaveNoRecovery)
            {
                ActivateNoRecoveryDownwardRecovery();
            }
            else if (
                !weaponInterrupted &&
                HasMeaningfulRecoilSequenceMouseInput()
            )
            {
                EndRecoilSequenceByPlayerInput();
                return;
            }
        }

        if (recoilSequenceVirtualRecoveryActive)
        {
            recoilSequenceRecoveryTargetY =
                recoilSequenceVirtualRecoveryTargetY;
        }
        else if (recoilSequenceSuppressRecovery)
        {
            recoilSequenceRecoveryTargetY =
                currentVerticalRecoil;
        }
        else
        {
            recoilSequenceRecoveryTargetY =
                normalRecoveryTargetY;
        }

        currentGun.SetSequenceRecoveryTarget(
            recoilSequenceRecoveryTargetY
        );
    }
    void ActivateNoRecoveryDownwardRecovery()
    {
        if (currentGun == null)
            return;

        if (recoilSequenceVirtualRecoveryActive)
            return;

        float currentVerticalRecoil =
            currentGun.GetCurrentVerticalRecoil();

        recoilSequenceVirtualRecoveryActive = true;

        recoilSequenceVirtualRecoveryTargetY =
            Mathf.Max(
                0f,
                currentVerticalRecoil -
                currentGun.GetNoRecoveryDownwardRecoveryAmount()
            );

        recoilSequenceRecoveryTargetY =
            recoilSequenceVirtualRecoveryTargetY;

        currentGun.SetSequenceRecoveryTarget(
            recoilSequenceRecoveryTargetY
        );
    }

    bool HasMeaningfulRecoilSequenceMouseInput()
    {
        return
            Mathf.Abs(recoilSequenceFrameMouseX) >
                recoilSequenceInputEpsilon ||
            Mathf.Abs(recoilSequenceFrameMouseY) >
                recoilSequenceInputEpsilon;
    }
    void FinalizeRecoilSequenceAfterWeaponTick(
    bool shotThisFrame
)
    {
        if (currentGun == null)
            return;

        if (shotThisFrame)
            return;

        if (!recoilSequenceActive)
            return;

        if (!currentGun.IsRecoilRecoveryActive())
            return;

        if (currentGun.IsRecoilSequenceContinuationActive())
            return;

        if (!currentGun.IsSequenceRecoveryAtTarget(
                recoilSequenceRecoveryTargetY,
                recoilSequenceInputEpsilon))
        {
            return;
        }

        float consumedRecoil =
            currentGun.ConsumeSequenceVerticalRecoil();

        pitch -= consumedRecoil;

        ResetRecoilSequenceState();
    }
    void EndRecoilSequenceByPlayerInput()
    {
        if (currentGun == null)
            return;
        float consumedRecoil =
            currentGun.ConsumeSequenceVerticalRecoil();

        pitch -= consumedRecoil;

        ResetRecoilSequenceState();
    }
    void ResetRecoilSequenceState()
    {
        recoilSequenceActive = false;

        recoilSequenceBaselinePitch = pitch;
        recoilSequenceUpwardPitch = 0f;
        recoilSequenceRecoveryTargetY = 0f;

        recoilSequenceHasUpwardIntent = false;
        recoilSequenceSuppressRecovery = false;

        recoilSequenceVirtualRecoveryActive = false;
        recoilSequenceVirtualRecoveryTargetY = 0f;

        if (currentGun != null)
            currentGun.ClearSequenceRecoveryTarget();
    }
    // =========================
    // MOVEMENT OVERRIDE
    // =========================
    /// <summary>Speed/jump multipliers keyed by owner (a skill instance or a source enum).</summary>
    public void OverrideMovement(object owner, float moveMul, float jumpMul)
    {
        if (owner == null) return;
        overrides[owner] = new Vector2(Mathf.Clamp01(moveMul), Mathf.Clamp01(jumpMul));
        RecalculateOverrides();
    }

    public void ClearOverrideMovement(object owner)
    {
        if (owner != null && overrides.Remove(owner))
            RecalculateOverrides();
    }

    void RecalculateOverrides()
    {
        moveOverrideMul = 1f;
        jumpOverrideMul = 1f;

        foreach (var kv in overrides)
        {
            moveOverrideMul = Mathf.Min(moveOverrideMul, kv.Value.x);
            jumpOverrideMul = Mathf.Min(jumpOverrideMul, kv.Value.y);
        }

        hasMoveOverride = overrides.Count > 0;
    }

    // =========================
    // MOVEMENT
    // =========================
    void HandleMovement(bool readInput)
    {
        isGrounded = controller.isGrounded;

        if (isGrounded && verticalVelocity <= 0f)
        {
            isJumping = false;

            // Fix: never keep the fall speed accumulated in the air while standing
            // (walking off a ledge later would drop at that old speed).
            if (verticalVelocity < -2f)
                verticalVelocity = -2f;
        }

        Keyboard kb = readInput ? Keyboard.current : null;

        Vector2 input = Vector2.zero;
        if (kb != null)
        {
            if (kb.wKey.isPressed) input.y += 1;
            if (kb.sKey.isPressed) input.y -= 1;
            if (kb.aKey.isPressed) input.x -= 1;
            if (kb.dKey.isPressed) input.x += 1;
        }

        input = Vector2.ClampMagnitude(input, 1f);
        Vector3 inputDirection = transform.right * input.x + transform.forward * input.y;
        float speed = hasMoveOverride ? moveSpeed * moveOverrideMul : moveSpeed;

        if (kb != null && kb.spaceKey.wasPressedThisFrame && isGrounded && !IsBlockedBySkill(SkillBlockMask.Jump))
        {
            float jump = hasMoveOverride ? jumpHeight * jumpOverrideMul : jumpHeight;
            verticalVelocity = Mathf.Sqrt(jump * -2f * gravity);

            jumpMoveDirection = inputDirection;
            isJumping = true;
        }

        Vector3 moveDirection = isGrounded ? inputDirection : Vector3.zero;

        if (useGravity && !isGrounded)
            verticalVelocity += gravity * Time.deltaTime;

        if (scatting)
        {
            scatterVelocityOwner.y += scatterGravity * Time.deltaTime;

            Vector3 scrat = scatterVelocityOwner * Time.deltaTime;
            CollisionFlags flags = controller.Move(scrat);
            bool onGround = (flags & CollisionFlags.Below) != 0 && scatterVelocityOwner.y < 0f;

            if (!onGround)
            {
                scatterAirborne = true;
            }
            else if (scatterAirborne)
            {
                // Landing after a real flight: stop (as before).
                scatting = false;
                scatterVelocityOwner.y = 0f;
                scatterVelocityOwner.x *= 0.15f;
                scatterVelocityOwner.z *= 0.15f;
            }
            else
            {
                // Horizontal knock-back that never left the ground: slide with friction.
                // (It used to stop on the first frame: a 15 m/s push moved a piece ~0.25 m.)
                scatterVelocityOwner.y = 0f;
                float k = Mathf.Exp(-knockbackGroundFriction * Time.deltaTime);
                scatterVelocityOwner.x *= k;
                scatterVelocityOwner.z *= k;
                if (new Vector2(scatterVelocityOwner.x, scatterVelocityOwner.z).magnitude < knockbackStopSpeed)
                    scatting = false;
            }
        }
        else
        {
            if (isJumping) moveDirection = jumpMoveDirection;
            controller.Move((moveDirection * speed + Vector3.up * verticalVelocity) * Time.deltaTime);
        }
    }

    // =========================
    // WEAPON BLOCK
    // =========================
    bool IsOwnCollider(Collider hit)
    {
        ChessPieceFPSController owner =
            hit.GetComponent<ChessPieceFPSController>();

        return owner == this;
    }
    bool IsWeaponColliderBlocked()
    {
        if (
            weaponBoxCollider == null ||
            cameraPoint == null
        )
        {
            return false;
        }

        Vector3 normalWeaponPosition =
            cameraPoint.position +
            cameraPoint.forward * weaponHolderOffset.z +
            cameraPoint.up * weaponHolderOffset.y +
            cameraPoint.right * weaponHolderOffset.x;

        Quaternion normalWeaponRotation =
            cameraPoint.rotation;

        Vector3 center =
            normalWeaponPosition +
            normalWeaponRotation *
            weaponBoxLocalCenter;

        Quaternion rotation =
            normalWeaponRotation *
            weaponBoxLocalRotation;

        int overlapCount =
            Physics.OverlapBoxNonAlloc(
                center,
                weaponBoxHalfExtents,
                weaponBlockOverlapBuffer,
                rotation,
                weaponBlockLayer,
                QueryTriggerInteraction.Ignore
            );

        for (int i = 0; i < overlapCount; i++)
        {
            Collider hit =
                weaponBlockOverlapBuffer[i];

            if (hit == null)
                continue;

            if (hit == weaponBoxCollider)
                continue;

            if (IsOwnCollider(hit))
            {
                continue;
            }

            return true;
        }

        return false;
    }
    void HandleWeaponBlock()
    {
        if (currentWeapon == null || attackPoint == null || cameraPoint == null) return;

        bool attackPointBlocked = Physics.SphereCast(
            cameraPoint.position, 0.0005f, cameraPoint.forward,
            out _, weaponBlockCheckDistance, weaponBlockLayer,
            QueryTriggerInteraction.Ignore
        );

        bool weaponColliderBlocked = IsWeaponColliderBlocked();

        bool blocked = attackPointBlocked || weaponColliderBlocked;

        if (!blocked && currentGun != null && currentGun.IsReloading()) blocked = true;
        if (!blocked && IsActionLocked) blocked = true; // locked by a skill (e.g. the queen firing): weapon held in the blocked pose

        if (blocked != currentWeapon.GetBlocked())
        {
            currentWeapon.SetBlocked(blocked);

            float adjustSpeed = weaponAdjustSpeed;
            if (blocked) adjustSpeed = weaponAdjustSpeed * 0.15f;
            weaponBlockDelayTime = adjustSpeed > 0f ? 3f / adjustSpeed : 0f;
        }

        Vector3 targetPos = blocked ? weaponBlockedPosition.localPosition : defaultWeaponLocalPos;
        Quaternion targetRot = blocked ? weaponBlockedPosition.localRotation : defaultWeaponLocalRot;
        
        if(currentWeaponLocalPos != targetPos || currentWeaponLocalRot != targetRot)
        {
            float adjustSpeed = weaponAdjustSpeed;
            if (blocked) adjustSpeed = weaponAdjustSpeed * 0.15f;
            currentWeaponLocalPos = Vector3.Lerp(currentWeaponLocalPos, targetPos, Time.deltaTime * adjustSpeed);
            currentWeaponLocalRot = Quaternion.Slerp(currentWeaponLocalRot, targetRot, Time.deltaTime * adjustSpeed);

            bool reachedTarget = Vector3.Distance( currentWeaponLocalPos, targetPos ) <= 0.005f;

            if (reachedTarget)
            {
                currentWeaponLocalPos = targetPos;
                currentWeaponLocalRot = targetRot;
            }

            weaponHolder.SetLocalPositionAndRotation(currentWeaponLocalPos, currentWeaponLocalRot);

            if (!blocked && reachedTarget)
            {
                weaponBlockDelayTime = 0f;
            }
            else if (weaponBlockDelayTime > 0f)
            {
                weaponBlockDelayTime -= Time.deltaTime;

                if (weaponBlockDelayTime <= 0f)
                {
                    weaponBlockDelayTime = 0f;
                }
            }
        }
        else if (!blocked)
        {
            weaponBlockDelayTime = 0f;
        }
    }
}
public interface IAbortableMovementSkill
{
    void AbortMovement();
}