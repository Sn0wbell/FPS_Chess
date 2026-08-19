using UnityEngine;
using System.Threading;
using System.Threading.Tasks;

public sealed class MoveAnimationController : MonoBehaviour
{
    [Header("Move Animation")]
    [SerializeField] private float moveDuration = 0.45f;
    [SerializeField] private float liftHeight = 0.35f;
    [SerializeField] private AnimationCurve moveCurve;

    private CancellationTokenSource _cts;
    private int _sessionId;

    private void Awake()
    {
        if (moveCurve == null || moveCurve.length == 0)
            moveCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
    }

    // ======================================================
    // PUBLIC API
    // ======================================================

    public async Task AnimateMove(
        Transform movingPiece,
        Vector3 targetPos,
        Transform capturedPiece = null) // <<< FIX QUAN TRỌNG
    {
        if (!IsValid(movingPiece))
            return;

        CancelRunning();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        int mySession = ++_sessionId;

        Vector3 startPos = movingPiece.position;
        float elapsed = 0f;

        while (elapsed < moveDuration)
        {
            if (token.IsCancellationRequested ||
                mySession != _sessionId ||
                !IsValid(movingPiece))
                return;

            float t = elapsed / moveDuration;
            float eased = moveCurve.Evaluate(t);

            Vector3 pos = Vector3.Lerp(startPos, targetPos, eased);

            float arc = 1f - Mathf.Pow(2f * t - 1f, 2f);
            pos.y += Mathf.Max(0f, arc) * liftHeight;

            movingPiece.position = pos;

            elapsed += Time.unscaledDeltaTime;
            await Task.Yield();
        }

        movingPiece.position = targetPos;
    }

    // ======================================================
    // INTERNAL
    // ======================================================

    private static bool IsValid(Transform t)
    {
        return t != null &&
               t.gameObject != null &&
               t.gameObject.activeInHierarchy;
    }

    private void CancelRunning()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    private void OnDisable()
    {
        CancelRunning();
    }

    private void OnDestroy()
    {
        CancelRunning();
    }
}
