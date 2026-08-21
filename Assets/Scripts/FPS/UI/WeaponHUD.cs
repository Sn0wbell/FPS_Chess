using UnityEngine;
using TMPro;

public class WeaponHUD : MonoBehaviour
{
    [Header("UI Elements")]
    public TextMeshProUGUI ammoText;
    public TextMeshProUGUI fireModeText;
    public TextMeshProUGUI weaponNameText;
    public WeaponDisplay3D weaponDisplay3D;

    private WeaponController weaponController;
    private GunController gunController;

    private void Start()
    {
        if (weaponController != null && weaponDisplay3D != null)
            weaponDisplay3D.SetWeaponModel(weaponController.weaponModelPrefab, weaponController.weaponDisplayScale);

        if (weaponController is GunController) gunController = (GunController)weaponController;
        else gunController = null;
    }
    void OnEnable()
    {
        if (gunController != null)
        {
            gunController.onGunStatChange += UpdateUI;
        }    
    }

    void OnDisable()
    {
        if (gunController != null)
        {
            gunController.onGunStatChange -= UpdateUI;
        }
    }

    void UpdateUI()
    {
        UpdateAmmoDisplay();
        UpdateFireModeDisplay();
    }

    void Update()
    {
        if (weaponController == null)
        {
            ammoText.text = "";
            fireModeText.text = "";
            weaponNameText.text = "";
            SetWeapon(null);
            return;
        }

        UpdateAmmoDisplay();
        UpdateFireModeDisplay();
    }

    void UpdateAmmoDisplay()
    {
        if (gunController != null)
        {
            ammoText.text = $"{gunController.GetCurrentAmmo()} / {gunController.GetTotalAmmo()}";
            ammoText.color = gunController.GetCurrentAmmo() == 0 ? Color.red : Color.white;
        }
    }

    void UpdateFireModeDisplay()
    {
        if (gunController != null)
        {
            string modeName = gunController.GetCurrentFireMode().ToString().ToUpper();
            fireModeText.text = $"[ {modeName} ]";
        }
    }

    public void SetWeapon(WeaponController newWeapon)
    {
        weaponController = newWeapon;
        if (weaponController != null)
        {
            weaponNameText.text = $"{weaponController.weaponName}";

            if (weaponDisplay3D != null) weaponDisplay3D.SetWeaponModel(weaponController.weaponModelPrefab, weaponController.weaponDisplayScale);
        }
    }
}
