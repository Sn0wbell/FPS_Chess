using UnityEngine;
using System.Collections;

/// <summary>
/// One piece of the wall the Rook leaves behind its dash. Blocks movement and bullets,
/// can be shot down, falls to the ground when the dash ends, and can be pulled over by a
/// Bishop's grapple to open a line of fire.
/// </summary>
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
public class WallSegment : MonoBehaviour, IDamageable, ITeamOwned
{
    [Header("Health")]
    [SerializeField] private float maxHealth = 50f;

    [Header("Physics")]
    [Tooltip("Mass (kg). Same material as the pieces, about 10x a piece (~80 kg): bullets barely move it.")]
    [SerializeField] private float mass = 800f;
    [Tooltip("Speed given to its top when a skill knocks it over (the queen's push, the king's pull; the bishop uses its own).")]
    [SerializeField] private float toppleSpeed = 6f;

    private float currentHealth;
    private RookSkill owner;
    private Team team;
    private Coroutine lifeRoutine;

    private Rigidbody rb;
    private Collider col;
    private Vector3 lineForward = Vector3.forward; // along the rook's dash (horizontal)
    private float stillTime;                        // time since the last check of how far it really moved
    private Vector3 settleFromPos;                  // where it was at that check
    private Quaternion settleFromRot;
    private const float SettleSeconds = 0.25f;      // checked over this long:
    private const float SettleDistance = 0.05f;     // moved less than this (m)...
    private const float SettleAngle = 2f;           // ...and turned less than this (deg): at rest, it becomes fixed
    private static PhysicsMaterial gripMaterial;   // high friction: it does not slide

    public Team Team => team;
    public bool IsToppled { get; private set; }
    /// <summary>Knocked over by a skill (the queen's push, the king's pull): lying on its side.</summary>
    public bool IsLoose { get; private set; }
    public float CurrentHealth => currentHealth;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<Collider>();

        col.isTrigger = false;
        if (gripMaterial == null)
            gripMaterial = new PhysicsMaterial("WallGrip")
            {
                staticFriction = 1f,
                dynamicFriction = 1f,
                frictionCombine = PhysicsMaterialCombine.Maximum
            };
        col.sharedMaterial = gripMaterial;

