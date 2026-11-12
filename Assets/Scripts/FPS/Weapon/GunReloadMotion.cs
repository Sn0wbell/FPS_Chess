using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>What one reload step does to its part.</summary>
public enum ReloadAction
{
    /// <summary>Moves (and turns) to the target's pose over from..to.</summary>
    MoveTo,
    /// <summary>Back to its rest pose over from..to.</summary>
    Return,
    /// <summary>Hidden at "from".</summary>
    Hide,
    /// <summary>Shown at "from" (at the target's pose, if one is set).</summary>
    Show,
    /// <summary>At "from", a copy of the part is thrown away (e.g. the empty magazine); visual only. The part stays.</summary>
    DropCopy,
    /// <summary>
    /// The gun's bolt: racked over from..to (pulled back, held, let go); if it is locked back (Bolt Lock On Empty),
    /// released at "from" instead (it slams home).
    /// </summary>
    RackBolt,
    /// <summary>The gun's bolt is pulled back over from..to and stays open (rounds loaded through the action).</summary>
    OpenBolt,
    /// <summary>The gun's bolt, held open, is pushed home over from..to.</summary>
    CloseBolt
}

/// <summary>When a reload step plays, by the magazine as the reload starts.</summary>
public enum ReloadCondition
{
    /// <summary>Every reload.</summary>
    Always,
    /// <summary>Only when the magazine was empty (e.g. the empty magazine thrown away, the bolt racked).</summary>
    Empty,
    /// <summary>Only when rounds were left (e.g. the magazine put away; a round is still chambered).</summary>
    NotEmpty
}

public enum ReloadEase
{
    Smooth,
    Linear,
    EaseIn,
    EaseOut
}

/// <summary>One step of a reload's motion: a part of the gun, what it does, and when (shares of the reload time).</summary>
[Serializable]
public class ReloadStep
{
    [Tooltip("Just a name, for you.")]
    public string label;
    [Tooltip("The part that moves: the magazine, a cover, a shell... or the gun itself (its tilt).")]
    public Transform part;
    public ReloadAction action = ReloadAction.MoveTo;
    [Tooltip("When it plays: every reload, only when the magazine was empty, or only when rounds were left.")]
    public ReloadCondition when = ReloadCondition.Always;
    [Tooltip("MoveTo: where it goes. Show: where it appears (empty: where it is). An empty object placed in the gun; " +
             "for the gun itself, a child of the gun showing its tilted pose.")]
    public Transform target;
    [Tooltip("When it starts: share of the reload time 0..1 (of one round's slot for per-round steps).")]
    [Range(0f, 1f)] public float from;
    [Tooltip("When it ends (MoveTo / Return).")]
    [Range(0f, 1f)] public float to = 0.1f;
    public ReloadEase ease = ReloadEase.Smooth;
    [Tooltip("DropCopy: the copy's speed (m/s) along the part's own axes.")]
    public Vector3 dropVelocity = new Vector3(0f, -0.5f, 0f);
    [Tooltip("DropCopy: how long the copy stays (s).")]
    public float dropLifetime = 5f;
    [Tooltip("DropCopy: how fast the copy tumbles (degrees/s).")]
    public float dropSpin = 0f;
}

/// <summary>
/// The gun's reload, shown: a free list of steps moving / hiding / showing / dropping its parts over the reload time,
/// plus per-round steps repeated once for each round loaded (a shotgun loaded one shell at a time). Purely visual: it
/// only reads the gun's reload progress. The gun itself can be a part (its tilt), added on top of its aim / recoil.
/// </summary>
[DisallowMultipleComponent]
public class GunReloadMotion : MonoBehaviour
{
    [Tooltip("Steps over the whole reload (0..1 of the reload time).")]
    [SerializeField] private List<ReloadStep> steps = new List<ReloadStep>();

    [Header("Repeat: one round at a time")]
    [Tooltip("Steps repeated for each round loaded (rounds missing, at most Max Repeats). Their from / to are shares " +
             "of one round's slot.")]
    [SerializeField] private List<ReloadStep> perRoundSteps = new List<ReloadStep>();
    [Tooltip("When the rounds start being loaded (share of the reload time).")]
    [SerializeField, Range(0f, 1f)] private float perRoundFrom = 0.2f;
    [Tooltip("When the last round is in (share of the reload time).")]
    [SerializeField, Range(0f, 1f)] private float perRoundTo = 0.8f;
    [SerializeField, Min(1)] private int maxRepeats = 8;

