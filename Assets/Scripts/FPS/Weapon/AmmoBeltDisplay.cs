using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// An ammo belt's rounds, shown by the rounds left in the gun: with fewer left than the belt holds, the rounds at the
/// box's end go first (the belt has run out of its box). During a reload the old belt stays as it was until the new box
/// comes (Refill At), then it is full.
/// The belt is always a chain of links under plain physics: gravity (down the gun's own up / down: turning or moving
/// the gun never swings it), a kick at each shot fired (Fire Kick), links that
/// only hinge about the rounds' axis with a little friction (Bend Stiffness) never past Max Bend, rounds as round
/// colliders (Round Radius) that never overlap. The belt runs on inside the box: only the rounds pulled out of its
/// mouth are out (a new box: a few; pulling the belt's end draws more out).
/// It is worked out in the gun's own space (it goes wherever the gun goes, nothing lags or jerks).
/// Its first rounds are gripped by the Hand (in the feed tray at rest; moved by the reload's steps to flick the belt
/// out / lay the new one in), its box's end stays in the box's mouth while the belt still runs into the box, and it
/// stays out of the box and the gun's body (Box Blocker, Gun Body Blocker). A copy thrown away (a reload's Drop Copy) flies loose in the world.
/// Purely visual.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1000)] // after the gun has placed its model this frame
public class AmmoBeltDisplay : MonoBehaviour
{
    private const float MaxStep = 1f / 120f;
    private const int MaxSubsteps = 6;
    private const float GroundGap = 0.005f;
    private const float MaxSpeed = 6f;         // m/s in the gun's space: a hand's flick at most (no whip-crack blow-ups)
    private const float SlackOutTime = 0.05f;  // s a round takes to slide out when the belt is given its slack after a reload
    private const float PullOutTaut = 1.005f;  // the belt from the hand to the mouth pulled past this share of its length: it slides out

    [Tooltip("The belt's rounds, from the gun's end (fed first) to the box's end.")]
    [SerializeField] private List<GameObject> rounds = new List<GameObject>();
    [Tooltip("Share of the reload when the new box (with a full belt) appears.")]
    [SerializeField, Range(0f, 1f)] private float refillAt = 0.48f;

    [Header("Hand (the belt's gun end)")]
    [Tooltip("Grips the belt's gun end (position and turn): on the feed tray at rest, moved by the reload's steps.")]
    [SerializeField] private Transform hand;
    [Tooltip("Rounds gripped by the hand (held in the feed tray at rest).")]
    [SerializeField, Min(1)] private int heldLinks = 2;
    [Tooltip("Share of the reload when the hand lets go of the old belt (flicked out).")]
    [SerializeField, Range(0f, 1f)] private float letGoAt = 0.24f;
    [Tooltip("Share of the reload when the hand picks up the new belt's end (to lay it on the tray).")]
    [SerializeField, Range(0f, 1f)] private float pickUpAt = 0.56f;
    [Tooltip("Share of the reload when the new belt lies in the tray: from then on its first rounds are held there.")]
    [SerializeField, Range(0f, 1f)] private float trayAt = 0.72f;
    [Tooltip("How long the belt's end takes to come into the hand once picked up (s).")]
    [SerializeField, Min(0f)] private float pickUpBlend = 0.1f;

    [Header("Physics")]
    [Tooltip("Each shot fired: how hard the belt is jerked toward the gun (m/s, most in its middle, none at its held ends).")]
    [SerializeField, Min(0f)] private float fireKick = 0.4f;
    [Tooltip("Each shot fired: the share of Fire Kick thrown up or down at random.")]
    [SerializeField, Range(0f, 1f)] private float fireKickRandom = 0.5f;
    [SerializeField, Min(0f)] private float gravityScale = 1f;
    [Tooltip("Speed lost: how fast the swinging dies down (per second).")]
    [SerializeField, Min(0f)] private float drag = 2f;
    [Tooltip("Constraint passes per step: higher keeps the belt's links tighter.")]
    [SerializeField, Range(1, 32)] private int iterations = 8;
    [Tooltip("Friction in the links' hinges: how much of a hinge's turning it stops (0: free; it never pulls the belt straight).")]
    [SerializeField, Range(0f, 1f)] private float bendStiffness = 0.15f;
    [Tooltip("How far a link hinges from straight at most (degrees).")]
    [SerializeField, Range(0f, 90f)] private float maxBend = 45f;
    [Tooltip("A round's collider: its case's radius across the belt (m). Rounds never come closer than twice it.")]
    [SerializeField, Min(0f)] private float roundRadius = 0.018f;
    [Tooltip("A new belt drawn up to the tray: its slack is all out by this share of the way from Pick Up At to Tray At (1: as it is laid in; lower: sooner).")]
    [SerializeField, Range(0.1f, 1f)] private float slackOutBy = 0.75f;
    [Tooltip("At the start: the belt out of the box, as a share of the way from the tray to the box's mouth (its slack).")]
    [SerializeField, Min(1f)] private float restSlack = 1.4f;
    [Tooltip("Rounds out of a new box's mouth when it comes (the rest are drawn out by pulling the belt's end).")]
    [SerializeField, Min(2)] private int newBoxRoundsOut = 4;
    [Tooltip("The ammo box the rounds never go into: an empty object whose unit cube (scaled) covers the box.")]
    [SerializeField, FormerlySerializedAs("keepOut")] private Transform boxBlocker;
    [Tooltip("The box blocker is round (a drum): a cylinder along its Z, its X / Y scale the diameter, instead of a box.")]
    [SerializeField] private bool roundBoxBlocker;
    [Tooltip("The gun's body (round the feed tray) the rounds never go into: an empty object whose unit cube covers it. " +
             "The rounds lying in the tray may.")]
    [SerializeField, FormerlySerializedAs("keepOutGun")] private Transform gunBodyBlocker;
    [Tooltip("Room kept between a round's middle and a blocker's faces (m): its case's radius keeps it just touching.")]
    [SerializeField, Min(0f), FormerlySerializedAs("keepOutMargin")] private float blockerPadding = 0.018f;

