using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "Reveal": a piece is marked for one team for a few seconds, visible THROUGH walls.
///  - Logic (for AI later): IsRevealed(target, team) / OnRevealed.
///  - Visual: only for the local player's team; every mesh of the target gets an x-ray
///    copy (ZTest Always, all sub-meshes) drawn on top of everything while the reveal lasts.
///    The target's own materials are never touched.
/// Used by the Pawn: whoever kills one of its clones is revealed to the pawn's side.
/// </summary>
public static class RevealSystem
{
    private static readonly Dictionary<(ChessPieceFPSController, Team), float> revealedUntil =
        new Dictionary<(ChessPieceFPSController, Team), float>();

    /// <summary>Team of the human player (set by the arena). Null = nobody sees reveal visuals.</summary>
    public static Team? LocalViewerTeam { get; set; }

    /// <summary>X-ray material (assign a material made from "FPSChess/XRayReveal"); a fallback is created on demand.</summary>
    public static Material XRayMaterial { get; set; }

    public static event Action<ChessPieceFPSController, Team, float> OnRevealed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        revealedUntil.Clear();
        LocalViewerTeam = null;
        XRayMaterial = null;
        OnRevealed = null;
    }

    public static void Reveal(ChessPieceFPSController target, Team toTeam, float seconds)
    {
        if (target == null || seconds <= 0f)
            return;

        var key = (target, toTeam);
        float until = Time.time + seconds;
        if (!revealedUntil.TryGetValue(key, out float current) || current < until)
            revealedUntil[key] = until;

        OnRevealed?.Invoke(target, toTeam, seconds);

        if (LocalViewerTeam.HasValue && LocalViewerTeam.Value == toTeam)
        {
            var highlight = target.GetComponent<RevealHighlight>();
            if (highlight == null)
                highlight = target.gameObject.AddComponent<RevealHighlight>();
            highlight.Show(toTeam);
        }
    }

    public static bool IsRevealed(ChessPieceFPSController target, Team toTeam) =>
        target != null && revealedUntil.TryGetValue((target, toTeam), out float until) && Time.time < until;

    internal static Material GetXRayMaterial()
    {
        if (XRayMaterial != null)
            return XRayMaterial;

        Shader shader = Shader.Find("FPSChess/XRayReveal");
        if (shader == null)
        {
            Debug.LogWarning("RevealSystem: shader 'FPSChess/XRayReveal' not found; assign PawnSkill.revealMaterial.");
            return null;
        }

        XRayMaterial = new Material(shader) { name = "XRayReveal (runtime)" };
        return XRayMaterial;
    }
}

/// <summary>
/// Shows a revealed piece through walls: each of its meshes gets an x-ray copy (one x-ray
/// material per sub-mesh, so the whole model is covered). The piece's own renderers and
/// materials are left untouched; the copies are destroyed when the reveal ends.
/// </summary>
public sealed class RevealHighlight : MonoBehaviour
{
    public const string OverlayName = "XRayOverlay";

    private readonly List<GameObject> overlays = new List<GameObject>();
    private Team viewer;
    private bool shown;

    public bool IsShown => shown;

    public void Show(Team viewerTeam)
    {
        viewer = viewerTeam;
        enabled = true;
        if (shown)
            return;

        Material xray = RevealSystem.GetXRayMaterial();
        if (xray == null)
            return;

        foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>())
        {
            if (r == null || r.gameObject.name == OverlayName) continue;
            MeshFilter filter = r.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) continue;

            GameObject overlay = NewOverlay(r.transform);
            overlay.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            SetupOverlayRenderer(overlay.AddComponent<MeshRenderer>(), xray, filter.sharedMesh.subMeshCount);
        }

        foreach (SkinnedMeshRenderer r in GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (r == null || r.gameObject.name == OverlayName || r.sharedMesh == null) continue;

            GameObject overlay = NewOverlay(r.transform);
            var copy = overlay.AddComponent<SkinnedMeshRenderer>();
            copy.sharedMesh = r.sharedMesh;
            copy.bones = r.bones;
            copy.rootBone = r.rootBone;
            SetupOverlayRenderer(copy, xray, r.sharedMesh.subMeshCount);
        }

        shown = true;
    }

    private GameObject NewOverlay(Transform parent)
    {
        var overlay = new GameObject(OverlayName);
        overlay.transform.SetParent(parent, false);
        overlays.Add(overlay);
        return overlay;
    }

    private static void SetupOverlayRenderer(Renderer r, Material xray, int subMeshCount)
    {
        var mats = new Material[Mathf.Max(1, subMeshCount)];
        for (int i = 0; i < mats.Length; i++)
            mats[i] = xray; // one per sub-mesh: the whole model is drawn through walls
        r.sharedMaterials = mats;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    private void Update()
    {
        if (shown && !RevealSystem.IsRevealed(GetComponent<ChessPieceFPSController>(), viewer))
            Hide();
    }

    private void OnDisable() => Hide();

    private void Hide()
    {
        if (!shown)
            return;

        foreach (GameObject overlay in overlays)
            if (overlay != null)
                Destroy(overlay);
        overlays.Clear();

        shown = false;
        enabled = false;
    }
}