    private sealed class PartState
    {
        public Transform part;
        public Vector3 restPosition;
        public Quaternion restRotation;
        public Renderer[] renderers;
        public bool[] rendererDefaults;
        public Vector3 position;
        public Quaternion rotation;
        public bool visible;
        public bool hidden; // currently hidden by a step
        public bool prop;   // all its renderers off at rest: a prop (a round in the hand), shown only by Show steps
    }

    private struct Entry
    {
        public ReloadStep step;
        public float from;
        public float to;
    }

    private GunController gun;
    private readonly Dictionary<Transform, PartState> parts = new Dictionary<Transform, PartState>();
    private readonly Dictionary<(Transform, Transform), (Vector3, Quaternion)> childTargets = new Dictionary<(Transform, Transform), (Vector3, Quaternion)>();
    private readonly List<Entry> timeline = new List<Entry>();
    private readonly List<ReloadStep> drops = new List<ReloadStep>();
    private bool playing;
    private float lastProgress;
    private bool initialized;

    public bool HasSteps => (steps != null && steps.Count > 0) || (perRoundSteps != null && perRoundSteps.Count > 0);

    private void Initialize()
    {
        if (initialized)
            return;
        initialized = true;
        gun = GetComponent<GunController>();
        parts.Clear();
        childTargets.Clear();
        Capture(steps);
        Capture(perRoundSteps);
    }

    /// <summary>Rest poses, renderers, and targets placed inside their own part (they move with it: kept as offsets).</summary>
    private void Capture(List<ReloadStep> list)
    {
        if (list == null)
            return;
        foreach (ReloadStep s in list)
        {
            if (s == null || s.part == null)
                continue;
            if (!parts.ContainsKey(s.part))
            {
                Renderer[] renderers = s.part.GetComponentsInChildren<Renderer>(true);
                var defaults = new bool[renderers.Length];
                bool anyOn = false;
                for (int i = 0; i < renderers.Length; i++)
                {
                    defaults[i] = renderers[i].enabled;
                    anyOn |= defaults[i];
                }
                bool prop = renderers.Length > 0 && !anyOn;
                // The gun's rest: its own default pose (not its aim / recoil pose now); in the editor (no Awake): where it is.
                bool isGun = gun != null && s.part == gun.transform && Application.isPlaying;
                parts[s.part] = new PartState
                {
                    part = s.part,
                    restPosition = isGun ? gun.DefaultLocalPosition : s.part.localPosition,
                    restRotation = isGun ? gun.DefaultLocalRotation : s.part.localRotation,
                    renderers = renderers,
                    rendererDefaults = defaults,
                    prop = prop,
                    hidden = prop,
                };
            }
            if (s.target != null && s.target != s.part && s.target.IsChildOf(s.part) && !childTargets.ContainsKey((s.part, s.target)))
            {
                Vector3 local = s.part.InverseTransformPoint(s.target.position);
                Vector3 scale = s.part.localScale;
                childTargets[(s.part, s.target)] = (Vector3.Scale(local, scale), Quaternion.Inverse(s.part.rotation) * s.target.rotation);
            }
        }
    }

    /// <summary>Called by the gun every tick, before it places its model.</summary>
    public void Tick()
    {
        Initialize(); // at the first tick: everything at rest
        if (gun == null || !HasSteps)
            return;
        if (!gun.IsReloading())
        {
            if (playing)
                Stop();
            return;
        }
        if (!playing)
            Begin(Mathf.Min(gun.GetMagazineSize() - gun.GetCurrentAmmo(), gun.GetTotalAmmo()), gun.GetCurrentAmmo() <= 0);

        Evaluate(gun.GetReloadProgress(), Time.time, false);
    }

    private void Begin(int rounds, bool empty)
    {
        playing = true;
        lastProgress = -1f;
        BuildTimeline(rounds, empty);
    }

    private static bool Plays(ReloadStep s, bool empty)
        => s != null && s.part != null &&
           (s.when == ReloadCondition.Always || (s.when == ReloadCondition.Empty) == empty);

