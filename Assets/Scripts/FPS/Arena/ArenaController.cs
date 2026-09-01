using UnityEngine;
using System;
using System.Collections.Generic;

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

    [Header("FPS UI")]
    [SerializeField] private FPSCombatUIController fpsUI;

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

    [Header("Weapon")]
    [SerializeField] private GameObject playerWeaponPrefab;

    // =========================
    // RUNTIME TRACKING
    // =========================

    private Dictionary<ChessPieceFPSController, float> outOfBoundsTimers = new Dictionary<ChessPieceFPSController, float>();

    private HashSet<ChessPieceFPSController> aliveBlack = new();
    private HashSet<PawnCloneHealth> activeBlackClones = new();
    private Dictionary<ChessPieceFPSController, List<BaseSkill>> pieceSkills = new();
    private Dictionary<ChessPieceFPSController, SkillContext> cachedContexts = new();

    public Action<ChessPieceFPSController> OnChessPieceDeath;
    public Action<Team> OnMatchEnd;

    private bool matchEnded = false;

    // =========================
    // UNITY CALLBACKS
    // =========================
    void Awake()
    {
        SpawnTeams();
        CacheAllSkills();
        BindPlayerCamera();
        BindFPSUI();
    }
    private void OnDestroy()
    {
        ClearArena();
    }
    void Update()
    {
        if (matchEnded) return;

        TrackSkills();
        CheckForDeadBlack();

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
    private Bounds GetBoundaryBounds()
    {
        Vector3 size = arenaBoundary.localScale;
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

        // White: góc (-X, -Z)
        Bounds whiteBounds = new Bounds(
            new Vector3(
                min.x + arenaBounds.size.x * spawnSpacing,
                arenaBounds.center.y,
                min.z + arenaBounds.size.z * spawnSpacing
            ),
            new Vector3(
                arenaBounds.size.x * 0.5f,
                arenaBounds.size.y,
                arenaBounds.size.z * 0.5f
            )
        );

        // Black: góc (+X, +Z)
        Bounds blackBounds = new Bounds(
            new Vector3(
                max.x - arenaBounds.size.x * spawnSpacing,
                arenaBounds.center.y,
                max.z - arenaBounds.size.z * spawnSpacing
            ),
            new Vector3(
                arenaBounds.size.x * 0.5f,
                arenaBounds.size.y,
                arenaBounds.size.z * 0.5f
            )
        );

        // ---------- White (Player) ----------
        if (whitePrefab.prefab)
        {
            Vector3 spawnPos = FindValidSpawnPositionInBounds(whiteBounds);
            GameObject go = Instantiate(whitePrefab.prefab, spawnPos, Quaternion.identity);

            go.transform.localScale = Vector3.one;

            go.transform.SetParent(transform, true);

            if (!go.TryGetComponent(out playerPiece))
                playerPiece = go.AddComponent<ChessPieceFPSController>();

            if (playerWeaponPrefab != null)
            {
                playerPiece.BindWeapon(playerWeaponPrefab, pointCenterScope, pointTopScope, pointBottomScope, pointLeftScope, pointRightScope);


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

            Vector3 spawnPos = hit.point + Vector3.up * 1.2f;

            if (Physics.CheckSphere(
                    spawnPos,
                    pieceRadius,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                continue;
            }

            return spawnPos;
        }

        Debug.LogWarning("[ArenaController] Spawn fallback used");
        return bounds.center + Vector3.up * 2f;
    }


    // =========================
    // CAMERA
    // =========================
    private void BindPlayerCamera()
    {
        if (!playerPiece || !playerPiece.cameraPoint) return;

        Camera mainCam = Camera.main;
        if (!mainCam) return;

        mainCam.transform.SetParent(playerPiece.cameraPoint);
        mainCam.transform.localPosition = Vector3.zero;
        mainCam.transform.localRotation = Quaternion.identity;

        playerPiece.EnableControl();
    }

    // =========================
    // SKILL MANAGEMENT
    // =========================
    private void CacheAllSkills()
    {
        pieceSkills.Clear();
        cachedContexts.Clear();

        if (playerPiece != null)
            CacheSkillContext(playerPiece);

        foreach (var piece in aliveBlack)
            CacheSkillContext(piece);
    }
    private void CacheSkillContext(ChessPieceFPSController piece)
    {
        if (!piece || cachedContexts.ContainsKey(piece)) return;

        if (!piece.TryGetComponent<CharacterController>(out var cc))
            cc = piece.gameObject.AddComponent<CharacterController>();

        SkillContext ctx = new SkillContext
        {
            owner = piece.gameObject,
            transform = piece.transform,
            fps = piece,
            controller = cc,
            deltaTime = Time.deltaTime
        };

        cachedContexts[piece] = ctx;

        // Initialize all skills
        if (!pieceSkills.TryGetValue(piece, out var skills))
            skills = new List<BaseSkill>(piece.GetComponents<BaseSkill>());

        foreach (var skill in skills)
            skill.Initialize(ctx);

        pieceSkills[piece] = skills;
    }

    private void TrackSkills()
    {
        foreach (var kv in pieceSkills)
        {
            var piece = kv.Key;
            if (!piece) continue;

            if (!cachedContexts.TryGetValue(piece, out var ctx)) continue;

            // chỉ cập nhật deltaTime mỗi frame
            ctx.deltaTime = Time.deltaTime;

            foreach (var skill in kv.Value)
            {
                if (skill == null) continue;

                // phòng ngừa NullReference
                if (ctx.controller == null || ctx.fps == null || ctx.transform == null) continue;

                skill.Tick(ctx);
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
            if (!piece || !piece.gameObject.activeInHierarchy)
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
        OnMatchEnd?.Invoke(winner);
    }

    // =========================
    // PUBLIC API
    // =========================
    public IReadOnlyCollection<ChessPieceFPSController> GetAliveBlack() => aliveBlack;

    public void ClearArena()
    {
        matchEnded = false;

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
        cachedContexts.Clear();
        pieceSkills.Clear();
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
