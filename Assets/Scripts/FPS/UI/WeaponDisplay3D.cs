using UnityEngine;

public class WeaponDisplay3D : MonoBehaviour
{
    [Header("References")]
    public Transform displayHolder;
    public Camera renderCamera;

    private GameObject currentWeaponModel;

    public void SetWeaponModel(GameObject weaponPrefab, float weaponScale)
    {
        // Clear old model
        if (currentWeaponModel != null)
            Destroy(currentWeaponModel);

        if (weaponPrefab == null) return;

        // Spawn weapon in front of camera
        currentWeaponModel = Instantiate(weaponPrefab, displayHolder);
        currentWeaponModel.layer = LayerMask.NameToLayer("Weapon");

        // Ensure all children use the correct layer
        SetLayerRecursively(currentWeaponModel, LayerMask.NameToLayer("Weapon"));

        // Position it nicely for display
        currentWeaponModel.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.Euler(0, -90, 0));
        currentWeaponModel.transform.localScale = Vector3.one * weaponScale; // tweak for your models
    }

    private void SetLayerRecursively(GameObject obj, int newLayer)
    {
        obj.layer = newLayer;
        foreach (Transform child in obj.transform)
            SetLayerRecursively(child.gameObject, newLayer);
    }
}