    private GunController gun;
    private bool attached;         // worked out in the belt's own space (else: a thrown copy, in the world)
    private Transform[] links;
    private Vector3[] restLocal;   // rounds' modelled positions, in the belt's space (the starting shape)
    private float[] lengthLocal;   // link i: round i .. i+1
    private Vector3 mouthDirLocal; // the last link's modelled direction, in the belt's space (out of the box)
    private Vector3 axisLocal;     // the rounds' forward, in the belt's space
    private float upSign = 1f;
    private Vector3[] handOffset;  // the gripped rounds, in the hand's space
    private Vector3 handRest;      // the hand's own rest place (its parent's space): in the tray
    private Vector3[] pos, vel, old;
    private Vector3[] gripBefore, gripNow, pickUpOffset;
    private float[] length;
    private float[] bendBefore;   // each hinge's bend at the step's start (its friction works against its change)
    private bool live;
    private float timeLeft;        // time not yet worked out (steps are always Max Step long: the same rest every frame)
    private bool fresh;            // the next start is a new box (only a few rounds out)
    private bool held;
    private int grip;
    private bool intoBox;
    private Vector3 axis;
    private float pickUpTime;
    private float lastProgress = -1f;
    private int shown;
    private int groundMask;
    private float minGap;
    private int total;             // rounds left on the whole belt (in the box too)
    private int outside;           // rounds out of the box's mouth (the rest are in the box: hidden)
    private float feed;            // how far the last round out has come out (belt space; a link's length: at the mouth)
    private int restOut;           // rounds out at rest (the belt's slack, Rest Slack): given back after a reload
    private int lastAmmo = -1;     // the gun's rounds last frame (fewer now, not reloading: shots fired)
    private Vector3[] thrownVel;   // a thrown copy: its rounds' speeds (world) as the belt was let go
    private bool thrownKnown;
    private int kicks;             // shots fired not yet felt by the belt

    public int Count => rounds != null ? rounds.Count : 0;

    private void Awake()
    {
        gun = GetComponentInParent<GunController>(true); // none on a thrown-away copy: it flies loose
        attached = gun != null;
        Setup();
    }

    private void OnEnable()
    {
        live = false; // shown again / thrown again (pooled copy): starts over
        lastAmmo = -1;
    }

    private void LateUpdate()
    {
        Refresh();
        Simulate(Time.deltaTime);
    }

    /// <summary>Shows as many rounds as the gun has left (a full belt once the new box is in during a reload).</summary>
    public void Refresh()
    {
        if (links == null || links.Length != Count)
            Setup(); // the rounds' list changed since
        if (gun == null)
        {
            shown = total = CountActive();
            return;
        }
        int left = gun.GetCurrentAmmo();
        if (!gun.IsReloading() && lastAmmo > left)
            kicks += lastAmmo - left; // shots fired
        lastAmmo = left;
        if (gun.IsReloading() && gun.GetReloadProgress() >= refillAt)
            left = Mathf.Min(gun.GetMagazineSize(), left + gun.GetTotalAmmo());
        total = left;
        Show(Mathf.Min(left, outside)); // the rounds still in the box: hidden
    }

    /// <summary>The first <paramref name="count"/> rounds (from the gun's end) on, the rest off.</summary>
    public void Show(int count)
    {
        if (rounds == null)
            return;
        shown = Mathf.Clamp(count, 0, rounds.Count);
        for (int i = 0; i < rounds.Count; i++)
        {
            GameObject r = rounds[i];
            bool on = i < count;
            if (r != null && r.activeSelf != on)
                r.SetActive(on);
        }
    }

