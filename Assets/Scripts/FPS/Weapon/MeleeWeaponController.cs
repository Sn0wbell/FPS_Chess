using UnityEngine;
using System.Collections.Generic;

public class MeleeWeaponController : WeaponController
{
    public float damage = 35f;
    public float range = 2.2f;
    public float attackRate = 1.2f;
    public float attackAngle = 100f;
    public LayerMask hitMask;

    private float nextAttackTime;
    private readonly HashSet<IDamageable> hitCache = new();

    public override void Tick(float deltaTime) { }

    public override void TryAttack()
    {
        if (isBlocked || firePoint == null || Time.time < nextAttackTime)
            return;

        nextAttackTime = Time.time + 1f / attackRate;
        hitCache.Clear();

        Vector3 origin = firePoint.position;
        var hits = Physics.OverlapSphere(origin, range, hitMask, QueryTriggerInteraction.Ignore);

        foreach (var col in hits)
        {
            if (col.gameObject == gameObject)
                continue;

            Vector3 toTarget = (col.transform.position - origin).normalized;
            if (Vector3.Angle(firePoint.forward, toTarget) > attackAngle * 0.5f)
                continue;

            if (col.TryGetComponent<IDamageable>(out var dmg) && hitCache.Add(dmg))
            {
                dmg.TakeDamage(new DamageInfo
                {
                    amount = damage,
                    hitPoint = col.ClosestPoint(origin),
                    hitDirection = toTarget,
                    source = gameObject,
                    type = DamageType.Melee
                });
            }
        }
    }
}
