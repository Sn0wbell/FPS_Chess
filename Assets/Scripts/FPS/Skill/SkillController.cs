using UnityEngine;
using System.Collections.Generic;

public class SkillController : MonoBehaviour
{
    public List<ActiveSkill> activeSkills = new();
    public List<PassiveSkill> passiveSkills = new();

    private SkillContext context;

    public SkillBlockMask CurrentBlockMask { get; private set; }

    // =========================
    // RUNTIME SKILL REGISTRATION
    // =========================
    public void RegisterSkillRuntime(ActiveSkill skill)
    {
        if (!activeSkills.Contains(skill))
        {
            activeSkills.Add(skill);
        }
    }

    public void Initialize(SkillContext ctx)
    {
        context = ctx;

        foreach (var p in passiveSkills)
            p.Initialize(ctx);

        foreach (var a in activeSkills)
            a.Initialize(ctx);
    }

    void Update()
    {
        context.deltaTime = Time.deltaTime;

        CurrentBlockMask = SkillBlockMask.None;

        // Tick active skills
        foreach (var s in activeSkills)
        {
            s.Tick(context);

            if (s.State == SkillState.Active)
                CurrentBlockMask |= s.blockMask;
        }
    }

    public bool IsBlocked(SkillBlockMask mask)
    {
        return (CurrentBlockMask & mask) != 0;
    }

    public void ForceCancelMovementSkills()
    {
        foreach (var skill in activeSkills)
        {
            if (skill is IMovementOverride)
                skill.ForceCancel();
        }
    }

    public void ForceCancel(System.Predicate<ActiveSkill> predicate)
    {
        foreach (var skill in activeSkills)
        {
            if (predicate(skill))
                skill.ForceCancel();
        }
    }
}