    private int CountActive()
    {
        int n = 0;
        if (rounds != null)
            while (n < rounds.Count && rounds[n] != null && rounds[n].activeSelf)
                n++;
        return n;
    }

    /// <summary>The modelled shape (rest poses now): spacing, the box's mouth, the grip.</summary>
    private void Setup()
    {
        int n = Count;
        links = new Transform[n];
        for (int i = 0; i < n; i++)
            links[i] = rounds[i] != null ? rounds[i].transform : null;
        for (int i = 0; i < n; i++)
            if (links[i] == null)
            {
                links = new Transform[0]; // a gap in the list: shown / hidden only
                break;
            }
        n = links.Length;
        restLocal = new Vector3[n];
        lengthLocal = new float[n];
        length = new float[n];
        bendBefore = new float[n];
        pos = new Vector3[n];
        vel = new Vector3[n];
        old = new Vector3[n];
        outside = n;
        int g = Mathf.Clamp(heldLinks, 1, Mathf.Max(1, n));
        handOffset = new Vector3[g];
        gripBefore = new Vector3[g];
        gripNow = new Vector3[g];
        pickUpOffset = new Vector3[g];
        groundMask = ~LayerMask.GetMask("Bullet", "Weapon", "ChessPieces", "Ignore Raycast");
        if (n == 0)
            return;

        axisLocal = transform.InverseTransformDirection(links[0].forward).normalized;
        for (int i = 0; i < n; i++)
            restLocal[i] = transform.InverseTransformPoint(links[i].position);
        // A belt's links are all alike: the usual one for each (a thrown copy's rounds may not all be where they were).
        var spans = new List<float>();
        for (int i = 0; i + 1 < n; i++)
            spans.Add(Vector3.Distance(restLocal[i], restLocal[i + 1]));
        spans.Sort();
        float link = spans.Count > 0 ? spans[spans.Count / 2] : 0f;
        for (int i = 0; i + 1 < n; i++)
            lengthLocal[i] = link;
        if (n > 1)
        {
            mouthDirLocal = Flat(restLocal[n - 1] - restLocal[n - 2], axisLocal).normalized;
            upSign = Vector3.Dot(links[0].up, links[1].position - links[0].position) >= 0f ? 1f : -1f;
        }
        if (hand != null)
        {
            for (int k = 0; k < g; k++)
                handOffset[k] = hand.InverseTransformPoint(links[k].position);
            handRest = hand.localPosition;
        }
    }

    private static Vector3 Flat(Vector3 v, Vector3 normal) => Vector3.ProjectOnPlane(v, normal);

    // The space the links are worked out in: the belt's own (attached), or the world (a thrown copy).
    private Vector3 ToSim(Vector3 world) => attached ? transform.InverseTransformPoint(world) : world;
    private Vector3 ToWorld(Vector3 p) => attached ? transform.TransformPoint(p) : p;
    private Vector3 ToSimDir(Vector3 worldDir) => attached ? transform.InverseTransformDirection(worldDir) : worldDir;

