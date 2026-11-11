using System.Collections.Generic;
using UnityEngine;
using static Unity.VisualScripting.Member;
using static UnityEngine.UI.Image;

public class Bullet : MonoBehaviour
{
    public GameObject bulletHolePrefab;
    public GameObject bulletFleshPrefab;

    private Rigidbody rb;
    private Vector3 startPosition;
    private Vector3 lastPosition;
    private float distanceTraveled = 0f;

    public float bulletDamageBonus = 0.01f;

    [Header("Impact Effects (pooled)")]
    [Tooltip("Seconds a bullet hole / blood effect stays.")]
    public float impactLifetime = 20f;
    [Tooltip("Most impact effects of one kind shown at once (the oldest is reused when full).")]
    public int impactPoolSize = 128;

    private bool despawned; // back to the pool (a second contact in the same physics step is ignored)

    [Header("Highlight (seen in flight, hidden behind walls)")]
    public bool highlight = true;
    [Tooltip("Shots of the player's own side.")]
    public Color ownSideColor = new Color(1f, 0.92f, 0.55f, 0.85f);
    [Tooltip("Shots of the other side.")]
    public Color otherSideColor = new Color(1f, 0.2f, 0.15f, 0.85f);
    [Tooltip("Width of the tracer where the bullet is / hit (m; thin at the muzzle end).")]
    public float tracerWidth = 0.08f;
    [Tooltip("The tracer is never thinner than this on screen (pixels): far shots stay visible.")]
    public float tracerMinPixels = 1.5f;
    [Tooltip("Length of the tracer streak (m): the same from any point of view.")]
    public float tracerLength = 2f;
    [Tooltip("How fast the tracer runs along the bullet's way (m/s): slower than the bullet so the eye can follow it " +
             "(the hit itself is the bullet's, at once).")]
    public float tracerSpeed = 150f;
    [Tooltip("The tracer reaches where the bullet hit at most this long after it (s): far shots are not shown too late.")]
    public float tracerMaxLag = 0.3f;
    [Tooltip("How long the tracer stays after the bullet hit / ended, fading out (s).")]
    public float tracerFadeSeconds = 0.15f;

    private ShotTracer tracer;
    private Color shotColor;
    public LayerMask weaponLayer;
    private float speed = 0f;
    private float lifeTime = 0f;
    private float baseDamage = 0f;
    private float maxRange = 0f;
    public float whizByRadius = 1.5f; // how close to AI counts as a near miss
    public float whizByCooldown = 0.5f; // prevent spamming same AI too often

    private float lifeTimer = 0f;

    private GameObject source;

    public float Speed { get => speed; set => speed = value; }
    public float LifeTime { get => lifeTime; set => lifeTime = value; }
    public float BaseDamage { get => baseDamage; set => baseDamage = value; }
    public float MaxRange { get => maxRange; set => maxRange = value; }
    public GameObject Source { get => source; set => source = value; }

