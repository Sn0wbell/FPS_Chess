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
        if (collision.gameObject == gameObject) return;

        GameObject hitObject = collision.gameObject;

        IDamageable target = hitObject.GetComponentInParent<IDamageable>();

        float velocityMagnitude;
        try { velocityMagnitude = rb.linearVelocity.magnitude; }
        catch { velocityMagnitude = rb.linearVelocity.magnitude; }

        // Use local damage value for this hit; do not mutate BaseDamage permanently
        float damage = baseDamage + (velocityMagnitude * bulletDamageBonus);

        if (target != null)
        {
            ContactPoint contact = collision.contacts[0];
            Vector3 hitPoint = contact.point;
            Vector3 hitNormal = contact.normal;
            Vector3 hitPosition = hitPoint + hitNormal * 0.001f;

            target.TakeDamage(new DamageInfo
            {
                amount = damage,
                hitPoint = hitPosition,
                hitDirection = transform.forward,
                source = Source,
                type = DamageType.Bullet
            });
        }

        if (hitObject.layer != LayerMask.NameToLayer("Enemy") && hitObject.layer != LayerMask.NameToLayer("Player"))
            SpawnImpactEffects(collision);
        else
            SpawnImpactFleshEffects(collision);

        Despawn();
    }
    void SpawnImpactEffects(Collision collision)
    {
        if (bulletHolePrefab != null && collision.contacts.Length > 0)
        {
            ContactPoint contact = collision.contacts[0];
            Vector3 hitPoint = contact.point;
            Vector3 hitNormal = contact.normal;
            Vector3 spawnPosition = hitPoint + hitNormal * 0.001f;
            Quaternion spawnRotation = Quaternion.LookRotation(hitNormal);

            GameObject hole = Instantiate(bulletHolePrefab, spawnPosition, spawnRotation);
            hole.transform.SetParent(collision.transform);
            Destroy(hole, 300f);
        }
    }
    void SpawnImpactFleshEffects(Collision collision)
    {
        if (bulletFleshPrefab != null && collision.contacts.Length > 0)
        {
            ContactPoint contact = collision.contacts[0];
            Vector3 hitPoint = contact.point;
            Vector3 hitNormal = contact.normal;
            Vector3 spawnPosition = hitPoint + hitNormal * 0.001f;
            Quaternion spawnRotation = Quaternion.LookRotation(hitNormal);

            GameObject hole = Instantiate(bulletFleshPrefab, spawnPosition, spawnRotation);
            hole.transform.SetParent(collision.transform);
            Destroy(hole, 300f);
        }
    }
    private void Despawn()
    {
        BaseDamage = 0f;
        Source = null;

        if (BulletPoolManager.Instance != null)
            BulletPoolManager.Instance.ReturnBullet(gameObject);
        else
            Destroy(gameObject);
    }
}
