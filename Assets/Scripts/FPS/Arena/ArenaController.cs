using UnityEngine;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine.UI;

public enum Team
{
    White,
    Black
}

[Serializable]
public struct ChessPieceSpawn
{
    public GameObject prefab;
    public int count;
    [Tooltip("This piece's own weapon (e.g. the king's sword). Empty: the piece gets no weapon.")]
    public GameObject weapon;
}

public class ArenaController : MonoBehaviour
{
    [HideInInspector] public ChessPieceFPSController playerPiece;

    [Header("Team Setup")]
    [SerializeField] private ChessPieceSpawn whitePrefab;
    [SerializeField] private ChessPieceSpawn[] blackPrefabs;

    [Header("Spawn Settings")]
    [SerializeField] private float spawnSpacing = 1.5f;
    [SerializeField] private bool showGizmos = true;

    [Header("Spawn Validation")]
    [SerializeField] private float pieceRadius = 0.35f;
    [SerializeField] private int maxSpawnAttempts = 30;

    [Header("Arena Boundary")]
    public Transform arenaBoundary;
    [SerializeField] private float outOfBoundsKillDelay = 5f;

    [Header("Death")]
    [Tooltip("Every piece that dies breaks into fragments (like a captured chess piece).")]
    [SerializeField] private PieceDeathEffect.Settings deathEffect = PieceDeathEffect.Default;
    [Tooltip("Death cam: the player's camera moves this far behind the body...")]
    [SerializeField] private float deathCamBack = 4f;
    [Tooltip("...this high above it...")]
    [SerializeField] private float deathCamUp = 2f;
    [Tooltip("...in this many seconds, looking at it.")]
    [SerializeField] private float deathCamMoveTime = 0.6f;

    [Header("Match Result")]
    [Tooltip("Seconds between the end of the match and the result on screen (time to watch the last piece break apart).")]
    [SerializeField] private float resultDelay = 1f;
    [Tooltip("The player's side in the chess move that started this duel (the chess <-> FPS bridge will set it).")]
    [SerializeField] private ArenaRole playerRole = ArenaRole.Attacker;

    [Tooltip("The result on the HUD (HUD/MatchResult).")]
    [SerializeField] private ArenaResultOverlay resultOverlay;

    public ArenaRole PlayerRole { get => playerRole; set => playerRole = value; }
    public bool MatchEnded => matchEnded;

    public PieceDeathEffect.Settings DeathEffect => deathEffect;
    public float DeathCamBack => deathCamBack;
    public float DeathCamUp => deathCamUp;
    public float DeathCamMoveTime => deathCamMoveTime;

    [Header("FPS UI")]
    [SerializeField] private FPSCombatUIController fpsUI;

    [Header("Skill Cooldown UI")]
    [SerializeField] private Image skillProgressRing;
    [SerializeField] private TextMeshProUGUI skillCountdownText;
    [SerializeField] private TextMeshProUGUI skillStatusText;

    [SerializeField]
    private Color skillCooldownColor =
        new Color(1f, 0.65f, 0.15f, 1f);

    [SerializeField]
    private Color skillReadyColor =
        new Color(0.2f, 1f, 0.5f, 1f);

    [SerializeField]
    private Color skillActiveColor =
        new Color(0.2f, 0.2f, 1f, 1f);

    [Header("Weapon HUD")]
    [SerializeField] private WeaponHUD weaponHUD;

    // =========================
    // CROSSHAIR
    // =========================
    [Header("Crosshair")]
    public RectTransform pointCenterScope;
    public RectTransform pointTopScope;
    public RectTransform pointBottomScope;
    public RectTransform pointLeftScope;
    public RectTransform pointRightScope;

    // =========================
    // RUNTIME TRACKING
    // =========================

    private Dictionary<ChessPieceFPSController, float> outOfBoundsTimers = new Dictionary<ChessPieceFPSController, float>();

    private HashSet<ChessPieceFPSController> aliveBlack = new();
    private HashSet<PawnCloneHealth> activeBlackClones = new();

    public Action<ChessPieceFPSController> OnChessPieceDeath;
    public Action<Team> OnMatchEnd;

    private bool matchEnded = false;
    private bool playerSpawned = false;

    /// <summary>The arena currently running (null when none).</summary>
    public static ArenaController Current { get; private set; }

