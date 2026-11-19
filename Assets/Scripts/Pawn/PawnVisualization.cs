using UnityEngine;

public class PawnVisualization : TickBehaviour
{
    [Header("Properties")]
    public Pawn owner;
    public GameDatabase data;
    [SerializeField] private string DatabaseName = "GeneralDatabase";

    [Header("Renderer")]
    public SpriteRenderer Hair;
    public SpriteRenderer Head;
    public SpriteRenderer Body;
    [Tooltip("Optional. Shows the equipped gun.")]
    public SpriteRenderer Weapon;

    [Header("Look (index into the list of the pawn's gender, -1 = pick randomly on Activate)")]
    [SerializeField] private int hairIndex = -1;
    [SerializeField] private int headIndex = -1;
    [SerializeField] private int bodyIndex = -1;
    [SerializeField] private int gunIndex = -1;

    [Header("Refresh")]
    [Tooltip("Seconds between facing / gender checks. Sprites only change when something actually changed.")]
    [SerializeField, Min(0f)] private float refreshInterval = 0.1f;

    private float timer;
    private bool hasFace;
    private FaceDirection lastFace;
    private bool hasGender;
    private Gender lastGender;

    public int HairIndex => hairIndex;
    public int HeadIndex => headIndex;
    public int BodyIndex => bodyIndex;
    public int GunIndex => gunIndex;

    private Gender CurrentGender => owner != null ? owner.Gender : Gender.Male;

    #region Public API
    /// <summary>
    /// Set owner and initialize the pawn's visualization.
    /// </summary>
    public virtual void Activate(Pawn pawn)
    {
        //Pawn will call this when pawn is generated.
        if (pawn != null) owner = pawn;

        if (data == null)
        {
            GameDatabase loaded = Resources.Load<GameDatabase>(DatabaseName);
            if (loaded == null)
            {
                Debug.LogError($"[PawnVisualization] Database '{DatabaseName}' not found in a Resources folder.", this);
                return;
            }
            data = loaded.Clone();
        }

        hasGender = owner != null;
        lastGender = CurrentGender;

        if (hairIndex < 0 || headIndex < 0 || bodyIndex < 0) RollBody(lastGender);
        if (gunIndex < 0) data.GetRandomGun(out gunIndex);

        Refresh(true);
    }

    /// <summary>Pick a random hair / head / body of the pawn's gender (and a gun when includeGun).</summary>
    public void RandomizeLook(bool includeGun = true)
    {
        if (data == null) return;
        RollBody(CurrentGender);
        if (includeGun || gunIndex < 0) data.GetRandomGun(out gunIndex);
        Refresh(true);
    }

    public void SetLook(int hair, int head, int body, int gun = -1)
    {
        hairIndex = hair; headIndex = head; bodyIndex = body;
        if (gun >= -1) gunIndex = gun;
        Refresh(true);
    }

    public void SetHair(int index) { hairIndex = index; Refresh(true); }
    public void SetHead(int index) { headIndex = index; Refresh(true); }
    public void SetBody(int index) { bodyIndex = index; Refresh(true); }
    public void SetGun(int index) { gunIndex = index; Refresh(true); }

    public void ShowWeapon(bool show)
    {
        if (Weapon != null) Weapon.enabled = show;
    }

    /// <summary>
    /// Re-apply sprites. force = ignore the "nothing changed" shortcut.
    /// If the pawn's gender changed since the last refresh, a new random hair/head/body of that gender is picked
    /// (the old indices belong to the other list).
    /// </summary>
    public void Refresh(bool force = false)
    {
        if (data == null || owner == null) return;

        Gender gender = owner.Gender;
        if (!hasGender) { hasGender = true; lastGender = gender; }
        else if (gender != lastGender)
        {
            lastGender = gender;
            RollBody(gender);
            force = true;
        }

        FaceDirection face = owner.Facing;
        if (!force && hasFace && face == lastFace) return;
        hasFace = true;
        lastFace = face;

        Apply(Hair, data.GetHair(gender, hairIndex), face);
        Apply(Head, data.GetHead(gender, headIndex), face);
        Apply(Body, data.GetBody(gender, bodyIndex), face);

        if (Weapon != null)
        {
            var gun = data.GetGun(gunIndex);
            Weapon.sprite = gun != null ? gun.sprite : null;
            Weapon.flipX = face == FaceDirection.West;
        }
    }

    private void RollBody(Gender gender)
    {
        data.GetRandomHair(gender, out hairIndex);
        data.GetRandomHead(gender, out headIndex);
        data.GetRandomBody(gender, out bodyIndex);
    }

    private static void Apply(SpriteRenderer r, SpritePackage_Multiple package, FaceDirection face)
    {
        if (r == null) return;
        if (package == null) { r.sprite = null; return; }
        r.sprite = package.GetSprite(face, out bool flip);
        r.flipX = flip; // West = mirrored East
    }
    #endregion

    #region Main thread
    /// Depends on the pawn's FaceDirection (and Gender) to assign the related sprite into each renderer.
    /// South/East/North use their own sprite, West = mirrored East.
    protected override void Tick(float deltaTime)
    {
        timer += deltaTime;
        if (timer < refreshInterval) return;
        timer = 0f;
        Refresh();
    }
    #endregion
}