    private void OnEnable()
    {
        if (rb == null)
            rb = GetComponent<Rigidbody>();

        // set better collision mode for fast bullets
        try
        {
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
        catch { /* safe-fail if using a different rigidbody type */ }

        startPosition = transform.position;
        lastPosition = startPosition;
        lifeTimer = 0f;
        distanceTraveled = 0f;
        despawned = false;
    }

    private void Update()
    {
        Vector3 currentPosition = transform.position;
        distanceTraveled = Vector3.Distance(currentPosition, startPosition);
        lifeTimer += Time.deltaTime;

        if (distanceTraveled >= maxRange || lifeTimer >= lifeTime)
        {
            Despawn();
            return;
        }

        if (tracer != null)
            tracer.Follow(currentPosition);
        lastPosition = currentPosition;
    }

    public void Fire(Vector3 direction)
    {
        if (rb == null)
            rb = GetComponent<Rigidbody>();

        // reset previous velocity
        try
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        catch
        {
            // fallback to regular API if needed
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // ensure continuous detection for fast bullets
        try { rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; } catch { }

        // set velocity
        try
        {
            rb.linearVelocity = direction.normalized * speed;
        }
        catch
        {
            rb.linearVelocity = direction.normalized * speed;
        }

        startPosition = transform.position;
        lastPosition = startPosition;
        lifeTimer = 0f;

        ShowHighlight();
    }

    // =========================
    // HIGHLIGHT
    // =========================
    /// <summary>
    /// The tracer, in the shooter's side colour: a short streak running along the bullet's way, slower than the
    /// bullet so it can be seen, then a moment where it hit (bullets are too fast to be seen themselves).
    /// </summary>
    private void ShowHighlight()
    {
        shotColor = ShotHighlight.ColorFor(Source, ownSideColor, otherSideColor);
        Material lineMat = highlight ? ShotHighlight.MaterialFor(shotColor, vertexAlpha: true) : null;
        EndTracer(transform.position); // (a pooled bullet fired again: its last tracer is left to fade)
        if (lineMat != null)
            tracer = ShotTracer.Begin(transform.position, rb != null ? rb.linearVelocity : transform.forward, lineMat,
                tracerLength, tracerSpeed, tracerMaxLag, tracerWidth, tracerMinPixels, tracerFadeSeconds);
    }

    /// <summary>The bullet hit / ended here: its tracer stays a moment, fading out.</summary>
    private void EndTracer(Vector3 at)
    {
        if (tracer != null)
            tracer.End(at);
        tracer = null;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (despawned || collision.gameObject == gameObject) return; // already hit something this step

        GameObject hitObject = collision.gameObject;

        IDamageable target = hitObject.GetComponentInParent<IDamageable>();

        bool hasContact = collision.contactCount > 0;
        ContactPoint contact = hasContact ? collision.GetContact(0) : default;
        Vector3 hitPoint = hasContact ? contact.point : transform.position;
        Vector3 hitNormal = hasContact ? contact.normal : -transform.forward;

        // A gun set to deal no damage (a pawn decoy's) deals none at all: no speed bonus either.
        if (target != null && baseDamage > 0f)
        {
            // Use local damage value for this hit; do not mutate BaseDamage permanently
            float damage = baseDamage + (rb.linearVelocity.magnitude * bulletDamageBonus);

            target.TakeDamage(new DamageInfo
            {
                amount = damage,
                hitPoint = hitPoint + hitNormal * 0.001f,
                hitDirection = transform.forward,
                source = Source,
                type = DamageType.Bullet
            });
        }

        // A piece (or a decoy) bleeds; anything else gets a bullet hole.
        bool hitAPiece = hitObject.GetComponentInParent<ChessPieceFPSController>() != null;
        SpawnImpact(hitAPiece ? bulletFleshPrefab : bulletHolePrefab, hasContact, hitPoint, hitNormal, collision.transform);
        EndTracer(hitPoint); // the tracer ends right where it hit

        Despawn();
    }
    /// <summary>A bullet hole / blood effect from the pool, stuck to what was hit, shown impactLifetime seconds.</summary>
    void SpawnImpact(GameObject prefab, bool hasContact, Vector3 hitPoint, Vector3 hitNormal, Transform hitTransform)
    {
        if (prefab == null || !hasContact)
            return;

        Vector3 spawnPosition = hitPoint + hitNormal * 0.001f;
        Quaternion spawnRotation = Quaternion.LookRotation(hitNormal);
        ImpactEffectPool.Spawn(prefab, spawnPosition, spawnRotation, hitTransform, impactLifetime, impactPoolSize,
            highlight ? shotColor : (Color?)null); // the hole glows in the shot's colour while it splashes
    }

    private void Despawn()
    {
        if (despawned)
            return; // never back to the pool twice
        despawned = true;
        EndTracer(transform.position);
        BaseDamage = 0f;
        Source = null;

        if (BulletPoolManager.Instance != null)
            BulletPoolManager.Instance.ReturnBullet(gameObject);
        else
            Destroy(gameObject);
    }
}