    private void BuildTimeline(int rounds, bool empty)
    {
        timeline.Clear();
        if (steps != null)
            foreach (ReloadStep s in steps)
                if (Plays(s, empty))
                    timeline.Add(new Entry { step = s, from = s.from, to = Mathf.Max(s.from, s.to) });

        int n = Mathf.Clamp(rounds, 1, Mathf.Max(1, maxRepeats));
        if (perRoundSteps != null && perRoundSteps.Count > 0)
        {
            float start = Mathf.Min(perRoundFrom, perRoundTo);
            float slot = (Mathf.Max(perRoundFrom, perRoundTo) - start) / n;
            for (int i = 0; i < n; i++)
                foreach (ReloadStep s in perRoundSteps)
                    if (Plays(s, empty))
                        timeline.Add(new Entry
                        {
                            step = s,
                            from = start + slot * (i + s.from),
                            to = start + slot * (i + Mathf.Max(s.from, s.to)),
                        });
        }
        // In time order (a stable sort: steps at the same time keep their list order).
        for (int i = 1; i < timeline.Count; i++)
        {
            Entry e = timeline[i];
            int j = i - 1;
            while (j >= 0 && timeline[j].from > e.from)
            {
                timeline[j + 1] = timeline[j];
                j--;
            }
            timeline[j + 1] = e;
        }
    }

    /// <summary>
    /// Every part's pose / visibility at <paramref name="progress"/> (0..1), replayed from rest. One-off events
    /// (drops, bolt racks) fire when their time is passed; <paramref name="clock"/>: the time now on the bolt's clock.
    /// </summary>
    private void Evaluate(float progress, float clock, bool preview)
    {
        foreach (PartState p in parts.Values)
        {
            p.position = p.restPosition;
            p.rotation = p.restRotation;
            p.visible = !p.prop;
        }
        drops.Clear();

        foreach (Entry e in timeline)
        {
            if (e.from > progress)
                break;
            if (!parts.TryGetValue(e.step.part, out PartState p))
                continue;
            float k = e.to > e.from ? Ease(e.step.ease, Mathf.Clamp01((progress - e.from) / (e.to - e.from))) : 1f;
            switch (e.step.action)
            {
                case ReloadAction.MoveTo:
                    if (e.step.target != null)
                    {
                        TargetPose(p, e.step.target, out Vector3 tp, out Quaternion tr);
                        p.position = Vector3.Lerp(p.position, tp, k);
                        p.rotation = Quaternion.Slerp(p.rotation, tr, k);
                    }
                    break;
                case ReloadAction.Return:
                    p.position = Vector3.Lerp(p.position, p.restPosition, k);
                    p.rotation = Quaternion.Slerp(p.rotation, p.restRotation, k);
                    break;
                case ReloadAction.Hide:
                    p.visible = false;
                    break;
                case ReloadAction.Show:
                    p.visible = true;
                    if (e.step.target != null)
                        TargetPose(p, e.step.target, out p.position, out p.rotation);
                    break;
                case ReloadAction.DropCopy:
                    if (!preview && e.from > lastProgress)
                        drops.Add(e.step); // no copies thrown into the edited scene
                    break;
                case ReloadAction.RackBolt:
                    if (e.from > lastProgress && gun != null)
                    {
                        float reloadTime = Mathf.Max(0f, gun.GetReloadTime());
                        gun.RackBolt(clock - (progress - e.from) * reloadTime, (e.to - e.from) * reloadTime);
                    }
                    break;
                case ReloadAction.OpenBolt:
                case ReloadAction.CloseBolt:
                    if (e.from > lastProgress && gun != null)
                    {
                        float reloadTime = Mathf.Max(0f, gun.GetReloadTime());
                        float start = clock - (progress - e.from) * reloadTime, duration = (e.to - e.from) * reloadTime;
                        if (e.step.action == ReloadAction.OpenBolt)
                            gun.OpenBolt(start, duration);
                        else
                            gun.CloseBolt(start, duration);
                    }
                    break;
            }
        }

        // Drops leave from where the part is now (before this frame's move), then the parts move.
        foreach (ReloadStep d in drops)
            Drop(d);
        foreach (PartState p in parts.Values)
            Apply(p);
        lastProgress = progress;
    }

    /// <summary>The target's pose in the part's parent's space.</summary>
    private void TargetPose(PartState p, Transform target, out Vector3 position, out Quaternion rotation)
    {
        if (childTargets.TryGetValue((p.part, target), out var rel))
        {
            position = p.restPosition + p.restRotation * rel.Item1;
            rotation = p.restRotation * rel.Item2;
            return;
        }
        Transform parent = p.part.parent;
        position = parent != null ? parent.InverseTransformPoint(target.position) : target.position;
        rotation = parent != null ? Quaternion.Inverse(parent.rotation) * target.rotation : target.rotation;
    }

