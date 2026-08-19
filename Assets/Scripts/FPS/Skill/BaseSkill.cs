using UnityEngine;

public enum SkillState { Ready, Active, Cooldown, Disabled }
[System.Flags]
public enum SkillBlockMask { None = 0, Attack = 1 << 0, Movement = 1 << 1, Jump = 1 << 2, All = ~0 }

public struct SkillContext
{
    public GameObject owner;
    public Transform transform;
    public ChessPieceFPSController fps;
    public WeaponController weapon;
    public CharacterController controller;
    public bool isCheck;
    public bool isLastStand;
    public float deltaTime;
}

public abstract class BaseSkill : MonoBehaviour
{
    [Header("Base Skill")]
    public float cooldown = 5f;

    protected SkillState state = SkillState.Ready;
    protected float cooldownEndTime;
    protected SkillContext context;

    public SkillState State => state;

    public virtual void Initialize(in SkillContext ctx) { context = ctx; }

    public virtual bool CanActivate() => state == SkillState.Ready && Time.time >= cooldownEndTime;

    public void TryActivate()
    {
        if (!CanActivate()) return;
        OnActivate(context);
    }

    protected abstract void OnActivate(in SkillContext ctx);

    protected virtual void OnCooldownStart() { }

    protected void EnterCooldown()
    {
        state = SkillState.Cooldown;
        cooldownEndTime = Time.time + cooldown;
        OnCooldownStart();
    }

    public virtual void Tick(in SkillContext ctx)
    {
        if (state == SkillState.Cooldown && Time.time >= cooldownEndTime)
            state = SkillState.Ready;
    }
}