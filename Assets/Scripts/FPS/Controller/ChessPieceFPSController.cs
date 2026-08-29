using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

public enum MovementOverrideSource
{
    None = 0,
    Skill = 1,
    GravityZone = 2
}

[RequireComponent(typeof(CharacterController))]
public class ChessPieceFPSController : MonoBehaviour
{
    // =========================
    // CORE
    // =========================
    private CharacterController controller;
    private bool canMove;
    private bool canAct;

    // =========================
    // CAMERA
    // =========================
    [Header("Camera")]
    public Transform cameraPoint;

    // =========================
    // MOVEMENT
    // =========================
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float jumpHeight = 3.5f;
    [SerializeField] private float gravity = -9.81f;

    private float verticalVelocity;
    private bool isGrounded;
    private bool useGravity = true;

    // =========================
    // SCATTER / MULTI-FRAME IMPULSE
    // =========================
    private Vector3 scatterVelocityOwner;
    private float scatterGravity = -25f;
    private bool scatting = false;

    // =========================
    // MOUSE LOOK
    // =========================
    [Header("Mouse Look")]
    [SerializeField] private float mouseSensitivity = 15f;
    [SerializeField] private float minPitch = -80f;
    [SerializeField] private float maxPitch = 60f;

    private float yaw;
    private float pitch;

    // =========================
    // WEAPON
    // =========================
    [Header("Weapon")]
    [SerializeField] private Transform firePoint;
    [SerializeField] private Transform weaponHolder;
    [SerializeField] private Transform weaponBlockedPosition;
    [SerializeField] private Vector3 weaponHolderOffset = Vector3.zero;
    [SerializeField] private LayerMask weaponBlockLayer;
    [SerializeField] private float weaponAdjustSpeed = 10f;

    private WeaponController currentWeapon;
    private GunController currentGun;
    private GameObject weaponObject;
    private float weaponBlockDelayTime = 0f;
    private float weaponBlockCheckDistance;

    private Vector3 defaultWeaponLocalPos;
    private Quaternion defaultWeaponLocalRot;
    private Vector3 currentWeaponLocalPos;
    private Quaternion currentWeaponLocalRot;

    // =========================
    // MOVEMENT OVERRIDE
    // =========================
    private float moveOverrideMul = 1f;
    private float jumpOverrideMul = 1f;
    private bool hasMoveOverride;

    private readonly Dictionary<MovementOverrideSource, Vector2> overrides
        = new Dictionary<MovementOverrideSource, Vector2>();

    // =========================
    // SKILL
    // =========================
    [HideInInspector]
    public List<ActiveSkill> boundSkills = new List<ActiveSkill>();

    private readonly HashSet<object> movementSuppressors = new();
    private bool IsMovementSuppressed => movementSuppressors.Count > 0;

    private readonly List<IAbortableMovementSkill> abortableMovementSkills = new();