    private void Apply(PartState p)
    {
        if (gun != null && p.part == gun.transform)
            gun.SetReloadPoseOffset(p.position - p.restPosition, Quaternion.Inverse(p.restRotation) * p.rotation); // on top of aim / recoil
        else
            p.part.SetLocalPositionAndRotation(p.position, p.rotation);

        if (p.hidden == !p.visible)
            return;
        p.hidden = !p.visible;
        for (int i = 0; i < p.renderers.Length; i++)
            if (p.renderers[i] != null)
                p.renderers[i].enabled = p.visible && (p.prop || p.rendererDefaults[i]);
    }

    private void Drop(ReloadStep s)
    {
        if (!Application.isPlaying || s.part == null)
            return;
        EjectedShell copy = EjectedShell.Throw(s.part.gameObject, s.part.position, s.part.rotation,
            s.part.TransformDirection(s.dropVelocity), s.dropSpin, s.dropLifetime, s.part.lossyScale);
        if (copy == null)
            return;
        foreach (Renderer r in copy.GetComponentsInChildren<Renderer>(true))
            r.enabled = true; // the copy shows even if the part is hidden at that moment
    }

    /// <summary>Every part back at rest and shown as it was (the reload is over, or was cut short).</summary>
    private void Stop()
    {
        playing = false;
        timeline.Clear();
        foreach (PartState p in parts.Values)
        {
            p.position = p.restPosition;
            p.rotation = p.restRotation;
            p.visible = !p.prop;
            Apply(p);
        }
        if (gun != null)
            gun.SetReloadPoseOffset(Vector3.zero, Quaternion.identity);
    }

    private void OnDisable()
    {
        if (playing)
            Stop();
    }

    private static float Ease(ReloadEase ease, float k)
    {
        switch (ease)
        {
            case ReloadEase.Linear: return k;
            case ReloadEase.EaseIn: return k * k;
            case ReloadEase.EaseOut: return 1f - (1f - k) * (1f - k);
            default: return k * k * (3f - 2f * k);
        }
    }

    /// <summary>Right-click the component: plays the reload of an empty magazine (without Play), all rounds missing.</summary>
    [ContextMenu("Test Reload (Empty)")]
    private void TestReloadEmpty() => TestReload(true);

    /// <summary>Right-click the component: plays the reload of a magazine with rounds left (without Play), half missing.</summary>
    [ContextMenu("Test Reload (Not Empty)")]
    private void TestReloadNotEmpty() => TestReload(false);

    private void TestReload(bool empty)
    {
        if (Application.isPlaying)
        {
            Debug.Log($"{name}: in Play, reload the gun to see it.");
            return;
        }
#if UNITY_EDITOR
        initialized = false;
        Initialize();
        if (gun == null || !HasSteps)
        {
            Debug.LogWarning($"{name}: needs a GunController and some steps.");
            return;
        }
        if (preview != null)
            UnityEditor.EditorApplication.update -= preview;

        Begin(empty ? gun.GetMagazineSize() : Mathf.Max(1, gun.GetMagazineSize() / 2), empty);
        gun.EditorBoltBegin(empty);
        float duration = Mathf.Max(0.01f, gun.GetReloadTime());
        double start = UnityEditor.EditorApplication.timeSinceStartup;
        preview = () =>
        {
            float clock = (float)(UnityEditor.EditorApplication.timeSinceStartup - start);
            float t = this == null ? 1f : clock / duration;
            if (this != null && t < 1f)
            {
                Evaluate(t, clock, true);
                PreviewGunPose();
                if (gun != null)
                    gun.EditorBoltTick(clock);
                UnityEditor.SceneView.RepaintAll();
                return;
            }
            UnityEditor.EditorApplication.update -= preview;
            preview = null;
            if (this != null)
            {
                Stop();
                PreviewGunPose();
                if (gun != null)
                    gun.EditorBoltEnd();
                initialized = false; // captured again at Play
            }
        };
        UnityEditor.EditorApplication.update += preview;
#endif
    }

#if UNITY_EDITOR
    private UnityEditor.EditorApplication.CallbackFunction preview;

    /// <summary>Edit mode: the gun does not tick, so its tilt is shown on its transform directly.</summary>
    private void PreviewGunPose()
    {
        if (gun != null && parts.TryGetValue(gun.transform, out PartState p))
            gun.transform.SetLocalPositionAndRotation(p.position, p.rotation);
    }
#endif
}
