using UnityEngine;

public abstract class ActiveSkill : BaseSkill
{
    [Header("Block Mask While Active")]
    [Tooltip("What the owner cannot do while this skill is active (applied by ChessPieceFPSController).")]
    public SkillBlockMask blockMask = SkillBlockMask.None;

    protected float activeEndTime;
    private bool forceCanceled;

    /// <summary>A dead piece cannot use skills.</summary>
    public override bool CanActivate() =>
        base.CanActivate() &&
        (context.fps == null ||
         (!context.fps.IsDead &&
          // a piece being pushed/pulled by another skill cannot start its own leap/dash/grapple
          !(this is IMovementOverride && context.fps.IsUnderForcedMove)));

    /// <summary>
    /// True while the skill is being cancelled because its object is disabled/destroyed:
    /// cleanup must not re-parent or toggle objects in that hierarchy (Unity forbids it).
    /// </summary>
    protected bool IsTearingDown { get; private set; }

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

    /// <summary>Moves the end of the active phase (e.g. once the real duration is known).</summary>
    protected void SetActiveRemaining(float seconds)
    {
        if (state == SkillState.Active)
            activeEndTime = Time.time + Mathf.Max(0f, seconds);
    }

    public override void Tick(in SkillContext ctx)
    {
        base.Tick(ctx);

        if (state != SkillState.Active) return;
        if (forceCanceled) return;

        TickActive(ctx);

        // TickActive may have finished or cancelled the skill itself.
        if (state != SkillState.Active) return;

        if (Time.time >= activeEndTime)
        {
            OnExitActive(ctx);
            EnterCooldown();
        }
    }

    /// <summary>The skill completed its own sequence before the time limit.</summary>
    protected void FinishActive()
    {
        if (state != SkillState.Active) return;
        OnExitActive(context);
        EnterCooldown();
    }

    /// <summary>
    /// The activation could not start at all (no target, no room): back to Ready, no cooldown.
    /// Call only from OnEnterActive before any effect was applied.
    /// </summary>
    protected void AbortActivation()
    {
        if (state != SkillState.Active) return;
        state = SkillState.Ready;
        cooldownEndTime = 0f;
    }

    public void ForceCancel()
    {
        if (state != SkillState.Active) return;
        forceCanceled = true;
        OnForceCancel(context);
        EnterCooldown();
    }

    /// <summary>Destroyed / disabled while active: release every effect on other pieces.</summary>
    protected virtual void OnDisable()
    {
        IsTearingDown = true;
        try { ForceCancel(); }
        finally { IsTearingDown = false; }
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