    /// <summary>One frame of the belt, then the rounds placed on it.</summary>
    public void Simulate(float dt)
    {
        int all = Mathf.Min(total, links != null ? links.Length : 0);
        if (all == 0 || dt <= 0f)
            return;

        bool reloading = attached && gun.IsReloading();
        float progress = reloading ? gun.GetReloadProgress() : 1f;
        bool refilled = reloading && progress >= refillAt && lastProgress < refillAt;
        lastProgress = reloading ? progress : -1f;
        bool hold = attached && hand != null && (!reloading || progress < letGoAt || progress >= pickUpAt);
        if (refilled)
            fresh = true;
        if (!live || refilled)
            Begin(hold);
        int n = Mathf.Min(all, attached ? outside : all); // the rounds out of the box: the ones worked out
        intoBox = attached && total > n; // the belt still runs into the box: its last round out stays in the mouth
        axis = attached ? axisLocal : transform.TransformDirection(axisLocal).normalized;
        float scale = attached ? 1f : Mathf.Abs(transform.lossyScale.x);
        for (int i = 0; i < links.Length; i++)
            length[i] = lengthLocal[i] * scale;
        minGap = 2f * roundRadius * scale;


        bool wasHeld = held;
        held = hold;
        // In the tray (the hand at rest there, or the new belt laid in by Tray At): its first rounds held. Out of it in
        // the hand (flicked out, drawn up): only the belt's first round, where the hand is; the rest hangs as it will.
        int lastGrip = wasHeld ? grip : 0;
        bool inHand = reloading && hand != null && progress < trayAt && (hand.localPosition - handRest).sqrMagnitude > 0.002f * 0.002f;
        grip = held ? Mathf.Min(inHand ? 1 : handOffset.Length, n) : 0;
        if (grip > lastGrip)
        {
            float left = pickUpBlend > 0f ? 1f - Mathf.Clamp01((Time.time - pickUpTime) / pickUpBlend) : 0f;
            for (int k = 0; k < grip; k++)
                pickUpOffset[k] = k < lastGrip ? pickUpOffset[k] * left : pos[k] - ToSim(hand.TransformPoint(handOffset[k]));
            pickUpTime = Time.time;
        }
        float blend = pickUpBlend > 0f ? Mathf.Clamp01((Time.time - pickUpTime) / pickUpBlend) : 1f;
        for (int k = 0; k < grip; k++)
        {
            gripBefore[k] = k < lastGrip ? gripNow[k] : pos[k];
            gripNow[k] = ToSim(hand.TransformPoint(handOffset[k])) + pickUpOffset[k] * (1f - blend);
        }

        // Attached: down the gun's own down, whichever way it is turned (turning it never swings the belt).
        Vector3 gravity = attached
            ? transform.InverseTransformDirection(gun.transform.TransformDirection(Vector3.down)) * (Physics.gravity.magnitude * gravityScale)
            : Physics.gravity * gravityScale;
        if (attached && kicks > 0 && n >= 2)
            Kick(n, -gravity.normalized);
        kicks = 0;

        // Fixed steps (the time left carried over): a belt at rest stays exactly at rest whatever the frame's length.
        timeLeft = Mathf.Min(timeLeft + dt, MaxStep * MaxSubsteps);
        int steps = Mathf.FloorToInt(timeLeft / MaxStep);
        timeLeft -= steps * MaxStep;
        float h = MaxStep;
        float keep = Mathf.Exp(-drag * h);
        float stiffStep = 1f - Mathf.Pow(1f - Mathf.Clamp01(bendStiffness), h * 60f);
        float stiff = 1f - Mathf.Pow(1f - stiffStep, 1f / Mathf.Max(1, iterations)); // per pass
        Vector3 mouth = attached ? restLocal[links.Length - 1] : Vector3.zero;

        for (int s = 0; s < steps; s++)
        {
            for (int i = 0; i < n; i++)
            {
                old[i] = pos[i];
                vel[i] = (vel[i] + Flat(gravity, axis) * h) * keep;
                pos[i] += vel[i] * h;
            }
            for (int i = 1; i + 1 < n; i++)
                bendBefore[i] = Hinge(old, i);
            for (int k = 0; k < grip; k++)
                pos[k] = Vector3.Lerp(gripBefore[k], gripNow[k], (s + 1f) / steps);
            if (intoBox)
                pos[n - 1] = MouthPin(n, mouth);

            for (int it = 0; it < iterations; it++)
            {
                for (int i = 1; i + 1 < n; i++)
                    BendJoint(i, n, stiff);
                if (intoBox && n >= 2)
                    TurnLink(n - 1, n - 2, -mouthDirLocal, n, stiff);
                Flatten(n);
                float bendCos = Mathf.Cos(maxBend * 0.5f * Mathf.Deg2Rad);
                for (int i = 0; i + 2 < n; i++)
                    for (int j = i + 2; j < n; j++)
                        // Two links apart: never past Max Bend (the rounds either side of a hinge held that far apart).
                        // (the box's mouth guides the belt in: its hinge is the lip's, not the limit's)
                        Apart(i, j, j == i + 2 && !(intoBox && j == n - 1) ? Mathf.Max(minGap, (length[i] + length[i + 1]) * bendCos) : minGap, n);
                if (attached)
                    for (int i = 0; i < n; i++)
                        if (!Pinned(i, n))
                        {
                            if (boxBlocker != null)
                                pos[i] = roundBoxBlocker ? KeptOutOfDrum(boxBlocker, pos[i]) : KeptOut(boxBlocker, pos[i], old[i]);
                            if (gunBodyBlocker != null)
                                pos[i] = KeptOut(gunBodyBlocker, pos[i], old[i], i < handOffset.Length); // (only the tray's rounds may lie in it)
                        }
                // Last: the links' spacing, each hinge's limit right behind it (walking out from the held end).
                for (int i = 0; i + 1 < n; i++)
                {
                    Separate(i, i + 1, length[i], n);
                    if (i >= 1)
                        if (!(intoBox && i + 1 == n - 1))
                            Apart(i - 1, i + 1, (length[i - 1] + length[i]) * bendCos, n);
                }
            }

            for (int i = 0; i < n; i++)
            {
                vel[i] = (pos[i] - old[i]) / h;
                if (attached)
                    vel[i] = Vector3.ClampMagnitude(vel[i], MaxSpeed);
                else
                    Ground(i);
            }

            // The hand pulls the belt taut from the box's mouth: it slides out by as much (its own swing never pulls
            // it: the mouth holds it). The last round out comes up from inside the box; once at the mouth, the next
            // one is the last (back in the box by a link). A new belt drawn up to the tray (Pick Up At on) is given
            // its slack as it comes: it slides out evenly, as much out as at the start by Slack Out By of the way to
            // Tray At; after that (or out of a reload) whatever is still short comes out a round at a time.
            bool slackOut = attached && grip > 0 && (!reloading || progress >= pickUpAt) && n < restOut;
            if (intoBox && grip > 0 && n >= 2 && (n < all || feed < lengthLocal[n - 2]))
            {
                float over = Overstretch(n);
                float add = over > 0f ? Mathf.Min(over, length[n - 2] * 0.5f) / scale : 0f;
                if (slackOut)
                {
                    float link = lengthLocal[n - 2];
                    float rate = link / SlackOutTime; // after Tray At: a round per Slack Out Time
                    float by = pickUpAt + (trayAt - pickUpAt) * slackOutBy;
                    if (reloading && progress < by)
                    {
                        float short_ = (restOut - n) * link - feed + link;  // still to come out
                        float until = Mathf.Max((by - progress) * gun.GetReloadTime(), SlackOutTime);
                        rate = short_ / until; // evenly, all out by Slack Out By
                    }
                    add = Mathf.Max(add, rate * h);
                }
                if (add > 0f)
                {
                    feed = Mathf.Min(feed + add, lengthLocal[n - 2]);
                    if (feed >= lengthLocal[n - 2] && n < all)
                    {
                        feed = 0f;
                        n++;
                        outside = n;
                        intoBox = total > n;
                        pos[n - 1] = MouthPin(n, mouth);
                        vel[n - 1] = Vector3.zero;
                    }
                }
            }
        }
        if (attached)
            Show(Mathf.Min(total, outside));
        Place(n);
    }