    // =========================
    // UNITY
    // =========================
    void Awake()
    {
        controller = GetComponent<CharacterController>();
        DisableControl();

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Start()
    {
        var skillController = FindAnyObjectByType<SkillController>();
        if (skillController != null)
        {
            ActiveSkill[] skills = GetComponentsInChildren<ActiveSkill>(true);
            foreach (var skill in skills)
            {
                if (skill != null)
                {
                    boundSkills.Add(skill);

                    SkillContext ctx = new SkillContext
                    {
                        fps = this,
                        controller = controller
                    };
                    skill.Initialize(ctx);
                    skillController.RegisterSkillRuntime(skill);

                    if (skill is IAbortableMovementSkill abortable)
                        abortableMovementSkills.Add(abortable);
                }
            }
        }
    }

    void Update()
    {
        if (!canMove && !canAct)
            return;

        if (canMove && !IsMovementSuppressed)
            HandleMovement();

        if (canAct)
        {
            HandleSkillInput();
        }

        HandleMouseLook();
        HandleWeaponBlock();
        UpdateWeaponSystemPosition();

        if (canAct)
        {
            HandleWeaponInput();
        }

        if (currentWeapon != null)
            currentWeapon.Tick(Time.deltaTime);
    }

    // =========================
    // CONTROL API
    // =========================
    public void EnableControl() { canMove = true; canAct = true; }
    public void DisableControl()
    {
        canMove = false;
        canAct = false;
        verticalVelocity = 0f;
    }

    public void DisableAction() { canAct = false; }
    public void EnableAction() { canAct = true; }

    public void SetGravityEnabled(bool enabled)
    {
        useGravity = enabled;
        if (!enabled)
            verticalVelocity = 0f;
    }
    public void ForceStopAllMotion()
    {
        verticalVelocity = 0f;
        scatterVelocityOwner = Vector3.zero;
        scatting = false;
    }
    public WeaponController GetCurrentWeapon()
    {
        return currentWeapon;
    }
    public void SuppressMovement(object owner)
    {
        if (owner == null) return;
        movementSuppressors.Add(owner);
        ForceStopAllMotion();
    }

    public void ReleaseMovementSuppression(object owner)
    {
        if (owner == null) return;
        movementSuppressors.Remove(owner);
    }

    public void AbortActiveMovementSkill()
    {
        foreach (var s in abortableMovementSkills)
            s?.AbortMovement();
    }

    public void WarpPosition(Vector3 worldPos)
    {
        controller.enabled = false;
        transform.position = worldPos;
        controller.enabled = true;
    }

    // =========================
    // SKILL INPUT
    // =========================
    public void AddExternalImpulse(Vector3 impulse)
    {
        scatterVelocityOwner = impulse;
        scatting = true;
    }

    public void ApplyExternalVerticalVelocity(float velocity)
    {
        verticalVelocity = velocity;
    }

    void HandleSkillInput()
    {
        if (!canAct || boundSkills.Count == 0) return;

        if (Keyboard.current.fKey.wasPressedThisFrame)
        {
            foreach (var skill in boundSkills)
                skill.TryActivate();
        }
    }

    // =========================
    // WEAPON
    // =========================
    public void UpdateWeaponSystemPosition()
    {
        if (firePoint == null || cameraPoint == null) return;
        if (currentGun != null)
        {
            firePoint.position = cameraPoint.position + cameraPoint.forward * currentGun.GetFirePointDistance();
            firePoint.forward = cameraPoint.forward;
            weaponBlockCheckDistance = currentGun.GetFirePointDistance();
            currentGun.BindFirePoint(firePoint);

            if (weaponBlockDelayTime > 0)
            {
                weaponBlockDelayTime -= Time.deltaTime;
            }
            else if (weaponBlockDelayTime < 0) weaponBlockDelayTime = 0;
            if(weaponBlockDelayTime == 0)
            {
                weaponHolder.position = cameraPoint.position + cameraPoint.forward * weaponHolderOffset.z + cameraPoint.up * weaponHolderOffset.y + cameraPoint.right * weaponHolderOffset.x;
                weaponHolder.forward = cameraPoint.forward;
                defaultWeaponLocalPos = weaponHolder.localPosition;
                defaultWeaponLocalRot = weaponHolder.localRotation;
                currentWeaponLocalPos = defaultWeaponLocalPos;
                currentWeaponLocalRot = defaultWeaponLocalRot;
            }
        }
    }

    public void BindWeapon(GameObject weapon, RectTransform centerScope, RectTransform topScope, RectTransform bottomScope, RectTransform leftScope, RectTransform rightScope)
    {
        if (!weapon) return;

        weaponHolder.forward = cameraPoint.forward;

        weaponObject = Instantiate(weapon, weaponHolder); ;
        weaponObject.SetActive(true);
        weaponObject.GetComponent<Rigidbody>().useGravity = false;
        weaponObject.GetComponent<Rigidbody>().isKinematic = true;
        weaponObject.transform.SetParent(weaponHolder);
        weaponObject.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

        currentWeapon = weaponObject.GetComponent<WeaponController>();
        if (currentWeapon is GunController gun)
        {
            currentGun = gun;

            currentGun.SetCrosshair(centerScope, topScope, bottomScope, leftScope, rightScope);
            currentGun.BindFirePoint(firePoint);
        }

        defaultWeaponLocalPos = weaponHolder.localPosition;
        defaultWeaponLocalRot = weaponHolder.localRotation;

        currentWeaponLocalPos = defaultWeaponLocalPos;
        currentWeaponLocalRot = defaultWeaponLocalRot;
    }

    void HandleWeaponInput()
    {
        if (!canAct || currentWeapon == null) return;
        if (Mouse.current == null) return;

        if (Mouse.current.leftButton.isPressed)
        {
            currentWeapon.TryAttack();
        }

        if (currentGun != null)
        {
            if (Mouse.current.rightButton.isPressed && !currentGun.GetBlocked() && (weaponBlockDelayTime <= 0))
            {
                currentGun.SetAiming(true);
            }
            else
            {
                currentGun.SetAiming(false);
            }

            if (!Mouse.current.leftButton.isPressed)
            {
                if(!currentGun.GetTriggerReleasedSinceLastShot()) currentGun.ResetHorizontalRecoilDirection();
                currentGun.SetTriggerReleasedSinceLastShot(true);
            }

            if (Keyboard.current == null) return;

            if (Keyboard.current.bKey.wasPressedThisFrame)
            {
                currentGun.SwitchFireMode();
                Debug.Log("Switched fire mode to: " + currentGun.GetFirePointDistance());
            }

            if (Keyboard.current.rKey.wasPressedThisFrame)
            {
                currentGun.StartReload();
            }
        }    
    }

    // =========================
    // CAMERA
    // =========================
    void HandleMouseLook()
    {
        if (Mouse.current == null) return;

        Vector2 delta = Mouse.current.delta.ReadValue();
        float mouseX = delta.x * mouseSensitivity * Time.deltaTime;
        float mouseY = delta.y * mouseSensitivity * Time.deltaTime;

        yaw += mouseX;
        pitch -= mouseY;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        Quaternion lookRot = Quaternion.Euler(pitch, yaw, 0f);

        if (currentWeapon is GunController gun)
        {
            Vector2 recoil = gun.GetAppliedRecoil();
            lookRot *= Quaternion.Euler(-recoil.y, recoil.x, 0f);
        }

        cameraPoint.rotation = lookRot;
    }

    // =========================
    // MOVEMENT OVERRIDE
    // =========================
    public void OverrideMovement(MovementOverrideSource source, float moveMul, float jumpMul)
    {
        overrides[source] = new Vector2(Mathf.Clamp01(moveMul), Mathf.Clamp01(jumpMul));
        RecalculateOverrides();
    }

    public void ClearOverrideMovement(MovementOverrideSource source)
    {
        if (overrides.Remove(source))
            RecalculateOverrides();
    }

    void RecalculateOverrides()
    {
        moveOverrideMul = 1f;
        jumpOverrideMul = 1f;

        foreach (var kv in overrides)
        {
            moveOverrideMul = Mathf.Min(moveOverrideMul, kv.Value.x);
            jumpOverrideMul = Mathf.Min(jumpOverrideMul, kv.Value.y);
        }

        hasMoveOverride = overrides.Count > 0;
    }

    // =========================
    // MOVEMENT
    // =========================
    void HandleMovement()
    {
        isGrounded = controller.isGrounded;

        if (Keyboard.current == null) return;

        Vector2 input = Vector2.zero;
        if (Keyboard.current.wKey.isPressed) input.y += 1;
        if (Keyboard.current.sKey.isPressed) input.y -= 1;
        if (Keyboard.current.aKey.isPressed) input.x -= 1;
        if (Keyboard.current.dKey.isPressed) input.x += 1;

        input = Vector2.ClampMagnitude(input, 1f);
        Vector3 move = transform.right * input.x + transform.forward * input.y;
        float speed = hasMoveOverride ? moveSpeed * moveOverrideMul : moveSpeed;

        if (Keyboard.current.spaceKey.wasPressedThisFrame && isGrounded)
        {
            float jump = hasMoveOverride ? jumpHeight * jumpOverrideMul : jumpHeight;
            verticalVelocity = Mathf.Sqrt(jump * -2f * gravity);
        }

        if (useGravity)
            verticalVelocity += gravity * Time.deltaTime;

        if (scatting)
        {
            scatterVelocityOwner.y += scatterGravity * Time.deltaTime;

            Vector3 scrat = scatterVelocityOwner * Time.deltaTime;
            CollisionFlags flags = controller.Move(scrat);

            if ((flags & CollisionFlags.Below) != 0 && scatterVelocityOwner.y < 0f)
            {
                scatting = false;
                scatterVelocityOwner.y = 0f;
                scatterVelocityOwner.x *= 0.15f;
                scatterVelocityOwner.z *= 0.15f;
            }
        }
        else
        {
            controller.Move((move * speed + Vector3.up * verticalVelocity) * Time.deltaTime);
        }
    }

    // =========================
    // WEAPON BLOCK
    // =========================
    void HandleWeaponBlock()
    {
        if (currentWeapon == null || firePoint == null || cameraPoint == null) return;

        bool blocked = Physics.SphereCast(
            cameraPoint.position, 0.035f, cameraPoint.forward,
            out _, weaponBlockCheckDistance, weaponBlockLayer,
            QueryTriggerInteraction.Ignore
        );

        if (!blocked && currentGun != null && currentGun.IsReloading()) blocked = true;

        if (blocked) weaponBlockDelayTime = Time.deltaTime * (weaponAdjustSpeed + 3);

        if (blocked != currentWeapon.GetBlocked())
        {
            currentWeapon.SetBlocked(blocked);
        }

        Vector3 targetPos = blocked ? weaponBlockedPosition.localPosition : defaultWeaponLocalPos;
        Quaternion targetRot = blocked ? weaponBlockedPosition.localRotation : defaultWeaponLocalRot;

        currentWeaponLocalPos = Vector3.Lerp(currentWeaponLocalPos, targetPos, Time.deltaTime * weaponAdjustSpeed);
        currentWeaponLocalRot = Quaternion.Slerp(currentWeaponLocalRot, targetRot, Time.deltaTime * weaponAdjustSpeed);

        weaponHolder.SetLocalPositionAndRotation(currentWeaponLocalPos, currentWeaponLocalRot);
    }
}
public interface IAbortableMovementSkill
{
    void AbortMovement();
}