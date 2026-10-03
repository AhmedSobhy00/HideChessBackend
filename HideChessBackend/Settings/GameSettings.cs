namespace HideChessBackend.Settings;

/// <summary>
/// Configurable game parameters — change in appsettings.json rather than touching code.
/// </summary>
public class GameSettings
{
    public const string Section = "GameSettings";

    /// <summary>Seconds players have to arrange their pieces during setup.</summary>
    public int SetupDurationSeconds { get; set; } = 60;

    /// <summary>Seconds a disconnected player has to reconnect before forfeiting.</summary>
    public int DisconnectionGraceSeconds { get; set; } = 30;

    /// <summary>Number of ranks (from the back rank) each player may deploy pieces in.</summary>
    public int DeploymentRanks { get; set; } = 4;
}
