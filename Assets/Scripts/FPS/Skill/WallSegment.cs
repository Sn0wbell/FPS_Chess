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

    private float currentHealth;
    private RookSkill owner;
    private Team team;
    private Coroutine lifeRoutine;

    private Rigidbody rb;
    private Collider col;

    public Team Team => team;
    public bool IsToppled { get; private set; }
    /// <summary>Pushed by a skill: a free rigid body (it may rock or tip over).</summary>
    public bool IsLoose { get; private set; }
    public float CurrentHealth => currentHealth;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<Collider>();

        col.isTrigger = false;

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
        currentHealth = maxHealth;
        IsToppled = false;

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
            ReturnToPool();
    }

    private IEnumerator LifeTimer(float t)
    {
        yield return new WaitForSeconds(t);
        ReturnToPool();
    }

    /// <summary>Dash over: the segment drops to the ground and stays upright (rotation locked).</summary>
    public void EnableGravity()
    {
        if (IsToppled || IsLoose) return;

        rb.isKinematic = false;
        rb.useGravity = true;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
    }

    /// <summary>
    /// Bishop grapple: the wall is pulled over towards <paramref name="pullTowards"/> and lies
    /// flat, opening a line of fire. Tipping happens around its base.
    /// </summary>
    public void Topple(Vector3 pullTowards, float force)
    {
        if (IsToppled || !gameObject.activeInHierarchy) return;
        IsToppled = true;

        Vector3 dir = pullTowards - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-4f)
            dir = -transform.forward;
        dir.Normalize();

        rb.isKinematic = false;
        rb.useGravity = true;
        rb.constraints = RigidbodyConstraints.None;

        // Push the top of the wall towards the Bishop: it rotates around its base and falls.
        Vector3 top = col.bounds.center + Vector3.up * col.bounds.extents.y;
        rb.AddForceAtPosition(dir * force, top, ForceMode.VelocityChange);
    }

    /// <summary>
    /// Pushed by a skill (e.g. the queen's shockwave): <paramref name="impulse"/> is momentum (kg m/s),
    /// so this heavy wall moves far less than a piece would. Applied above its centre: near the source it
    /// is thrown, at the edge it only rocks or tips over.
    /// </summary>
    public void Push(Vector3 impulse)
    {
        if (!gameObject.activeInHierarchy || impulse.sqrMagnitude < 1e-6f) return;
        IsLoose = true;

        rb.isKinematic = false;
        rb.useGravity = true;
        rb.constraints = RigidbodyConstraints.None;

        Vector3 above = col.bounds.center + Vector3.up * (col.bounds.extents.y * 0.5f);
        rb.AddForceAtPosition(impulse, above, ForceMode.Impulse);
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
