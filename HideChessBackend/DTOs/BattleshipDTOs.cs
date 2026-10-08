using HideChessBackend.Models.Battleship;

namespace HideChessBackend.DTOs;

public class CreateBattleshipGameRequest
{
    public string PlayerName { get; set; } = "Commander";
}

public class CreateBattleshipBotRequest
{
    public string PlayerName { get; set; } = "Commander";
    public string Difficulty { get; set; } = "Medium"; // Easy, Medium, Hard
}

public class JoinBattleshipGameRequest
{
    public string GameId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = "Commander";
}

public class PlaceShipDTO
{
    public ShipType Type { get; set; }
    public int StartRow { get; set; }
    public int StartCol { get; set; }
    public bool IsVertical { get; set; }
}

public class PlaceFleetRequest
{
    public string GameId { get; set; } = string.Empty;
    public List<PlaceShipDTO> Ships { get; set; } = new();
}

public class FireShotRequest
{
    public string GameId { get; set; } = string.Empty;
    public int TargetRow { get; set; }
    public int TargetCol { get; set; }
}
