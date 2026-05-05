namespace ArmageddonRidge.Core;

public static class GameConstants
{
    public const int WorldWidth = 1200;
    public const int WorldHeight = 700;
    public const int GroundMinY = 300;
    public const int GroundMaxY = 650;
    public const int TankWidth = 32;
    public const int TankHeight = 18;
    public const int TankCollisionWidth = 74;
    public const int TankCollisionHeight = 46;
    public const float ProjectileCollisionRadius = 5f;
    public const float FixedDeltaTime = 1f / 60f;
    public const float Gravity = 120f;
    public const int WindMin = -24;
    public const int WindMax = 24;
    public const int PowerMin = 1;
    public const int PowerMax = 100;
    public const int StartingHealth = 75;
    public const int ArmorUpgradeMax = 150;
    public const int StartingCash = 5000;
    public const int WinReward = 750;
    public const int LossConsolation = 300;
    public const int KillBonus = 500;
}