    // =========================
    // UNITY CALLBACKS
    // =========================
    void Awake()
    {
        Current = this;
        SpawnTeams();
        BindPlayerCamera();
        BindFPSUI();
    }
    private void OnDestroy()
    {
        if (Current == this)
        {
            Current = null;
            RevealSystem.LocalViewerTeam = null; // no player view any more
        }
        ClearArena();
    }
    void Update()
    {
        if (matchEnded) return;

        UpdateSkillCooldownUI();
        CheckForDeadBlack();

        // Player (White) dead -> Black wins. Checked first so a simultaneous
        // death never reports a White win for a dead player.
        if (IsPlayerDead())
        {
            EndMatch(Team.Black);
            return;
        }

        if (aliveBlack.Count == 0)
            EndMatch(Team.White);

        CheckArenaBoundary();
    }
    private void BindFPSUI()
    {
        if (fpsUI != null)
            fpsUI.Rebind();
    } 
    private void CheckArenaBoundary()
    {
        if (!arenaBoundary) return;

        Bounds bounds = GetBoundaryBounds();

        // Player
        if (playerPiece)
            ProcessBoundary(playerPiece, bounds);

        // Black pieces
        foreach (var piece in aliveBlack)
            if (piece)
                ProcessBoundary(piece, bounds);
    }
    private void ProcessBoundary(
    ChessPieceFPSController piece,
    Bounds bounds)
    {
        bool inside = bounds.Contains(piece.transform.position);

        if (inside)
        {
            outOfBoundsTimers.Remove(piece);
            return;
        }

        if (!outOfBoundsTimers.ContainsKey(piece))
            outOfBoundsTimers[piece] = 0f;

        outOfBoundsTimers[piece] += Time.deltaTime;

        if (outOfBoundsTimers[piece] >= outOfBoundsKillDelay)
        {
            KillPiece(piece);
            outOfBoundsTimers.Remove(piece);
        }
    }
    private void KillPiece(ChessPieceFPSController piece)
    {
        if (!piece) return;

        // Prefer health system if exists
        var health = piece.GetComponent<ChessPieceHealth>();
        if (health)
        {
            health.ForceKill();
            return;
        }

        // Fallback
        piece.gameObject.SetActive(false);
    }
    /// <summary>
    /// The arena area: the real world size of the object assigned to arenaBoundary
    /// (same box the out-of-bounds rule and the spawns use).
    /// </summary>
    public bool TryGetBounds(out Bounds bounds)
    {
        if (!arenaBoundary)
        {
            bounds = default;
            return false;
        }

        bounds = GetBoundaryBounds();
        return true;
    }

    public static bool ContainsXZ(Bounds b, Vector3 p, float margin = 0f) =>
        p.x >= b.min.x + margin && p.x <= b.max.x - margin && p.z >= b.min.z + margin && p.z <= b.max.z - margin;

    /// <summary>
    /// Where a body thrown with this velocity comes down at its start height (gravity &lt; 0), ground bounces included:
    /// each time it hits the ground at minBounceSpeed or more (up to maxBounces times) it bounces back up at
    /// bounciness x that speed, keeping friction x its speed along the ground.
    /// </summary>
    public static Vector3 PredictLanding(Vector3 start, Vector3 velocity, float gravity, float bounciness = 0f,
        float minBounceSpeed = float.PositiveInfinity, float friction = 1f, int maxBounces = 0)
    {
        float g = Mathf.Max(0.01f, -gravity);
        float vy = Mathf.Max(0f, velocity.y);
        Vector3 along = new Vector3(velocity.x, 0f, velocity.z);
        Vector3 travel = along * (2f * vy / g);
        for (int i = 0; i < maxBounces && bounciness > 0f && vy >= minBounceSpeed; i++)
        {
            vy *= bounciness;
            along *= friction;
            travel += along * (2f * vy / g);
        }
        return start + travel;
    }

    private Bounds GetBoundaryBounds()
    {
        Vector3 s = arenaBoundary.lossyScale; // world size, whatever the parents' scale
        Vector3 size = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        Vector3 center = arenaBoundary.position;
        return new Bounds(center, size);
    }

