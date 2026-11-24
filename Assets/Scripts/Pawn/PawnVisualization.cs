using UnityEngine;

/// <summary>Draws what the Pawn's data says: hair / head / body by facing, and the weapon sprite.</summary>
public class PawnVisualization : TickBehaviour
{
    [Header("References")]
    public Pawn owner;
    public SpriteRenderer Hair;
    public SpriteRenderer Head;
    public SpriteRenderer Body;
    [Tooltip("Optional. Owns the weapon root, rotation, sorting and attacks. This script only gives it the sprite.")]
    public PawnWeapon Weapon;

    private bool hasFace;
    private FaceDirection lastFace;

    #region Public API
    /// <summary>Bind to the pawn and draw. Called by Pawn.Init.</summary>
    public virtual void Activate(Pawn pawn)
    {
        Unbind();
        if (pawn != null) owner = pawn;
        if (owner == null) return;
        if (Weapon == null) Weapon = owner.weapon;

        Bind();
        Refresh(true);
        if (Weapon != null) Weapon.Activate(owner); // after Refresh so the sprite is already set
    }

    /// <summary>Re-apply sprites. force = ignore the "facing did not change" shortcut.</summary>
    public void Refresh(bool force = false)
    {
        if (owner == null) return;

        FaceDirection face = owner.Facing;
        if (!force && hasFace && face == lastFace) return;
        hasFace = true;
        lastFace = face;

        GameDatabase db = owner.Database;
        Apply(Hair, db.GetLook(LookSlot.Hair, owner.HairId), face);
        Apply(Head, db.GetLook(LookSlot.Head, owner.HeadId), face);
        Apply(Body, db.GetLook(LookSlot.Body, owner.BodyId), face);

        if (Weapon != null) Weapon.SetSprite(owner.Equipped.sprite);
    }

    public void ShowWeapon(bool show)
    {
        if (Weapon != null) Weapon.Show(show);
    }
    #endregion

    #region Internals
    private static void Apply(SpriteRenderer r, LookDef look, FaceDirection face)
    {
        if (r == null) return;
        if (look == null) { r.sprite = null; return; }
        r.sprite = look.GetSprite(face, out bool flip);
        r.flipX = flip; // West = mirrored East
    }

    private void Bind() { owner.OnDataChanged += HandleDataChanged; }
    private void Unbind() { if (owner != null) owner.OnDataChanged -= HandleDataChanged; }
    private void HandleDataChanged(Pawn p) => Refresh(true);

    protected override void On()
    {
        base.On();
        if (owner != null) { Unbind(); Bind(); } // re-enable after Off
    }

    protected override void Off()
    {
        base.Off();
        Unbind();
    }

    // Comparing one enum per frame is cheaper than a timer. Sprites only change when the facing did.
    protected override void Tick(float deltaTime) => Refresh();
    #endregion
}