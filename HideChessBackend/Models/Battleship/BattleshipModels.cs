namespace HideChessBackend.Models.Battleship;

public enum ShipType
{
    Carrier = 5,    // 5 cells
    Battleship = 4, // 4 cells
    Cruiser = 3,    // 3 cells
    Submarine = 3,  // 3 cells
    Destroyer = 2   // 2 cells
}

public enum CellState
{
    Empty,
    Ship,
    Hit,
    Miss,
    Sunk
}

public class Coordinate
{
    public int Row { get; set; } // 0..9
    public int Col { get; set; } // 0..9

    public Coordinate() { }
    public Coordinate(int row, int col)
    {
        Row = row;
        Col = col;
    }
}

public class ShipInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ShipType Type { get; set; }
    public int Length => (int)Type;
    public int StartRow { get; set; }
    public int StartCol { get; set; }
    public bool IsVertical { get; set; }
    public List<Coordinate> OccupiedCells { get; set; } = new();
    public int Hits { get; set; }
    public bool IsSunk => Hits >= Length;
}

public class BattleshipPlayer
{
    public string PlayerId { get; set; } = Guid.NewGuid().ToString("N");
    public string ConnectionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsBot { get; set; }
    public string BotDifficulty { get; set; } = "Medium"; // Easy, Medium, Hard
    public bool IsReady { get; set; }
    public List<ShipInstance> Ships { get; set; } = new();
    public CellState[,] Grid { get; set; } = new CellState[10, 10]; // Own fleet grid
    public CellState[,] TargetRadar { get; set; } = new CellState[10, 10]; // Enemy radar grid
    public List<Coordinate> HitsOnOpponent { get; set; } = new();
    public List<Coordinate> MissesOnOpponent { get; set; } = new();
    
    // Bot AI targeting memory
    public List<Coordinate> BotTargetQueue { get; set; } = new();
    public Coordinate? BotFirstHit { get; set; }
    public Coordinate? BotLastHit { get; set; }
}

public enum BattleshipPhase
{
    WaitingForPlayers,
    Setup,
    Playing,
    Finished,
    Abandoned
}

public class BattleshipGameState
{
    public string GameId { get; set; } = string.Empty;
    public string RoomCode { get; set; } = string.Empty;
    public BattleshipPhase Phase { get; set; } = BattleshipPhase.WaitingForPlayers;
    public BattleshipPlayer Host { get; set; } = new();
    public BattleshipPlayer? Guest { get; set; }
    public string CurrentTurnPlayerId { get; set; } = string.Empty;
    public string? WinnerPlayerId { get; set; }
    public string? WinnerName { get; set; }
    public List<string> ActionLogs { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
