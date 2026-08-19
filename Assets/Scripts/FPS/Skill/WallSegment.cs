using UnityEngine;
using System.Collections;

[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
public class WallSegment : MonoBehaviour, IDamageable
{
    [Header("Health")]
    [SerializeField] private float maxHealth = 50f;

    private float currentHealth;
    private RookSkill owner;
    private Coroutine lifeRoutine;

    private Rigidbody rb;
    private Collider col;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<Collider>();

        col.isTrigger = false;

        rb.useGravity = false;
        rb.isKinematic = true;
        rb.constraints = RigidbodyConstraints.FreezeAll;
    }

    public void Init(RookSkill skill)
    {
        owner = skill;
    }

    public void Activate(Vector3 position, Quaternion rotation, float lifetime)
    {
        currentHealth = maxHealth;

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

    public void EnableGravity()
    {
        rb.isKinematic = false;
        rb.useGravity = true;

        rb.constraints =
            RigidbodyConstraints.FreezeRotationY |
            RigidbodyConstraints.FreezeRotationZ;
    }
    public void ReturnToPool()
    {
        if (lifeRoutine != null)
        {
            StopCoroutine(lifeRoutine);
            lifeRoutine = null;
        }

        rb.angularVelocity = Vector3.zero;
        rb.useGravity = false;
        rb.isKinematic = true;
        rb.constraints = RigidbodyConstraints.FreezeAll;

        owner?.ReturnToPool(this);
    }
}
