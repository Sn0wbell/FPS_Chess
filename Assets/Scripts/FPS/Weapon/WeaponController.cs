using UnityEngine;

public enum WeaponType
{
    Firearm,
    Melee,
    Explosive
}

public abstract class WeaponController : MonoBehaviour
{
    [Header("Weapon Settings")]
    public string weaponName;
    public WeaponType weaponType;

    [Header("HUD Display")]
    public GameObject weaponModelPrefab;
    public float weaponDisplayScale;

    protected Transform firePoint;
    protected bool isBlocked;

    public virtual void BindFirePoint(Transform point)
    {
        firePoint = point;
    }

    public virtual void SetBlocked(bool blocked)
    {
        isBlocked = blocked;
    }
    public virtual bool GetBlocked()
    {
        return isBlocked;
    }
    public abstract void Tick(float deltaTime);
    public abstract void TryAttack();
}
