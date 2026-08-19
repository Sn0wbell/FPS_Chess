using System.Collections.Generic;
using UnityEngine;

public class BulletPoolManager : MonoBehaviour
{
    public static BulletPoolManager Instance { get; private set; }

    [Header("Pool Settings")]
    public GameObject bulletPrefab;
    public int initialPoolSize = 100;

    private readonly Queue<GameObject> available = new();
    private readonly HashSet<GameObject> allBullets = new();

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        for (int i = 0; i < initialPoolSize; i++)
            CreateBullet();
    }

    private GameObject CreateBullet()
    {
        var bullet = Instantiate(bulletPrefab, transform);
        bullet.SetActive(false);
        available.Enqueue(bullet);
        allBullets.Add(bullet);
        return bullet;
    }

    public GameObject GetBullet(Vector3 position, Quaternion rotation)
    {
        if (available.Count == 0)
            CreateBullet();

        var bullet = available.Dequeue();
        bullet.transform.SetPositionAndRotation(position, rotation);
        bullet.transform.localScale = Vector3.one;
        bullet.SetActive(true);
        return bullet;
    }

    public void ReturnBullet(GameObject bullet)
    {
        if (bullet == null || Instance == null)
            return;

        if (!allBullets.Contains(bullet))
            return;

        bullet.SetActive(false);
        available.Enqueue(bullet);
    }
}
