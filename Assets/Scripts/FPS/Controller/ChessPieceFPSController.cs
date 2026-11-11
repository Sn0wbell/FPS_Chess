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
    /// <summary>This piece's gravity (walking, jumping and knock-back flights all use it).</summary>
    public float Gravity => gravity;
    /// <summary>Knock-back bounces (used to predict where a thrown piece comes to rest).</summary>
    public float KnockbackBounciness => knockbackBounciness;
    public float MinBounceSpeed => minBounceSpeed;
    public float GroundBounciness => groundBounciness;
    public float GroundBounceFriction => groundBounceFriction;
    public int MaxGroundBounces => maxGroundBounces;

    private float verticalVelocity;
    private bool isGrounded;
    private bool onSteepGround;  // touching only ground steeper than maxStandAngle (a cliff, a trunk, a ledge edge)
    private Vector3 steepNormal;
    // Contacts the CharacterController reported under the body during this frame's moves (what makes it "grounded").
    private bool supportContact;
    private Vector3 groundNormal = Vector3.up;
    private float supportAngle = 180f;
    private Vector3 supportNormal = Vector3.up;

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
    private bool scatting = false;
    private bool scatterAirborne;
    private bool fireHeldThroughBlock;

    [Header("Knock-back")]
    [Tooltip("Ground friction for horizontal knock-backs (higher = shorter slide).")]
    [SerializeField] private float knockbackGroundFriction = 8f;
    [Tooltip("A sliding knock-back ends below this speed (m/s).")]
    [SerializeField] private float knockbackStopSpeed = 0.5f;
    [Tooltip("Steepest surface a piece can stand on (degrees). Resting only on steeper ones (a trunk, a cliff, a sharp edge) it slides off and cannot jump or leap from there. Walking up slopes still follows the CharacterController's slope limit.")]
    [Range(0f, 90f)]
    [SerializeField] private float maxStandAngle = 75f;
    [Tooltip("A knocked piece bounces off walls (steeper than the slope limit): share of the speed into the wall it keeps (0 = no bounce).")]
    [Range(0f, 1f)]
    [SerializeField] private float knockbackBounciness = 0.5f;
    [Tooltip("It only bounces off the ground when it hits it at least this fast (m/s); slower, it lands.")]
    [SerializeField] private float minBounceSpeed = 3f;
    [Tooltip("Bounce off the ground (walkable slope): share of the falling speed it keeps (0 = never bounces off the ground).")]
    [Range(0f, 1f)]
    [SerializeField] private float groundBounciness = 0.3f;
    [Tooltip("Share of its speed along the ground it keeps at each ground bounce (1 = no friction).")]
    [Range(0f, 1f)]
    [SerializeField] private float groundBounceFriction = 0.5f;
    [Tooltip("Ground bounces per knock at most; after that it lands.")]
    [SerializeField] private int maxGroundBounces = 2;
    [Tooltip("Bounce off another piece's body (like off a wall; the other piece is not moved): share of the speed into it it keeps. Its head (flat) counts as ground.")]
    [Range(0f, 1f)]
    [SerializeField] private float pieceBounciness = 0.5f;

    // =========================
    // MOUSE LOOK
    // =========================
    [Header("Mouse Look")]
    [SerializeField] private float mouseSensitivity = 15f;
    private const float MouseFrameScale = 1f / 60f;
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
    private GameObject boundWeaponSource; // the weapon (prefab) this piece was given: its pawn clones carry the same
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
    private readonly Dictionary<object, float> knockOverrides = new Dictionary<object, float>(); // knock-back multipliers

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

        StopAimingView(); // dead while aiming: the death cam is not zoomed in

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
    /// <summary>A pawn clone breaking apart: its hidden body stops moving and blocking at once.</summary>
    public void BreakCloneBody()
    {
        if (!IsPawnClone) return;
        bodyBroken = true;
        if (controller != null)
            controller.enabled = false;
    }

    /// <summary>A pawn clone reused from the pool: whole again.</summary>
    public void RestoreCloneBody()
    {
        if (!IsPawnClone) return;
        bodyBroken = false;
        if (controller != null)
            controller.enabled = true;
    }

    void HandleRevive()
    {
        isDeadRaw = false;
        // Back to life at rest: not still flying from the knock-back it died in.
        scatting = false;
        scatterVelocityOwner = Vector3.zero;
        verticalVelocity = 0f;
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
        {
            // The weapon still sits in the hands (and is lowered against a wall) without control: AI pieces, pawn clones.
            if (!bodyBroken)
            {
                HandleWeaponBlock();
                UpdateWeaponSystemPosition();

                // The player's view (zoom + gun model) still finishes going where it was heading when control ended:
                // aiming at the match end, the aimed view stays (never frozen half way).
                if (IsPlayerControlled && currentGun != null)
                    currentGun.UpdateAimView();
            }
            return;
        }

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
        // (Aiming is kept: at the match end the player still sees the aimed view, e.g. the last enemy breaking apart.)
    }

    /// <summary>The player's piece stops aiming at once (death): the camera is back to its normal view.</summary>
    private void StopAimingView()
    {
        if (IsPlayerControlled && currentGun != null)
            currentGun.StopAiming();
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
    /// Takes this piece over whatever moves it now (the later skill wins): the skill holding it lets go of it
    /// (a king's pull releases just this piece, a bishop's rope snaps; any other skill is cancelled),
    /// any flight in progress stops, then <paramref name="owner"/> claims it.
    /// </summary>
    public bool ClaimForcedMove(object owner)
    {
        if (owner == null) return false;
        if (forcedMoveOwner != null && !ReferenceEquals(forcedMoveOwner, owner))
        {
            if (forcedMoveOwner is IHeldPieceKnockHandler handler)
                handler.OnHeldPieceTakenOver(this);
            else if (forcedMoveOwner is ActiveSkill holder)
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
    /// Knock multipliers (e.g. a king's gravity zone) scale it, except the one of the source itself; a skill
    /// holding it (rope, pull) is then told, with the flight already set.
    /// </summary>
    public void AddExternalImpulse(Vector3 impulse, object source = null)
    {
        foreach (var kv in knockOverrides)
            if (!ReferenceEquals(kv.Key, source))
                impulse *= kv.Value;

        InterruptActiveMovementSkills();
        scatterVelocityOwner = impulse;
        scatting = true;
        scatterAirborne = false;
        groundBounces = 0;

        if (forcedMoveOwner is IHeldPieceKnockHandler holder && !ReferenceEquals(holder, source))
            holder.OnHeldPieceKnocked(this);
    }

    private int groundBounces; // ground bounces of the current knock

    /// <summary>
    /// A knocked piece about to hit a wall (or the ground fast enough, while flying) bounces off it like a real body:
    /// moves up to the surface, then v' = v - (1 + e)(v.n)n (the part into the surface is reversed and scaled by e,
    /// the part along it is kept), so every bounce is weaker. Another piece is bounced off like a wall (pieceBounciness; its flat head counts as ground); it is not moved.
    /// </summary>
    private bool TryBounce(Vector3 step)
    {
        float distance = step.magnitude;
        if (distance < 1e-5f || controller == null)
            return false;

        Vector3 center = controller.bounds.center;
        float half = Mathf.Max(0f, controller.height * 0.5f - controller.radius);
        int mask = Physics.DefaultRaycastLayers;
        // Nearest surface ahead that is not part of this piece (its own weapon, overlay copies...).
        // Other pieces count too (a dead one's body is switched off: not hit).
        RaycastHit hit = default;
        bool found = false;
        foreach (RaycastHit h in Physics.CapsuleCastAll(center + Vector3.up * half, center - Vector3.up * half, controller.radius * 0.95f,
                     step / distance, distance + controller.skinWidth, mask, QueryTriggerInteraction.Ignore))
        {
            if (h.collider == null || h.distance <= 0f || h.collider.transform.IsChildOf(transform))
                continue;
            if (!found || h.distance < hit.distance)
            {
                hit = h;
                found = true;
            }
        }
        if (!found)
            return false;

        Vector3 n = hit.normal;
        float into = Vector3.Dot(scatterVelocityOwner, n); // < 0: moving into the surface
        if (into >= 0f)
            return false;

        // Ground = a walkable slope (the same limit the piece walks up), anything steeper is a wall.
        bool ground = Vector3.Angle(n, Vector3.up) <= controller.slopeLimit;
        bool otherPiece = hit.collider.GetComponentInParent<ChessPieceFPSController>() != null;
        float e = ground ? groundBounciness : otherPiece ? pieceBounciness : knockbackBounciness;
        if (e <= 0f)
            return false;
        if (ground && (!scatterAirborne || -into < minBounceSpeed || groundBounces >= maxGroundBounces))
            return false; // sliding along it, too slow or bounced enough: lands as usual

        controller.Move(step / distance * Mathf.Max(0f, hit.distance - controller.skinWidth));
        Vector3 normalPart = into * n;
        Vector3 alongPart = scatterVelocityOwner - normalPart;
        if (ground)
        {
            alongPart *= groundBounceFriction; // friction: it does not skid far along the ground
            groundBounces++;
        }
        scatterVelocityOwner = alongPart - e * normalPart;
        scatterAirborne = true;
        return true;
    }

    /// <summary>Flying / sliding from a knock-back right now.</summary>
    public bool IsKnockedBack => scatting;

    /// <summary>Standing (what a jump needs): not in the air, not on a surface steeper than maxStandAngle.</summary>
    public bool IsOnWalkableGround => isGrounded;


    /// <summary>Contacts under the body (lower half-sphere) while moving: the flattest one is what it stands on.</summary>
    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (controller == null || hit.collider == null || hit.collider.transform.IsChildOf(transform))
            return;
        Vector3 lowerSphere = controller.bounds.center - Vector3.up * Mathf.Max(0f, controller.height * 0.5f - controller.radius);
        if (hit.point.y > lowerSphere.y)
            return; // beside or above: not holding it up
        float angle = Vector3.Angle(hit.normal, Vector3.up);
        supportContact = true;
        if (angle < supportAngle)
        {
            supportAngle = angle;
            supportNormal = hit.normal;
        }
    }

    /// <summary>Something no steeper than maxStandAngle right under the feet (other pieces do not count).</summary>
    private bool StandableGroundUnderFeet()
    {
        float r = controller.radius * 0.95f;
        Vector3 bottom = controller.bounds.center - Vector3.up * Mathf.Max(0f, controller.height * 0.5f - controller.radius);
        int mask = Physics.DefaultRaycastLayers & ~(1 << gameObject.layer);
        foreach (RaycastHit h in Physics.SphereCastAll(bottom + Vector3.up * 0.05f, r, Vector3.down, controller.skinWidth + 0.15f, mask, QueryTriggerInteraction.Ignore))
            if (h.collider != null && !h.collider.transform.IsChildOf(transform) && Vector3.Angle(h.normal, Vector3.up) <= maxStandAngle)
                return true;
        return false;
    }

    /// <summary>
    /// Everything right under the feet is steeper than maxStandAngle (nothing to stand on among them).
    /// Other pieces do not count (standing on a piece's head is standing).
    /// </summary>
    private bool SteepGroundUnderFeet(out Vector3 normal)
    {
        normal = Vector3.up;
        float r = controller.radius * 0.95f;
        Vector3 bottom = controller.bounds.center - Vector3.up * Mathf.Max(0f, controller.height * 0.5f - controller.radius);
        int mask = Physics.DefaultRaycastLayers & ~(1 << gameObject.layer);
        bool steep = false;
        float nearest = float.PositiveInfinity;
        foreach (RaycastHit h in Physics.SphereCastAll(bottom + Vector3.up * 0.05f, r, Vector3.down, controller.skinWidth + 0.15f, mask, QueryTriggerInteraction.Ignore))
        {
            if (h.collider == null || h.collider.transform.IsChildOf(transform))
                continue;
            if (Vector3.Angle(h.normal, Vector3.up) <= maxStandAngle)
                return false; // something to stand on: standing
            if (h.distance < nearest)
            {
                nearest = h.distance;
                normal = h.normal;
                steep = true;
            }
        }
        return steep;
    }

    /// <summary>Velocity of the knock-back flight in progress (a rope holding the piece constrains it).</summary>
    public Vector3 KnockVelocity
    {
        get => scatterVelocityOwner;
        set { if (scatting) scatterVelocityOwner = value; }
    }

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

    /// <summary>The weapon (prefab) last given with BindWeapon.</summary>
    public GameObject BoundWeaponPrefab => boundWeaponSource;

    public Vector3 WeaponHolderOffset => weaponHolderOffset;

    /// <summary>
    /// Gives a piece without a camera point (a pawn clone) an aim point: the weapon, its attack point and the
    /// weapon block then work like on a real piece (an AI turns the aim point to look up / down).
    /// </summary>
    public void UseAimPoint(Transform aim, Vector3 holderOffset)
    {
        cameraPoint = aim;
        weaponHolderOffset = holderOffset;
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
        boundWeaponSource = weapon;
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

        // The mouse delta is already this frame's movement: scaled by a fixed 60 FPS frame (same feel as before
        // at 60 FPS), not by deltaTime, so turning does not depend on the frame rate.
        float mouseX = delta.x * mouseSensitivity * MouseFrameScale;
        float mouseY = delta.y * mouseSensitivity * MouseFrameScale;

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
    /// <summary>
    /// Speed/jump multipliers keyed by owner (a skill instance or a source enum); knockMul scales the knock-backs
    /// this piece takes from any other source while it lasts.
    /// </summary>
    public void OverrideMovement(object owner, float moveMul, float jumpMul, float knockMul = 1f)
    {
        if (owner == null) return;
        overrides[owner] = new Vector2(Mathf.Clamp01(moveMul), Mathf.Clamp01(jumpMul));
        if (knockMul < 1f) knockOverrides[owner] = Mathf.Clamp01(knockMul);
        else knockOverrides.Remove(owner);
        RecalculateOverrides();
    }

    public void ClearOverrideMovement(object owner)
    {
        if (owner == null) return;
        knockOverrides.Remove(owner);
        if (overrides.Remove(owner))
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
        // Resting only on something steeper than maxStandAngle (a near-vertical trunk, a rock's edge): not
        // standing: no walking, jumping or leaping from there; it slides down / off it.
        // What it rests on: the contacts the controller reported (exact, also on edges); none reported (e.g. standing
        // on another piece): a look right under the feet.
        if (supportContact)
        {
            // Only steep when nothing to stand on is right under the feet either (pressed against a trunk's foot,
            // the contacts alternate between the trunk and the ground: that is standing, not sliding).
            onSteepGround = isGrounded && supportAngle > maxStandAngle && !StandableGroundUnderFeet();
            steepNormal = supportNormal;
        }
        else
            onSteepGround = isGrounded && SteepGroundUnderFeet(out steepNormal);
        // The ground it walks on (for walking down slopes along them).
        groundNormal = isGrounded && supportContact && supportAngle <= maxStandAngle ? supportNormal : Vector3.up;
        supportContact = false;
        supportAngle = 180f;
        if (onSteepGround)
            isGrounded = false;

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
            scatterVelocityOwner.y += gravity * Time.deltaTime; // a knocked piece falls with its own gravity

            Vector3 scrat = scatterVelocityOwner * Time.deltaTime;
            if (TryBounce(scrat))
                return; // bounced off a wall / the ground this frame
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
            Vector3 walk = moveDirection * speed;
            // Walking down a slope: along it (same speed), so it does not step off into the air every frame
            // (which made it stutter and lose its speed). Uphill and on flat ground: unchanged.
            if (isGrounded && !isJumping && walk.sqrMagnitude > 1e-6f)
            {
                Vector3 along = Vector3.ProjectOnPlane(walk, groundNormal);
                if (along.y < 0f && along.sqrMagnitude > 1e-6f)
                    walk = along.normalized * walk.magnitude;
            }
            Vector3 velocity = walk + Vector3.up * verticalVelocity;
            if (onSteepGround && verticalVelocity < 0f)
                velocity = Vector3.ProjectOnPlane(velocity, steepNormal); // slides down along it (and off an edge)
            controller.Move(velocity * Time.deltaTime);
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