using UnityEngine;

public enum DamageType
{
    Bullet,
    Melee,
    Explosive
}

public struct DamageInfo
{
    public float amount;
    public Vector3 hitPoint;
    public Vector3 hitDirection;
    public GameObject source;
    public DamageType type;
}

public interface IDamageable
{
    void TakeDamage(DamageInfo info);
}