    /// <summary>
    /// The shots fired since last frame: the belt jerked toward the gun (along itself), a little up or down at random,
    /// most in its middle, none at its held ends.
    /// </summary>
    private void Kick(int n, Vector3 up)
    {
        float strength = fireKick * Mathf.Min(kicks, 3);
        float side = Random.value < 0.5f ? -1f : 1f;
        for (int i = 1; i < n; i++)
        {
            if (Pinned(i, n))
                continue;
            float t = i / (n - 1f);
            float weight = grip > 0 && intoBox ? Mathf.Sin(t * Mathf.PI) : grip > 0 ? t : 1f;
            // Along the belt it is held (its links): the jerk shows across it, one way or the other.
            Vector3 toGun = Flat(pos[i - 1] - pos[i], axis).normalized;
            Vector3 across = Vector3.Cross(axis, toGun) * side;
            vel[i] += (toGun + across + up * Random.Range(-fireKickRandom, fireKickRandom)) * (strength * weight);
        }
    }

    /// <summary>The belt as modelled (attached) or as it is now (a thrown copy: flying with its throw).</summary>
    private void Begin(bool hold)
    {
        live = true;
        bool newBox = fresh && attached && hand != null && links.Length > 1; // (no hand: nothing to draw it out)
        fresh = false;
        bool rest = !newBox && attached && hand != null && links.Length > 2;
        float link = links.Length > 1 ? lengthLocal[0] : 0f;
        float span = links.Length > 1 ? Vector3.Distance(restLocal[0], restLocal[links.Length - 1]) : 0f;
        int atRest = link > 0f ? Mathf.CeilToInt(span * restSlack / link) + 1 : links.Length;
        restOut = Mathf.Clamp(atRest, Mathf.Min(links.Length, handOffset.Length + 2), links.Length);
        int fewest = Mathf.Min(links.Length, handOffset.Length + 2);
        outside = newBox ? Mathf.Clamp(newBoxRoundsOut, fewest, links.Length)
            : rest ? Mathf.Clamp(atRest, fewest, links.Length) : links.Length;
        feed = outside >= 2 ? lengthLocal[outside - 2] : 0f;
        held = hold;
        for (int k = 0; k < pickUpOffset.Length; k++)
            pickUpOffset[k] = Vector3.zero;
        pickUpTime = -1000f;
        Vector3 v = Vector3.zero, turn = Vector3.zero, center = Vector3.zero;
        var shell = attached ? null : GetComponent<EjectedShell>();
        if (shell != null)
        {
            v = shell.Velocity;
            turn = shell.Spin * Mathf.Deg2Rad;
            center = transform.position;
        }
        for (int i = 0; i < links.Length; i++)
        {
            pos[i] = attached ? restLocal[i] : links[i].position;
            vel[i] = attached ? Vector3.zero : v + Vector3.Cross(turn, pos[i] - center);
            if (!attached && thrownKnown && i < thrownVel.Length)
                vel[i] += thrownVel[i]; // as it was moving when let go, the throw on top
        }
        thrownKnown = false;
        if (newBox)
        {
            // A new box: its few rounds out stick straight out of the mouth (then hang).
            Vector3 mouth = restLocal[links.Length - 1];
            for (int i = 0; i < outside; i++)
                pos[i] = mouth - mouthDirLocal * (lengthLocal[0] * (outside - 1 - i));
        }
        else if (rest && outside < links.Length)
        {
            // At the start: just the slack out, from the tray to the box's mouth, bowed the way the belt was modelled
            // (away from the gun and the box, not down into them).
            Vector3 a = restLocal[0], b = restLocal[links.Length - 1];
            Vector3 bow = Flat(restLocal[(links.Length - 1) / 2] - (a + b) * 0.5f, axisLocal);
            if (bow.sqrMagnitude < 1e-8f)
                bow = Flat(transform.InverseTransformDirection(gun.transform.TransformDirection(Vector3.down)), axisLocal);
            Drape(a, b, outside, link, bow.normalized);
        }
        if (hand != null)
            for (int k = 0; k < gripNow.Length && k < links.Length; k++)
            {
                gripNow[k] = ToSim(hand.TransformPoint(handOffset[k]));
                if (hold && !newBox)
                    pos[k] = gripNow[k];
            }
    }

