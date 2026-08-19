using UnityEngine;

public abstract class ActiveSkill : BaseSkill
{
    [Header("Block Mask While Active")]
    public SkillBlockMask blockMask = SkillBlockMask.None;

    protected float activeEndTime;
    private bool forceCanceled;

    protected override void OnActivate(in SkillContext ctx)
    {
        forceCanceled = false;
        EnterActive(GetActiveDuration(ctx));
        OnEnterActive(ctx);
    }

    protected void EnterActive(float duration)
    {
        state = SkillState.Active;
        activeEndTime = Time.time + duration;
    }

    public override void Tick(in SkillContext ctx)
    {
        base.Tick(ctx);

        if (state != SkillState.Active) return;
        if (forceCanceled) return;

        TickActive(ctx);

        if (Time.time >= activeEndTime)
        {
            OnExitActive(ctx);
            EnterCooldown();
        }
    }

    public void ForceCancel()
    {
        if (state != SkillState.Active) return;
        forceCanceled = true;
        OnForceCancel(context);
        EnterCooldown();
    }

    protected abstract float GetActiveDuration(in SkillContext ctx);

    protected virtual void OnEnterActive(in SkillContext ctx) { }
    protected virtual void TickActive(in SkillContext ctx) { }
    protected virtual void OnExitActive(in SkillContext ctx) { }

    protected virtual void OnForceCancel(in SkillContext ctx)
    {
        OnExitActive(ctx);
    }
}