using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// DEPRECATED. Skills are now owned, initialized and ticked by their piece
/// (ChessPieceFPSController), exactly once per frame. This component is kept only so
/// existing scenes that reference it still load; it no longer ticks anything
/// (ticking here as well made skills run twice per frame).
/// </summary>
public class SkillController : MonoBehaviour
{
    public List<ActiveSkill> activeSkills = new();
    public List<PassiveSkill> passiveSkills = new();

    public SkillBlockMask CurrentBlockMask { get; private set; }

    // =========================
    // RUNTIME SKILL REGISTRATION
    // =========================
    public void RegisterSkillRuntime(ActiveSkill skill)
    {
        if (skill != null && !activeSkills.Contains(skill))
            activeSkills.Add(skill);
    }

    public void Initialize(SkillContext ctx)
    {
        foreach (var p in passiveSkills)
            if (p != null) p.Initialize(ctx);
    }

    void Update()
    {
        // Informational only: no Tick() here.
        CurrentBlockMask = SkillBlockMask.None;
        foreach (var s in activeSkills)
            if (s != null && s.State == SkillState.Active)
                CurrentBlockMask |= s.blockMask;
    }

    public bool IsBlocked(SkillBlockMask mask) => (CurrentBlockMask & mask) != 0;

    public void ForceCancelMovementSkills()
    {
        foreach (var skill in activeSkills)
            if (skill is IMovementOverride)
                skill.ForceCancel();
    }

    public void ForceCancel(System.Predicate<ActiveSkill> predicate)
    {
        foreach (var skill in activeSkills)
            if (skill != null && predicate(skill))
                skill.ForceCancel();
    }
}
