using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "Reveal": a piece is marked for one team for a few seconds, visible THROUGH walls.
///  - Logic (for AI later): IsRevealed(target, team) / OnRevealed.
///  - Visual: only for the local player's team; the target's renderers get an extra
///    x-ray material (ZTest Always) drawn on top of everything while the reveal lasts.
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

/// <summary>Adds the x-ray material to a revealed piece while the reveal lasts, then restores its materials.</summary>
public sealed class RevealHighlight : MonoBehaviour
{
    private Renderer[] renderers = Array.Empty<Renderer>();
    private Material[][] originals = Array.Empty<Material[]>();
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

        renderers = GetComponentsInChildren<Renderer>();
        originals = new Material[renderers.Length][];
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || r is LineRenderer || r is ParticleSystemRenderer) continue;

            Material[] mats = r.sharedMaterials;
            originals[i] = mats;
            var withXRay = new Material[mats.Length + 1];
            Array.Copy(mats, withXRay, mats.Length);
            withXRay[mats.Length] = xray; // extra pass over the last sub-mesh, drawn through walls
            r.sharedMaterials = withXRay;
        }
        shown = true;
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

        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null && originals[i] != null)
                renderers[i].sharedMaterials = originals[i];

        shown = false;
        enabled = false;
    }
}