    /// <summary>Where the last round out is held: in the box's mouth, back inside by as much as it is not out yet.</summary>
    private Vector3 MouthPin(int n, Vector3 mouth) => mouth + mouthDirLocal * (lengthLocal[n - 2] - feed);

    /// <summary>
    /// <paramref name="count"/> rounds a link apart from <paramref name="a"/> to <paramref name="b"/>, hanging between
    /// them (a curve sagging toward <paramref name="down"/> long enough for them), into pos.
    /// </summary>
    private void Drape(Vector3 a, Vector3 b, int count, float link, Vector3 down)
    {
        const int Samples = 64;
        float want = link * (count - 1);
        var pts = new Vector3[Samples + 1];
        void Curve(float sag)
        {
            for (int k = 0; k <= Samples; k++)
            {
                float t = k / (float)Samples;
                pts[k] = Vector3.Lerp(a, b, t) + down * (sag * 4f * t * (1f - t));
            }
        }
        float Length()
        {
            float l = 0f;
            for (int k = 0; k < Samples; k++)
                l += Vector3.Distance(pts[k], pts[k + 1]);
            return l;
        }
        float lo = 0f, hi = want;
        for (int it = 0; it < 24; it++)
        {
            float mid = (lo + hi) * 0.5f;
            Curve(mid);
            if (Length() < want) lo = mid; else hi = mid;
        }
        Curve((lo + hi) * 0.5f);
        pos[0] = pts[0];
        int seg = 0;
        float along = 0f;
        for (int i = 1; i < count; i++)
        {
            float target = link * i;
            while (seg < Samples)
            {
                float s = Vector3.Distance(pts[seg], pts[seg + 1]);
                if (along + s >= target)
                {
                    pos[i] = Vector3.Lerp(pts[seg], pts[seg + 1], (target - along) / Mathf.Max(s, 1e-6f));
                    break;
                }
                along += s;
                seg++;
            }
            if (seg >= Samples)
                pos[i] = b;
        }
    }

    /// <summary>How far the belt from the last gripped round to the box's mouth is pulled past its length (m).</summary>
    private float Overstretch(int n)
    {
        float chain = 0f;
        for (int i = grip - 1; i < n - 1; i++)
            chain += length[i];
        return Vector3.Distance(pos[grip - 1], pos[n - 1]) - chain * PullOutTaut;
    }

    /// <summary>With <paramref name="roundsLeft"/> left, the belt still runs into its box (more of them than are out).</summary>
    public bool RunsIntoBox(int roundsLeft) => roundsLeft > outside;

    /// <summary>
    /// This is a thrown copy of <paramref name="source"/>: each round goes on at the speed it had as it was let go
    /// (the throw itself is added at its start). A short belt (all out of its box) thrown: the gun's rounds are gone.
    /// </summary>
    public void ThrownFrom(AmmoBeltDisplay source)
    {
        if (source == null || source.vel == null)
            return;
        gun = null; // loose in the world now
        attached = false;
        Setup(); // its rounds as they are now (a pooled copy too: thrown before, with another shape)
        thrownVel = new Vector3[links.Length];
        for (int i = 0; i < links.Length && i < source.vel.Length; i++)
            thrownVel[i] = source.attached ? source.transform.TransformDirection(source.vel[i]) : source.vel[i];
        thrownKnown = true;
        live = false;
        // A belt all out of its box thrown away: its rounds go with it (the reload then loads a whole box).
        if (source.attached && source.total > 0 && !source.RunsIntoBox(source.total))
        {
            source.gun.DiscardLoadedRounds();
            source.total = 0;
            source.Show(0);
        }
    }

