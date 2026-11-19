using System;
using UnityEngine;

public class Pawn : GameUnit
{
    #region Settings
    [Header("Identity")]
    [SerializeField] private Gender gender = Gender.Male;
    [Tooltip("Pick a random gender every time InitializeUnit is called (spawning).")]
    [SerializeField] private bool randomGenderOnInit = false;

    [Header("Assets")]
    public GameDatabase Database;
    public PawnVisualization visualizer;
    public PawnWeapon weapon;
    #endregion

    #region State
    /// <summary>Male or Female. Change it with SetGender so listeners (visualization, UI) are notified.</summary>
    public Gender Gender => gender;

    public event Action<Pawn> OnGenderChanged;
    #endregion

    #region Lifecycle
    public override void InitializeUnit(UnitAttribute attribute)
    {
        base.InitializeUnit(attribute);
        if (randomGenderOnInit) SetGender(RNG.GetInt(0, 2) == 0 ? Gender.Male : Gender.Female);
    }

    /// <summary>Change gender. PawnVisualization picks a matching Male/Female look on its next refresh.</summary>
    public void SetGender(Gender value)
    {
        if (gender == value) return;
        gender = value;
        OnGenderChanged?.Invoke(this);
    }

    protected override void OnValidate()
    {
        base.OnValidate();

        if (!gameObject.TryGetComponent<PawnVisualization>(out visualizer))
            visualizer = gameObject.AddComponent<PawnVisualization>();
        if (weapon == null) weapon = GetComponentInChildren<PawnWeapon>(true);
        visualizer.Activate(this);
    }
    #endregion
}