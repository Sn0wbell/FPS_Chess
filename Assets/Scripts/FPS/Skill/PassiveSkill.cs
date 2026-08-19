using UnityEngine;

public abstract class PassiveSkill : MonoBehaviour
{
    protected SkillContext context;

    public virtual void Initialize(in SkillContext ctx)
    {
        context = ctx;
    }
}
