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
        UpdateUI();
    }
    private void SubscribeGun()
    {
        if (gunController != null)
        {
            gunController.onGunStatChange -= UpdateUI;
            gunController.onGunStatChange += UpdateUI;
        }
    }
    void OnEnable()
    {
        SubscribeGun();
    }
    private void UnsubscribeGun()
    {
        if (gunController != null)
        {
            gunController.onGunStatChange -= UpdateUI;
        }
    }
    void OnDisable()
    {
        UnsubscribeGun();
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
        UnsubscribeGun();

        weaponController = newWeapon;
        gunController = weaponController as GunController;

        if (weaponController != null)
        {
            weaponNameText.text = $"{weaponController.weaponName}";

            if (weaponDisplay3D != null) weaponDisplay3D.SetWeaponModel(weaponController.weaponModelPrefab, weaponController.weaponDisplayScale);
        }
        else
        {
            weaponNameText.text = "";
        }

        if (isActiveAndEnabled)
        {
            SubscribeGun();
        }

        UpdateUI();
    }
}
