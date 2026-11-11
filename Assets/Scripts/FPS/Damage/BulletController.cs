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

        Despawn();
    }
    /// <summary>A bullet hole / blood effect from the pool, stuck to what was hit, shown impactLifetime seconds.</summary>
    void SpawnImpact(GameObject prefab, bool hasContact, Vector3 hitPoint, Vector3 hitNormal, Transform hitTransform)
    {
        if (prefab == null || !hasContact)
            return;

        Vector3 spawnPosition = hitPoint + hitNormal * 0.001f;
        Quaternion spawnRotation = Quaternion.LookRotation(hitNormal);
        ImpactEffectPool.Spawn(prefab, spawnPosition, spawnRotation, hitTransform, impactLifetime, impactPoolSize);
    }

    private void Despawn()
    {
        if (despawned)
            return; // never back to the pool twice
        despawned = true;
        BaseDamage = 0f;
        Source = null;

        if (BulletPoolManager.Instance != null)
            BulletPoolManager.Instance.ReturnBullet(gameObject);
        else
            Destroy(gameObject);
    }
}
