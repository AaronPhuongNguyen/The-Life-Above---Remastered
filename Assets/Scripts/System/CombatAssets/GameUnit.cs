using UnityEngine;

public class GameUnit : TickBehaviour,IUnit
{
    #region Properties
    [Header("Unit Core")]
    public UnitAttribute Attribute;

    public UnitAttribute GetAttribute() => Attribute != null ? Attribute : null;
    #endregion
}