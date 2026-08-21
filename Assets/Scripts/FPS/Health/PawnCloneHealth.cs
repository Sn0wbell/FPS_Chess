using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PawnCloneHealth : MonoBehaviour, IDamageable
{
    private float lifetime;
    private float dissolveTime;
    private float timer;
    private bool dying;

    private GameObject destroyVFX;
    private PawnClonePool pool;

    private CharacterController controller;
    private GunController[] guns;
    private MeleeWeaponController[] melees;

    // Scatter simulation
    private Vector3 scatterVelocity;
    private float gravity = -25f;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        guns = GetComponentsInChildren<GunController>(true);
        melees = GetComponentsInChildren<MeleeWeaponController>(true);

        var marker = GetComponent<PawnCloneMarker>();
        if (!marker)
            marker = gameObject.AddComponent<PawnCloneMarker>();

        marker.IsClone = true;
    }

    public void Activate(
        float life,
        float dissolve,
        GameObject vfx,
        PawnClonePool ownerPool,
        Vector3 initialVelocity)
    {
        CancelInvoke();

        lifetime = Mathf.Max(0.01f, life);
        dissolveTime = dissolve;
        destroyVFX = vfx;
        pool = ownerPool;

        scatterVelocity = initialVelocity;

        timer = 0f;
        dying = false;

        gameObject.SetActive(true);

        EnableNoDamageMode();
    }

    private void Update()
    {
        if (dying)
            return;

        timer += Time.deltaTime;
        if (timer >= lifetime)
        {
            Die();
            return;
        }

        if (controller == null)
            return;

        scatterVelocity.y += gravity * Time.deltaTime;

        Vector3 move = scatterVelocity * Time.deltaTime;
        CollisionFlags flags = controller.Move(move);

        if ((flags & CollisionFlags.Below) != 0 && scatterVelocity.y < 0f)
        {
            scatterVelocity.y = 0f;
            scatterVelocity.x *= 0.15f;
            scatterVelocity.z *= 0.15f;
        }
    }

    public void TakeDamage(DamageInfo info)
    {
        if (dying)
            return;

        Die();
    }

    private void Die()
    {
        if (dying)
            return;

        dying = true;

        if (destroyVFX)
            Instantiate(destroyVFX, transform.position, Quaternion.identity);

        Invoke(nameof(ReturnToPool), dissolveTime);
    }

    private void ReturnToPool()
    {
        CancelInvoke();
        pool.Return(this);
    }

    public void ForceDespawn()
    {
        CancelInvoke();
        dying = false;
        timer = 0f;
        gameObject.SetActive(false);
        pool.Return(this);
    }

    public void CancelAll()
    {
        CancelInvoke();
        dying = false;
        timer = 0f;
        gameObject.SetActive(false);
    }

    private void EnableNoDamageMode()
    {
        foreach (var g in guns)
            g.setDamage(0f);

        foreach (var m in melees)
            m.damage = 0f;
    }
}
