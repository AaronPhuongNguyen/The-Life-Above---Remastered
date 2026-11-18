using UnityEngine;

public class GameUnit : TickBehaviour,IUnit
{
    #region Properties
    [Header("Unit Core")]
    public UnitAttribute Attribute;
    public UnitAttribute GetAttribute() => Attribute != null ? Attribute : null;

    [Header("Properties")]
    public bool Active = false;
    public Vector2 direction;

    [HideInInspector] public bool canMove=true,isMoving;
    #endregion

    #region Initialize Unit
    protected override void On()
    {
        base.On();
        
    }
    protected override void Off()
    {
        base.Off();
    }
    protected virtual void InitializeUnit(UnitAttribute attribute)
    {
        if (attribute == null) return;
    }
    #endregion

    #region Given Tick
    #endregion

    #region Universal Behaviours
    public void MoveUnit(float deltaTime)
    {

    }
    #endregion

    #region Unit Helpers
    //Active
    protected void SetUnitActive(bool switcher) => Active = switcher;
    public void ToggleUnitActive() => Active = !Active;
    public void TurnUnitOn() => SetUnitActive(true);
    public void TurnUnitOff() => SetUnitActive(false);

    //Move
    protected void SetUnitMovability(bool switcher) => canMove = switcher;
    //...
    #endregion

    #region Deep Debugger
    [ContextMenu("Try move random")]
    public void TryMoveRandom() { }
    #endregion
}