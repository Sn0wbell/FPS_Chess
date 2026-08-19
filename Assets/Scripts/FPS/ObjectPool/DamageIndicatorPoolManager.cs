using UnityEngine;
using System.Collections.Generic;

public class DamageIndicatorPoolManager : MonoBehaviour
{
    [Header("Indicator Prefab")]
    public DamageIndicatorUI indicatorPrefab; // assign the red arrow prefab
    public int poolSize = 10;

    private List<DamageIndicatorUI> indicators = new();
    private Transform player;
    private Camera playerCamera;

    private void Awake()
    {
        // Pre-instantiate indicators for performance
        for (int i = 0; i < poolSize; i++)
        {
            DamageIndicatorUI newIndicator = Instantiate(indicatorPrefab, transform);
            newIndicator.gameObject.SetActive(false);
            indicators.Add(newIndicator);
        }
    }

    public void Initialize(Transform playerTransform, Camera cam)
    {
        player = playerTransform;
        playerCamera = cam;

        foreach (var indicator in indicators)
            indicator.Initialize(player, playerCamera);
    }

    public void ShowHit(Vector3 hitSource, float damageAmount)
    {
        DamageIndicatorUI indicator = indicators.Find(i => !i.gameObject.activeSelf);

        if (indicator == null)
            indicator = indicators[0]; // fallback

        indicator.gameObject.SetActive(true);
        indicator.ShowHitFrom(hitSource, damageAmount);
    }
}