    private bool Pinned(int i, int n) => i < grip || (i == n - 1 && intoBox);

    /// <summary>Every free link onto the belt's plane (across the rounds), through its held end.</summary>
    private void Flatten(int n)
    {
        Vector3 at = intoBox && grip == 0 ? pos[n - 1] : pos[0];
        for (int i = 0; i < n; i++)
            if (!Pinned(i, n))
                pos[i] -= axis * Vector3.Dot(pos[i] - at, axis);
    }

    /// <summary>
    /// The hinge at round <paramref name="i"/>: both links beside it turned about it, back by the hinge's friction
    /// (<paramref name="stiff"/> of this step's turning), keeping the three rounds' middle where it was.
    /// </summary>
    private void BendJoint(int i, int n, float stiff)
    {
        Vector3 u = Flat(pos[i] - pos[i - 1], axis), w = Flat(pos[i + 1] - pos[i], axis);
        if (u.sqrMagnitude < 1e-12f || w.sqrMagnitude < 1e-12f)
            return;
        float e = Vector3.SignedAngle(u, w, axis);
        float fix = Mathf.DeltaAngle(bendBefore[i], e) * stiff; // friction: against this step's turning only
        float wa = Pinned(i - 1, n) ? 0f : 1f, wb = Pinned(i + 1, n) ? 0f : 1f;
        if (wa + wb <= 0f || Mathf.Abs(fix) < 1e-6f)
            return;
        // Turning the link before by +x and the link after by -x each take x off the bend.
        Vector3 a0 = pos[i - 1], c0 = pos[i + 1];
        if (wa > 0f)
            pos[i - 1] = pos[i] - Quaternion.AngleAxis(fix * wa / (wa + wb), axis) * (pos[i] - pos[i - 1]);
        if (wb > 0f)
            pos[i + 1] = pos[i] + Quaternion.AngleAxis(-fix * wb / (wa + wb), axis) * (pos[i + 1] - pos[i]);
        // ...without moving the three rounds' middle (no push out of nowhere), unless one of them is held.
        if (Pinned(i, n) || wa <= 0f || wb <= 0f)
            return;
        Vector3 shift = (pos[i - 1] - a0 + pos[i + 1] - c0) / 3f;
        pos[i - 1] -= shift;
        pos[i] -= shift;
        pos[i + 1] -= shift;
    }

    /// <summary>The bend at round <paramref name="i"/> of the rounds at <paramref name="p"/> (degrees, about the axis).</summary>
    private float Hinge(Vector3[] p, int i)
    {
        Vector3 u = Flat(p[i] - p[i - 1], axis), w = Flat(p[i + 1] - p[i], axis);
        return u.sqrMagnitude < 1e-12f || w.sqrMagnitude < 1e-12f ? 0f : Vector3.SignedAngle(u, w, axis);
    }

    /// <summary>Round <paramref name="free"/> turned about <paramref name="pivot"/> toward <paramref name="dir"/> (from the pivot).</summary>
    private void TurnLink(int pivot, int free, Vector3 dir, int n, float stiff)
    {
        if (Pinned(free, n))
            return;
        Vector3 d = Flat(pos[free] - pos[pivot], axis);
        if (d.sqrMagnitude < 1e-12f || dir.sqrMagnitude < 1e-12f)
            return;
        float e = Vector3.SignedAngle(dir, d, axis);
        float fix = e * stiff; // a lip / a grip only leans the belt its way: it can still hang over it
        pos[free] = pos[pivot] + Quaternion.AngleAxis(-fix, axis) * (pos[free] - pos[pivot]);
    }

    /// <summary>Rounds <paramref name="a"/> and <paramref name="b"/> at <paramref name="d"/> apart.</summary>
    private void Separate(int a, int b, float d, int n)
    {
        Vector3 delta = pos[b] - pos[a];
        float m = delta.magnitude;
        if (m < 1e-6f)
            return;
        float wa = Pinned(a, n) ? 0f : 1f, wb = Pinned(b, n) ? 0f : 1f;
        if (wa + wb <= 0f)
            return;
        Vector3 fix = delta * ((m - d) / (m * (wa + wb)));
        pos[a] += fix * wa;
        pos[b] -= fix * wb;
    }

