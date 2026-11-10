using TMPro;
using UnityEngine;

/// <summary>The player's side in the chess move that started this duel (set by the chess &lt;-&gt; FPS bridge later).</summary>
public enum ArenaRole
{
    Attacker,
    Defender
}

/// <summary>
/// End-of-match result on the HUD: a big VICTORY / DEFEAT and, smaller below it, what it means for the
/// chess move (attack / defense successful or failed). Lives on HUD/MatchResult; its "content" child
/// (the two texts) stays hidden until the result is shown, after a short delay, so the last piece
/// breaking apart can be seen first. Layout, font, size and the subtitle colour are set in the scene.
/// </summary>
public class ArenaResultOverlay : MonoBehaviour
{
    [Tooltip("Shown with the result, hidden otherwise (holds the two texts).")]
    [SerializeField] private GameObject content;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private TMP_Text subtitleText;
    [SerializeField] private Color victoryColor = new Color(1f, 0.85f, 0.2f);
    [SerializeField] private Color defeatColor = new Color(1f, 0.25f, 0.2f);

    private string title;
    private string subtitle;
    private float showAt = -1f;

    public bool IsShown => content != null && content.activeSelf;
    public string Title => title;
    public string Subtitle => subtitle;

    public static string TitleFor(bool playerWon) => playerWon ? "VICTORY" : "DEFEAT";

    public static string SubtitleFor(bool playerWon, ArenaRole role)
    {
        if (role == ArenaRole.Attacker)
            return playerWon ? "ATTACK SUCCESSFUL" : "ATTACK FAILED";
        return playerWon ? "DEFENSE SUCCESSFUL" : "DEFENSE FAILED";
    }

    private void Awake()
    {
        if (content != null)
            content.SetActive(false);
    }

    /// <summary>Shows the result after <paramref name="delay"/> seconds.</summary>
    public void Show(bool playerWon, ArenaRole role, float delay)
    {
        title = TitleFor(playerWon);
        subtitle = SubtitleFor(playerWon, role);

        if (titleText != null)
        {
            titleText.text = title;
            titleText.color = playerWon ? victoryColor : defeatColor;
        }
        if (subtitleText != null)
            subtitleText.text = subtitle;

        showAt = Time.time + Mathf.Max(0f, delay);
        if (delay <= 0f)
            Reveal();
    }

    public void Hide()
    {
        showAt = -1f;
        if (content != null)
            content.SetActive(false);
    }

    private void Update()
    {
        if (showAt >= 0f && Time.time >= showAt)
            Reveal();
    }

    private void Reveal()
    {
        showAt = -1f;
        if (content != null)
            content.SetActive(true);
    }
}
