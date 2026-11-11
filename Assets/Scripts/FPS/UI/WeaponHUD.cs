using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class WeaponHUD : MonoBehaviour
{
    [Header("UI Elements")]
    public TextMeshProUGUI ammoText;
    public TextMeshProUGUI fireModeText;
    public TextMeshProUGUI weaponNameText;
    public WeaponDisplay3D weaponDisplay3D;

    [Header("Reload Indicator")]
    public GameObject reloadIndicator;
    public Image reloadProgress;
    public TextMeshProUGUI reloadCountdown;

    private WeaponController weaponController;
    private GunController gunController;

    // What is on screen now: the texts are rebuilt only when these change (no new strings every frame).
    private int shownAmmo = int.MinValue;
    private int shownTotal = int.MinValue;
    private int shownMode = int.MinValue;
    private int shownReloadTenths = int.MinValue;
    private bool showingNoGun;

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
        if (gunController == null)
        {
            ShowNoGun();
            return;
        }

        UpdateAmmoDisplay();
        UpdateFireModeDisplay();
        UpdateReloadIndicator();
    }

    /// <summary>No gun (none, a melee weapon, or gone with its piece): no ammo, fire mode or reload bar left on screen.</summary>
    void ShowNoGun()
    {
        if (!showingNoGun)
        {
            ammoText.text = "";
            fireModeText.text = "";
            if (reloadIndicator != null)
                reloadIndicator.SetActive(false);
            showingNoGun = true;
            ForgetShownValues();
        }
        if (weaponController == null && weaponNameText.text.Length > 0)
            weaponNameText.text = "";
    }

    void ForgetShownValues()
    {
        shownAmmo = int.MinValue;
        shownTotal = int.MinValue;
        shownMode = int.MinValue;
        shownReloadTenths = int.MinValue;
    }

    void UpdateAmmoDisplay()
    {
        if (gunController != null)
        {
            showingNoGun = false;
            int ammo = gunController.GetCurrentAmmo();
            int total = gunController.GetTotalAmmo();
            if (ammo == shownAmmo && total == shownTotal)
                return; // unchanged: no new string
            shownAmmo = ammo;
            shownTotal = total;
            ammoText.text = $"{ammo} / {total}";
            ammoText.color = ammo == 0 ? Color.red : Color.white;
        }
    }

    void UpdateFireModeDisplay()
    {
        if (gunController != null)
        {
            FireMode mode = gunController.GetCurrentFireMode();
            if ((int)mode == shownMode)
                return; // unchanged: no new string
            shownMode = (int)mode;
            string modeName = mode.ToString().ToUpper();
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
            if (weaponDisplay3D != null) weaponDisplay3D.SetWeaponModel(null, 1f); // no weapon: no model left on the HUD
        }

        showingNoGun = false;
        ForgetShownValues(); // the new weapon is shown in full

        if (isActiveAndEnabled)
        {
            SubscribeGun();
        }

        UpdateUI();
    }
    void UpdateReloadIndicator()
    {
        bool showIndicator =
            gunController != null &&
            gunController.IsReloading();

        if (reloadIndicator != null)
            reloadIndicator.SetActive(showIndicator);

        if (!showIndicator)
            return;

        if (reloadProgress != null)
        {
            reloadProgress.fillAmount =
                gunController.GetReloadProgress();
        }

        if (reloadCountdown != null)
        {
            float remaining = gunController.GetReloadRemainingTime();
            int tenths = Mathf.CeilToInt(remaining * 10f);
            if (tenths != shownReloadTenths) // a new string only when the shown tenth changes
            {
                shownReloadTenths = tenths;
                reloadCountdown.text = $"{remaining:0.0}";
            }
        }
    }
}