    /// <summary>Rounds <paramref name="a"/> and <paramref name="b"/> (not side by side) at least <paramref name="d"/> apart: no overlap.</summary>
    private void Apart(int a, int b, float d, int n)
    {
        Vector3 delta = pos[b] - pos[a];
        float m = delta.magnitude;
        if (m >= d || m < 1e-6f)
            return;
        float wa = Pinned(a, n) ? 0f : 1f, wb = Pinned(b, n) ? 0f : 1f;
        if (wa + wb <= 0f)
            return;
        Vector3 fix = delta * ((m - d) / (m * (wa + wb)));
        pos[a] += fix * wa;
        pos[b] -= fix * wb;
    }

    /// <summary>
    /// <paramref name="p"/> pushed out of <paramref name="box"/> (a unit cube) through its side / top / bottom: the one
    /// it came in from (<paramref name="before"/>), else the nearest. <paramref name="leaveInside"/>: a round already in
    /// it before (lying in the gun's feed tray) is left there; only rounds coming in are kept out.
    /// </summary>
    private Vector3 KeptOutOfDrum(Transform drum, Vector3 p)
    {
        // <paramref name="p"/> pushed out of a cylinder along the drum's Z (its unit cylinder: radius 0.5, length 1),
        // straight out from its axis: along the drum's round side.
        Vector3 l = drum.InverseTransformPoint(ToWorld(p));
        Vector3 s = drum.lossyScale;
        s = new Vector3(Mathf.Max(Mathf.Abs(s.x), 1e-5f), Mathf.Max(Mathf.Abs(s.y), 1e-5f), Mathf.Max(Mathf.Abs(s.z), 1e-5f));
        float rx = 0.5f + blockerPadding / s.x, ry = 0.5f + blockerPadding / s.y, hz = 0.5f + blockerPadding / s.z;
        if (Mathf.Abs(l.z) >= hz)
            return p;
        float e = (l.x / rx) * (l.x / rx) + (l.y / ry) * (l.y / ry);
        if (e >= 1f)
            return p;
        if (e < 1e-8f)
            l.y = ry; // on the axis: out the top
        else
        {
            float k = 1f / Mathf.Sqrt(e);
            l.x *= k;
            l.y *= k;
        }
        return ToSim(drum.TransformPoint(l));
    }

    private Vector3 KeptOut(Transform box, Vector3 p, Vector3 before, bool leaveInside = false)
    {
        Vector3 l = box.InverseTransformPoint(ToWorld(p));
        Vector3 s = box.lossyScale;
        s = new Vector3(Mathf.Max(Mathf.Abs(s.x), 1e-5f), Mathf.Max(Mathf.Abs(s.y), 1e-5f), Mathf.Max(Mathf.Abs(s.z), 1e-5f));
        Vector3 half = new Vector3(0.5f + blockerPadding / s.x, 0.5f + blockerPadding / s.y, 0.5f + blockerPadding / s.z);
        if (Mathf.Abs(l.x) >= half.x || Mathf.Abs(l.y) >= half.y || Mathf.Abs(l.z) >= half.z)
            return p;
        Vector3 lb = box.InverseTransformPoint(ToWorld(before));
        bool outX = Mathf.Abs(lb.x) >= half.x, outY = Mathf.Abs(lb.y) >= half.y;
        if (leaveInside && !outX && !outY && Mathf.Abs(lb.z) < half.z)
            return p;
        bool pushX = outX && !outY ? true : outY && !outX ? false : (half.x - Mathf.Abs(l.x)) * s.x <= (half.y - Mathf.Abs(l.y)) * s.y;
        if (pushX)
            l.x = (outX ? lb.x : l.x) >= 0f ? half.x : -half.x;
        else
            l.y = (outY ? lb.y : l.y) >= 0f ? half.y : -half.y;
        return ToSim(box.TransformPoint(l));
    }

    /// <summary>A thrown copy: its links stop on the ground / walls.</summary>
    private void Ground(int i)
    {
        Vector3 step = pos[i] - old[i];
        float d = step.magnitude;
        if (d < 1e-6f || !Physics.Raycast(old[i], step / d, out RaycastHit hit, d, groundMask, QueryTriggerInteraction.Ignore))
            return;
        pos[i] = hit.point + hit.normal * GroundGap;
        vel[i] = Vector3.ProjectOnPlane(vel[i], hit.normal) * 0.5f;
    }

    /// <summary>Each round on its link, nose along the belt's forward, turned with the belt.</summary>
    private void Place(int n)
    {
        Vector3 axisWorld = transform.TransformDirection(axisLocal).normalized;
        for (int i = 0; i < n; i++)
            old[i] = ToWorld(pos[i]);
        for (int i = 0; i < n; i++)
        {
            Vector3 along = Flat(old[Mathf.Min(i + 1, n - 1)] - old[Mathf.Max(i - 1, 0)], axisWorld);
            if (along.sqrMagnitude < 1e-10f)
                along = links[i].up * upSign;
            links[i].SetPositionAndRotation(old[i], Quaternion.LookRotation(axisWorld, along * upSign));
        }
    }
}