        rb.mass = mass;
        rb.useGravity = false;
        rb.isKinematic = true;
        rb.constraints = RigidbodyConstraints.FreezeAll;
    }

    public void Init(RookSkill skill, Team ownerTeam)
    {
        owner = skill;
        team = ownerTeam;
    }

    public void Activate(Vector3 position, Quaternion rotation, float lifetime)
    {
        PieceDeathEffect.Restore(gameObject); // reused from the pool: visible again
        currentHealth = maxHealth;
        IsToppled = false;
        Vector3 f = rotation * Vector3.forward;
        f.y = 0f;
        lineForward = f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;

        transform.SetPositionAndRotation(position, rotation);
        gameObject.SetActive(true);

        if (lifeRoutine != null)
            StopCoroutine(lifeRoutine);

        lifeRoutine = StartCoroutine(LifeTimer(lifetime));
    }

    public void TakeDamage(DamageInfo info)
    {
        currentHealth -= info.amount;
        if (currentHealth <= 0f)
            Break();
    }

    private IEnumerator LifeTimer(float t)
    {
        yield return new WaitForSeconds(t);
        Break();
    }

    /// <summary>
    /// Dash over: the segment drops to the ground. It grips (no sliding) and may only lean forwards / backwards along
    /// the dash line (e.g. on a slope, nudging its neighbours); it never falls over sideways (see FixedUpdate).
    /// </summary>
    public void EnableGravity()
    {
        if (IsToppled || IsLoose) return;

        rb.isKinematic = false;
        rb.useGravity = true;
        rb.constraints = RigidbodyConstraints.None;
        stillTime = 0f;
        settleFromPos = rb.position;
        settleFromRot = rb.rotation;
    }

    private void FixedUpdate()
    {
        if (rb == null || rb.isKinematic)
            return;

        // Settled (fallen, leaned, neighbours nudged): it becomes fixed, so no piece can shove it any more
        // (a character controller pushes any free body, however heavy). Knocking it over frees it again.
        // Judged by how far it really moved (the velocity of a body resting on the ground is not 0).
        stillTime += Time.fixedDeltaTime;
        if (stillTime >= SettleSeconds)
        {
            float moved = Vector3.Distance(rb.position, settleFromPos);
            float turned = Quaternion.Angle(rb.rotation, settleFromRot);
            if (moved < SettleDistance && turned < SettleAngle)
            {
                rb.isKinematic = true;
                return;
            }
            stillTime = 0f;
            settleFromPos = rb.position;
            settleFromRot = rb.rotation;
        }

        if (IsToppled)
            return; // knocked over: free to fall on its side (it is fixed again once it lies still)

        // Only a lean along the line: turning about the horizontal axis across it. No yaw, no sideways roll.
        Vector3 across = Vector3.Cross(Vector3.up, lineForward).normalized;
        rb.angularVelocity = across * Vector3.Dot(rb.angularVelocity, across);

        Vector3 f = transform.forward - across * Vector3.Dot(transform.forward, across);
        if (f.sqrMagnitude < 1e-6f)
            return;
        f.Normalize();
        Quaternion lean = Quaternion.LookRotation(f, Vector3.Cross(f, across));
        if (Quaternion.Angle(rb.rotation, lean) > 0.01f)
            rb.rotation = lean;
    }

    /// <summary>
    /// Bishop grapple: the wall is pulled over towards <paramref name="pullTowards"/> and lies
    /// flat, opening a line of fire. Tipping happens around its base.
    /// </summary>
    public void Topple(Vector3 pullTowards, float force) => ToppleTowards(pullTowards - transform.position, force);

    /// <summary>A charge (knight's leap, rook's dash) ran into it: it falls over sideways, away along <paramref name="direction"/>.</summary>
    public void KnockOver(Vector3 direction) => ToppleTowards(direction, toppleSpeed);

    /// <summary>
    /// Falls over sideways (the only way it ever does): onto whichever of its two sides faces
    /// <paramref name="direction"/> the most, turning about its base; it is not moved along.
    /// </summary>
    private void ToppleTowards(Vector3 direction, float speed)
    {
        if (IsToppled || !gameObject.activeInHierarchy) return;
        IsToppled = true;

        Vector3 side = transform.right;
        side.y = 0f;
        side = side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.Cross(Vector3.up, lineForward).normalized;
        direction.y = 0f;
        if (Vector3.Dot(side, direction) < 0f)
            side = -side;

        rb.isKinematic = false;
        rb.useGravity = true;
        rb.constraints = RigidbodyConstraints.None;
        stillTime = 0f;
        settleFromPos = rb.position;
        settleFromRot = rb.rotation;

        // Push the top of the wall to that side: it rotates around its base and falls.
        Vector3 top = col.bounds.center + Vector3.up * col.bounds.extents.y;
        rb.AddForceAtPosition(side * speed, top, ForceMode.VelocityChange);
    }

    /// <summary>
    /// Pushed / pulled by a skill (the queen's shockwave, the king's pull): it is not moved along, it falls over
    /// sideways towards <paramref name="impulse"/> (like when a bishop hooks it).
    /// </summary>
    public void Push(Vector3 impulse)
    {
        if (!gameObject.activeInHierarchy || impulse.sqrMagnitude < 1e-6f) return;
        IsLoose = true;
        ToppleTowards(impulse, toppleSpeed);
    }

    /// <summary>Gone (destroyed, time up, replaced by a new dash): it breaks apart like a piece, then back to the pool.</summary>
    public void Break()
    {
        PieceDeathEffect.Shatter(gameObject);
        ReturnToPool();
    }

    public void ReturnToPool()
    {
        if (lifeRoutine != null)
        {
            StopCoroutine(lifeRoutine);
            lifeRoutine = null;
        }

        if (!rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        rb.useGravity = false;
        rb.isKinematic = true;
        rb.constraints = RigidbodyConstraints.FreezeAll;
        IsToppled = false;
        IsLoose = false;

        if (owner != null)
            owner.ReturnToPool(this);
        else
            Destroy(gameObject); // its Rook is gone: nothing will reuse it
    }
}