    // =========================
    // SPAWN
    // =========================
    private void SpawnTeams()
    {
        aliveBlack.Clear();
        activeBlackClones.Clear();

        if (!arenaBoundary)
        {
            Debug.LogError("[ArenaController] arenaBoundary not assigned");
            return;
        }

        Bounds arenaBounds = GetBoundaryBounds();

        Vector3 min = arenaBounds.min;
        Vector3 max = arenaBounds.max;
        Vector3 mid = arenaBounds.center;
        // Each side spawns in its own quarter of the arena, spawnSpacing metres in from the edges.
        float margin = Mathf.Max(0f, spawnSpacing);

        // White: góc (-X, -Z)
        Bounds whiteBounds = new Bounds();
        whiteBounds.SetMinMax(
            new Vector3(min.x + margin, min.y, min.z + margin),
            new Vector3(mid.x - margin, max.y, mid.z - margin));

        // Black: góc (+X, +Z)
        Bounds blackBounds = new Bounds();
        blackBounds.SetMinMax(
            new Vector3(mid.x + margin, min.y, mid.z + margin),
            new Vector3(max.x - margin, max.y, max.z - margin));

        // ---------- White (Player) ----------
        if (whitePrefab.prefab)
        {
            Vector3 spawnPos = FindValidSpawnPositionInBounds(whiteBounds);
            GameObject go = Instantiate(whitePrefab.prefab, spawnPos, Quaternion.identity);

            go.transform.localScale = Vector3.one;

            go.transform.SetParent(transform, true);

            if (!go.TryGetComponent(out playerPiece))
                playerPiece = go.AddComponent<ChessPieceFPSController>();

            playerPiece.SetTeam(Team.White);

            playerSpawned = true;

            GameObject weapon = whitePrefab.weapon;
            if (weapon != null)
            {
                playerPiece.BindWeapon(weapon, pointCenterScope, pointTopScope, pointBottomScope, pointLeftScope, pointRightScope);


                if (weaponHUD != null)
                    weaponHUD.SetWeapon(playerPiece.GetCurrentWeapon());
            }
        }

        // ---------- Black Team ----------
        SpawnBlackTeam(blackPrefabs, blackBounds);
    }
    private void SpawnBlackTeam(ChessPieceSpawn[] prefabs, Bounds blackBounds)
    {
        if (prefabs == null) return;

        int row = 0;
        int col = 0;

        foreach (var spawn in prefabs)
        {
            for (int i = 0; i < spawn.count; i++)
            {
                Vector3 spawnPos = FindValidSpawnPositionInBounds(blackBounds);
                GameObject go = Instantiate(spawn.prefab, spawnPos, Quaternion.identity, transform);

                if (!go.TryGetComponent(out ChessPieceFPSController fps))
                    fps = go.AddComponent<ChessPieceFPSController>();

                fps.DisableControl(); // AI-ready
                fps.SetTeam(Team.Black);
                fps.SetPlayerControlled(false);
                if (spawn.weapon != null)
                    fps.BindWeapon(spawn.weapon, null, null, null, null, null); // its own weapon, used by the AI later
                aliveBlack.Add(fps);

                col++;
                if (col >= 8)
                {
                    col = 0;
                    row++;
                }
            }
        }
    }


    // =========================
    // SPAWN VALIDATION
    // =========================

    private Vector3 FindValidSpawnPositionInBounds(Bounds bounds)
    {
        if (TryFindGround(bounds, out Vector3 spawnPos))
            return spawnPos;

        // No ground in this side's quarter: try anywhere in the arena before giving up.
        if (arenaBoundary && TryFindGround(GetBoundaryBounds(), out Vector3 anywhere))
        {
            Debug.LogWarning("[ArenaController] No free ground in the spawn area; spawned elsewhere in the arena.");
            return anywhere;
        }

        Debug.LogError("[ArenaController] No ground found inside the arena boundary: check arenaBoundary's position and size.");
        return bounds.center + Vector3.up * 2f;
    }

    private bool TryFindGround(Bounds bounds, out Vector3 spawnPos)
    {
        for (int i = 0; i < maxSpawnAttempts; i++)
        {
            float x = UnityEngine.Random.Range(bounds.min.x, bounds.max.x);
            float z = UnityEngine.Random.Range(bounds.min.z, bounds.max.z);

            float rayHeight = bounds.max.y + 5f;
            Vector3 rayOrigin = new Vector3(x, rayHeight, z);

            if (!Physics.Raycast(
                    rayOrigin,
                    Vector3.down,
                    out RaycastHit hit,
                    rayHeight * 2f,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                continue;
            }

            spawnPos = hit.point + Vector3.up * 1.2f;

            if (Physics.CheckSphere(
                    spawnPos,
                    pieceRadius,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                continue;
            }

            return true;
        }

        spawnPos = default;
        return false;
    }

    // =========================
    // CAMERA
    // =========================
    private void BindPlayerCamera()
    {
        if (!playerPiece || !playerPiece.cameraPoint) return;

        Camera mainCam = Camera.main;
        if (!mainCam) return;

        DeathCamera.End(mainCam); // a death cam from the previous match
        mainCam.transform.SetParent(playerPiece.cameraPoint);
        mainCam.transform.localPosition = Vector3.zero;
        mainCam.transform.localRotation = Quaternion.identity;

        playerPiece.SetPlayerControlled(true); // only this piece reads keyboard / mouse
        RevealSystem.LocalViewerTeam = playerPiece.Team; // reveals for this side are drawn on screen
        playerPiece.EnableControl();
    }

    // =========================
    // SKILL MANAGEMENT
    // =========================
    // Skills are owned, initialized and ticked by each piece (ChessPieceFPSController).

    private BaseSkill GetPlayerSkillForUI()
    {
        if (!playerPiece)
            return null;

        List<ActiveSkill> skills = playerPiece.boundSkills;

        if (skills == null || skills.Count == 0)
            return null;

        // Ưu tiên kỹ năng đang hồi chiêu.
        foreach (var skill in skills)
        {
            if (skill != null && skill.State == SkillState.Cooldown)
                return skill;
        }

        // Nếu không có kỹ năng hồi chiêu, lấy kỹ năng đầu tiên.
        return skills[0];
    }

    private void UpdateSkillCooldownUI()
    {
        BaseSkill skill = GetPlayerSkillForUI();

        if (skill == null)
        {
            if (skillCountdownText != null)
                skillCountdownText.text = "";

            if (skillStatusText != null)
                skillStatusText.text = "Skill unavailable";

            if (skillProgressRing != null)
            { 
                skillProgressRing.fillAmount = 0f;
                skillProgressRing.color = Color.gray;
            }

            return;
        }

        bool isReady = skill.State == SkillState.Ready;
        bool isCoolingDown = skill.State == SkillState.Cooldown;

        if (skillProgressRing != null)
        {
            skillProgressRing.enabled = true;

            skillProgressRing.fillAmount = isReady
                ? 1f
                : skill.CooldownProgress;

            skillProgressRing.color = isReady
                ? skillReadyColor
                : skillCooldownColor;

            if (skill.State == SkillState.Active) skillProgressRing.color = skillActiveColor;
        }

        if (skillCountdownText != null)
        {
            skillCountdownText.text = isCoolingDown
                ? $"{skill.CooldownRemaining:0.0}s"
                : "F";

            if (skill.State == SkillState.Active) skillCountdownText.text = "";
        }

        if (skillStatusText != null)
        {
            if (isReady)
            {
                skillStatusText.color = skillReadyColor;
                skillStatusText.text = "Skill ready!";
            }
            else if (isCoolingDown)
            {
                skillStatusText.color = skillCooldownColor;
                skillStatusText.text = "Skill cooldown...";
            }
            else if (skill.State == SkillState.Active)
            {
                skillStatusText.color = skillActiveColor;
                skillStatusText.text = "Skill active...";
            }
            else
            {
                skillStatusText.color = Color.red;
                skillStatusText.text = "Skill unavailable";
            }
        }
    }

    // =========================
    // BLACK TEAM DEATH
    // =========================
    private void CheckForDeadBlack()
    {
        RemoveDead(aliveBlack);
        RemoveDeadClones(activeBlackClones);
    }

    private void RemoveDead(HashSet<ChessPieceFPSController> set)
    {
        var dead = new List<ChessPieceFPSController>();

        foreach (var piece in set)
            if (IsDead(piece))
                dead.Add(piece);

        foreach (var piece in dead)
        {
            set.Remove(piece);
            OnChessPieceDeath?.Invoke(piece);
        }
    }

    private void RemoveDeadClones(HashSet<PawnCloneHealth> clones)
    {
        var dead = new List<PawnCloneHealth>();

        foreach (var clone in clones)
            if (!clone || !clone.gameObject.activeInHierarchy)
                dead.Add(clone);

        foreach (var clone in dead)
            clones.Remove(clone);
    }

    // A piece is dead when it was destroyed, deactivated, or its health reached zero
    // (ChessPieceHealth does not deactivate the GameObject by itself).
    private static bool IsDead(ChessPieceFPSController piece)
    {
        if (!piece || !piece.gameObject.activeInHierarchy)
            return true;

        var health = piece.GetComponent<ChessPieceHealth>();
        return health && !health.IsAlive;
    }

    private bool IsPlayerDead() => playerSpawned && IsDead(playerPiece);

    // =========================
    // CLONE MANAGEMENT
    // =========================
    public void RegisterClone(PawnCloneHealth clone, Team team)
    {
        if (team == Team.Black)
            activeBlackClones.Add(clone);
    }

    public void UnregisterClone(PawnCloneHealth clone, Team team)
    {
        if (team == Team.Black)
            activeBlackClones.Remove(clone);
    }

    // =========================
    // MATCH END
    // =========================
    private void EndMatch(Team winner)
    {
        matchEnded = true;

        // The losers' skill effects still running on their own (a dead queen's grenades / shockwave) stop here:
        // nothing can hurt the winners once the match is decided.
        StopSkillEffects(except: winner);

        // The match is over: the player's piece takes no more input.
        bool playerWon = winner == Team.White; // the player always plays White in the arena
        if (playerPiece)
        {
            playerWon = winner == playerPiece.Team;
            playerPiece.DisableControl();
        }

        // Result on screen a moment later (the last piece breaking apart is seen first).
        if (resultOverlay != null)
            resultOverlay.Show(playerWon, playerRole, resultDelay);
        else
            Debug.LogWarning("[ArenaController] No result UI assigned (Match Result > Result Overlay).");

        OnMatchEnd?.Invoke(winner);
    }

    // =========================
    // PUBLIC API
    // =========================
    public IReadOnlyCollection<ChessPieceFPSController> GetAliveBlack() => aliveBlack;

    /// <summary>Grenades not exploded yet and shockwaves still expanding are removed (all, or all but one side's).</summary>
    private static void StopSkillEffects(Team? except = null)
    {
        foreach (var volley in FindObjectsByType<GrenadeVolley>(FindObjectsSortMode.None))
            if (volley != null && (!except.HasValue || volley.Team != except.Value))
                volley.Defuse();
        foreach (var wave in FindObjectsByType<PushWave>(FindObjectsSortMode.None))
            if (wave != null && (!except.HasValue || wave.OwnerTeam != except.Value))
                wave.Stop();
    }

    public void ClearArena()
    {
        StopSkillEffects();       // nothing left over hits the next arena's pieces
        RevealSystem.ClearAll();
        matchEnded = false;
        if (resultOverlay != null)
            resultOverlay.Hide();
        playerSpawned = false;

        if (playerPiece)
            Destroy(playerPiece.gameObject);

        foreach (var piece in aliveBlack)
            if (piece)
                Destroy(piece.gameObject);

        foreach (var clone in activeBlackClones)
            if (clone)
                Destroy(clone.gameObject);

        aliveBlack.Clear();
        activeBlackClones.Clear();
        outOfBoundsTimers.Clear();
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!showGizmos) return;

        GUIStyle style = new GUIStyle
        {
            normal = { textColor = Color.white },
            fontSize = 14
        };

        if (playerPiece)
        {
            UnityEditor.Handles.Label(
                playerPiece.transform.position + Vector3.up * 1.5f,
                "PLAYER",
                style
            );

            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(playerPiece.transform.position, pieceRadius);
        }

        Gizmos.color = Color.black;
        foreach (var piece in aliveBlack)
            if (piece)
                Gizmos.DrawWireSphere(piece.transform.position, pieceRadius);

        Gizmos.color = Color.red;
        foreach (var clone in activeBlackClones)
            if (clone)
                Gizmos.DrawWireSphere(clone.transform.position, pieceRadius * 0.5f);
    }
#endif
}
