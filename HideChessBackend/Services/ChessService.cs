using HideChessBackend.Enums;
using HideChessBackend.Models;

namespace HideChessBackend.Services;

/// <summary>
/// Full chess-rule engine implemented from scratch.
///
/// Coordinate system:
///   Row 0 = rank 1 = White back rank  (a1 … h1)
///   Row 7 = rank 8 = Black back rank  (a8 … h8)
///   Col 0 = file a, Col 7 = file h
///
/// White pawns move in the +row direction; Black pawns in the −row direction.
/// </summary>
public class ChessService : IChessService
{
    // ── Deployment zone (configurable via GameSettings but also used inline) ──
    private const int DeploymentRanks = 4; // each player's setup rows

    // Standard back-rank order: a=Rook, b=Knight, c=Bishop, d=Queen, e=King, f=Bishop, g=Knight, h=Rook
    private static readonly PieceType[] BackRank =
    {
        PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
        PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook
    };

    // ════════════════════════════════════════════════════════════════════════
    //  Setup helpers
    // ════════════════════════════════════════════════════════════════════════

    public Dictionary<(int row, int col), ChessPieceInfo> CreateDefaultSetup(PieceColor color)
    {
        var board = new Dictionary<(int, int), ChessPieceInfo>();

        if (color == PieceColor.White)
        {
            // Row 0 (rank 1) – back rank
            for (int c = 0; c < 8; c++)
                board[(0, c)] = new ChessPieceInfo { Type = BackRank[c], Color = color, Row = 0, Col = c };
            // Row 1 (rank 2) – pawns
            for (int c = 0; c < 8; c++)
                board[(1, c)] = new ChessPieceInfo { Type = PieceType.Pawn, Color = color, Row = 1, Col = c };
        }
        else
        {
            // Row 7 (rank 8) – back rank
            for (int c = 0; c < 8; c++)
                board[(7, c)] = new ChessPieceInfo { Type = BackRank[c], Color = color, Row = 7, Col = c };
            // Row 6 (rank 7) – pawns
            for (int c = 0; c < 8; c++)
                board[(6, c)] = new ChessPieceInfo { Type = PieceType.Pawn, Color = color, Row = 6, Col = c };
        }

        return board;
    }

    public bool ValidateSetupFormation(Dictionary<(int row, int col), ChessPieceInfo> board, PieceColor color)
    {
        int minRow = color == PieceColor.White ? 0 : 4;
        int maxRow = color == PieceColor.White ? 3 : 7;

        // Every piece must belong to 'color' and be inside the deployment zone
        if (board.Any(kvp => kvp.Value.Color != color ||
                              kvp.Key.row < minRow || kvp.Key.row > maxRow ||
                              kvp.Key.col < 0 || kvp.Key.col > 7))
            return false;

        var pieces = board.Values.ToList();
        return pieces.Count == 16
               && pieces.Count(p => p.Type == PieceType.King)   == 1
               && pieces.Count(p => p.Type == PieceType.Queen)  == 1
               && pieces.Count(p => p.Type == PieceType.Rook)   == 2
               && pieces.Count(p => p.Type == PieceType.Bishop) == 2
               && pieces.Count(p => p.Type == PieceType.Knight) == 2
               && pieces.Count(p => p.Type == PieceType.Pawn)   == 8;
    }

    public Dictionary<(int row, int col), ChessPieceInfo> CombineBoards(
        Dictionary<(int row, int col), ChessPieceInfo> whiteBoard,
        Dictionary<(int row, int col), ChessPieceInfo> blackBoard)
    {
        var combined = new Dictionary<(int, int), ChessPieceInfo>(whiteBoard);
        foreach (var (key, piece) in blackBoard)
            combined[key] = piece;
        return combined;
    }

    public bool AreKingsAdjacent(Dictionary<(int row, int col), ChessPieceInfo> board)
    {
        (int r, int c) wk = (-1, -1), bk = (-1, -1);
        foreach (var ((r, c), p) in board)
        {
            if (p.Type != PieceType.King) continue;
            if (p.Color == PieceColor.White) wk = (r, c);
            else                             bk = (r, c);
        }
        if (wk.r < 0 || bk.r < 0) return false;
        return Math.Abs(wk.r - bk.r) <= 1 && Math.Abs(wk.c - bk.c) <= 1;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Move generation (pseudo-legal → filtered for legality)
    // ════════════════════════════════════════════════════════════════════════

    public List<(int row, int col)> GetLegalMoves(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        int fromRow, int fromCol,
        (int row, int col)? enPassantTarget)
    {
        if (!board.TryGetValue((fromRow, fromCol), out var piece))
            return new List<(int, int)>();

        var pseudo = GetPseudoLegalMoves(board, fromRow, fromCol, piece, enPassantTarget);
        var legal  = new List<(int, int)>(pseudo.Count);

        foreach (var (tr, tc) in pseudo)
        {
            var simBoard = SimulateMove(board, fromRow, fromCol, tr, tc, piece, enPassantTarget);
            if (!IsInCheck(simBoard, piece.Color))
                legal.Add((tr, tc));
        }
        return legal;
    }

    // ── Dispatcher ──────────────────────────────────────────────────────────
    private List<(int, int)> GetPseudoLegalMoves(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        int row, int col,
        ChessPieceInfo piece,
        (int row, int col)? ep)
        => piece.Type switch
        {
            PieceType.Pawn   => PawnMoves(board, row, col, piece.Color, ep),
            PieceType.Knight => KnightMoves(board, row, col, piece.Color),
            PieceType.Bishop => SlidingMoves(board, row, col, piece.Color,
                                    new[] { (1,1),(1,-1),(-1,1),(-1,-1) }),
            PieceType.Rook   => SlidingMoves(board, row, col, piece.Color,
                                    new[] { (1,0),(-1,0),(0,1),(0,-1) }),
            PieceType.Queen  => SlidingMoves(board, row, col, piece.Color,
                                    new[] { (1,1),(1,-1),(-1,1),(-1,-1),(1,0),(-1,0),(0,1),(0,-1) }),
            PieceType.King   => KingMoves(board, row, col, piece.Color),
            _                => new List<(int, int)>()
        };

    // ── Pawn ────────────────────────────────────────────────────────────────
    private static List<(int, int)> PawnMoves(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        int row, int col, PieceColor color,
        (int row, int col)? ep)
    {
        var moves   = new List<(int, int)>(4);
        int dir     = color == PieceColor.White ? 1 : -1;
        int startR  = color == PieceColor.White ? 1 : 6; // starting rank for double-push

        // Single forward push
        int nextR = row + dir;
        if (IsOnBoard(nextR, col) && !board.ContainsKey((nextR, col)))
        {
            moves.Add((nextR, col));
            // Double push only from the pawn's starting rank
            if (row == startR)
            {
                int doubleR = row + 2 * dir;
                if (!board.ContainsKey((doubleR, col)))
                    moves.Add((doubleR, col));
            }
        }

        // Diagonal captures (normal + en-passant)
        foreach (int dc in new[] { -1, 1 })
        {
            int cc = col + dc;
            if (!IsOnBoard(nextR, cc)) continue;

            if (board.TryGetValue((nextR, cc), out var target) && target.Color != color)
                moves.Add((nextR, cc));
            else if (ep.HasValue && nextR == ep.Value.row && cc == ep.Value.col)
                moves.Add((nextR, cc));
        }
        return moves;
    }

    // ── Knight ──────────────────────────────────────────────────────────────
    private static List<(int, int)> KnightMoves(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        int row, int col, PieceColor color)
    {
        var moves   = new List<(int, int)>(8);
        int[] drs   = { 2, 2,-2,-2, 1, 1,-1,-1 };
        int[] dcs   = { 1,-1, 1,-1, 2,-2, 2,-2 };
        for (int i = 0; i < 8; i++)
        {
            int nr = row + drs[i], nc = col + dcs[i];
            if (!IsOnBoard(nr, nc)) continue;
            if (board.TryGetValue((nr, nc), out var p) && p.Color == color) continue;
            moves.Add((nr, nc));
        }
        return moves;
    }

    // ── Sliding pieces (bishop / rook / queen) ───────────────────────────────
    private static List<(int, int)> SlidingMoves(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        int row, int col, PieceColor color,
        (int dr, int dc)[] directions)
    {
        var moves = new List<(int, int)>(16);
        foreach (var (dr, dc) in directions)
        {
            int nr = row + dr, nc = col + dc;
            while (IsOnBoard(nr, nc))
            {
                if (board.TryGetValue((nr, nc), out var p))
                {
                    if (p.Color != color) moves.Add((nr, nc)); // capture
                    break;                                     // blocked
                }
                moves.Add((nr, nc));
                nr += dr; nc += dc;
            }
        }
        return moves;
    }

    // ── King ────────────────────────────────────────────────────────────────
    private static List<(int, int)> KingMoves(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        int row, int col, PieceColor color)
    {
        var moves = new List<(int, int)>(8);
        int[] drs = { 1, 1, 1, 0, 0,-1,-1,-1 };
        int[] dcs = { 1, 0,-1, 1,-1, 1, 0,-1 };
        for (int i = 0; i < 8; i++)
        {
            int nr = row + drs[i], nc = col + dcs[i];
            if (!IsOnBoard(nr, nc)) continue;
            if (board.TryGetValue((nr, nc), out var p) && p.Color == color) continue;
            moves.Add((nr, nc));
        }
        return moves;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Check / checkmate / stalemate
    // ════════════════════════════════════════════════════════════════════════

    public bool IsInCheck(Dictionary<(int row, int col), ChessPieceInfo> board, PieceColor color)
    {
        // Locate the king
        (int r, int c) king = (-1, -1);
        foreach (var ((r, c), p) in board)
        {
            if (p.Color == color && p.Type == PieceType.King)
            {
                king = (r, c); break;
            }
        }
        if (king.r < 0) return false; // no king on board (shouldn't happen)

        var opp = Opposite(color);
        // Check whether any opponent piece attacks the king square.
        // Use pseudo-legal moves (not legal) to avoid infinite recursion.
        foreach (var ((r, c), p) in board)
        {
            if (p.Color != opp) continue;
            var attacks = GetPseudoLegalMoves(board, r, c, p, null);
            if (attacks.Any(m => m.Item1 == king.r && m.Item2 == king.c))
                return true;
        }
        return false;
    }

    public bool IsCheckmate(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        PieceColor color,
        (int row, int col)? enPassantTarget)
        => IsInCheck(board, color) && !HasAnyLegalMove(board, color, enPassantTarget);

    public bool IsStalemate(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        PieceColor color,
        (int row, int col)? enPassantTarget)
        => !IsInCheck(board, color) && !HasAnyLegalMove(board, color, enPassantTarget);

    private bool HasAnyLegalMove(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        PieceColor color,
        (int row, int col)? ep)
    {
        foreach (var ((r, c), p) in board)
        {
            if (p.Color != color) continue;
            if (GetLegalMoves(board, r, c, ep).Count > 0) return true;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Insufficient material
    // ════════════════════════════════════════════════════════════════════════

    public bool HasInsufficientMaterial(Dictionary<(int row, int col), ChessPieceInfo> board)
    {
        var whites = board.Values.Where(p => p.Color == PieceColor.White).ToList();
        var blacks = board.Values.Where(p => p.Color == PieceColor.Black).ToList();

        // K vs K
        if (whites.Count == 1 && blacks.Count == 1) return true;

        // K vs K+N  or  K vs K+B
        static bool OnlyMinor(List<ChessPieceInfo> solo, List<ChessPieceInfo> pair)
        {
            if (solo.Count != 1 || pair.Count != 2) return false;
            var minor = pair.First(p => p.Type != PieceType.King);
            return minor.Type == PieceType.Knight || minor.Type == PieceType.Bishop;
        }
        if (OnlyMinor(whites, blacks) || OnlyMinor(blacks, whites)) return true;

        // K+B vs K+B on same colour squares
        if (whites.Count == 2 && blacks.Count == 2)
        {
            var wb = whites.FirstOrDefault(p => p.Type == PieceType.Bishop);
            var bb = blacks.FirstOrDefault(p => p.Type == PieceType.Bishop);
            if (wb != null && bb != null &&
                (wb.Row + wb.Col) % 2 == (bb.Row + bb.Col) % 2)
                return true;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Apply a move and return the new board
    // ════════════════════════════════════════════════════════════════════════

    public MoveApplicationResult ApplyMove(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        int fromRow, int fromCol,
        int toRow, int toCol,
        PieceType? promotionPiece,
        (int row, int col)? enPassantTarget)
    {
        if (!board.TryGetValue((fromRow, fromCol), out var piece))
            return Fail("No piece at source square");

        var legal = GetLegalMoves(board, fromRow, fromCol, enPassantTarget);
        if (!legal.Any(m => m.row == toRow && m.col == toCol))
            return Fail("Illegal move");

        var newBoard = new Dictionary<(int, int), ChessPieceInfo>(board);
        ChessPieceInfo? captured = null;
        bool isEP    = false;
        bool isPromo = false;
        (int, int)? newEP = null;

        // ── En-passant capture ──────────────────────────────────────────────
        if (piece.Type == PieceType.Pawn &&
            enPassantTarget.HasValue &&
            toRow == enPassantTarget.Value.row &&
            toCol == enPassantTarget.Value.col)
        {
            isEP = true;
            int capturedRow = piece.Color == PieceColor.White ? toRow - 1 : toRow + 1;
            captured = newBoard[(capturedRow, toCol)];
            newBoard.Remove((capturedRow, toCol));
        }
        // ── Normal capture ──────────────────────────────────────────────────
        else if (newBoard.TryGetValue((toRow, toCol), out captured))
        {
            newBoard.Remove((toRow, toCol));
        }
        else
        {
            captured = null;
        }

        // ── En-passant opportunity created by double pawn push ──────────────
        if (piece.Type == PieceType.Pawn && Math.Abs(toRow - fromRow) == 2)
            newEP = ((fromRow + toRow) / 2, fromCol);

        // ── Remove piece from source ────────────────────────────────────────
        newBoard.Remove((fromRow, fromCol));

        // ── Promotion ───────────────────────────────────────────────────────
        var finalType = piece.Type;
        if (piece.Type == PieceType.Pawn)
        {
            bool promotionRank = (piece.Color == PieceColor.White && toRow == 7) ||
                                  (piece.Color == PieceColor.Black && toRow == 0);
            if (promotionRank)
            {
                isPromo   = true;
                finalType = promotionPiece ?? PieceType.Queen;
            }
        }

        // ── Place piece at destination ──────────────────────────────────────
        newBoard[(toRow, toCol)] = new ChessPieceInfo
        {
            Type = finalType, Color = piece.Color, Row = toRow, Col = toCol
        };

        return new MoveApplicationResult
        {
            Success            = true,
            NewBoard           = newBoard,
            CapturedPiece      = captured,
            NewEnPassantTarget = newEP,
            IsPromotion        = isPromo,
            IsEnPassant        = isEP
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Notation helpers
    // ════════════════════════════════════════════════════════════════════════

    public string ToAlgebraic(int row, int col)
        => $"{(char)('a' + col)}{row + 1}";

    public (int row, int col) FromAlgebraic(string pos)
    {
        if (pos.Length < 2)
            throw new ArgumentException($"Invalid algebraic notation: '{pos}'");
        return (pos[1] - '1', pos[0] - 'a');
    }

    public string GetPositionKey(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        PieceColor activeColor,
        string? enPassantTarget)
    {
        // Ordered, compact representation — same position must always yield the same key.
        var sb = new System.Text.StringBuilder(capacity: board.Count * 8);
        foreach (var ((r, c), p) in board.OrderBy(k => k.Key.row * 8 + k.Key.col))
        {
            sb.Append((int)p.Type);
            sb.Append((int)p.Color);
            sb.Append(r);
            sb.Append(',');
            sb.Append(c);
            sb.Append(';');
        }
        sb.Append('|');
        sb.Append((int)activeColor);
        sb.Append('|');
        sb.Append(enPassantTarget ?? "-");
        return sb.ToString();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Private utilities
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulate a single move on a copied board for legality checking.
    /// Does NOT validate legality — that is the caller's responsibility.
    /// </summary>
    private static Dictionary<(int, int), ChessPieceInfo> SimulateMove(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        int fromRow, int fromCol,
        int toRow, int toCol,
        ChessPieceInfo piece,
        (int row, int col)? ep)
    {
        var nb = new Dictionary<(int, int), ChessPieceInfo>(board);
        nb.Remove((fromRow, fromCol));
        nb.Remove((toRow, toCol));

        // En-passant: remove the actual captured pawn (not the landing square)
        if (piece.Type == PieceType.Pawn && ep.HasValue &&
            toRow == ep.Value.row && toCol == ep.Value.col)
        {
            int capR = piece.Color == PieceColor.White ? toRow - 1 : toRow + 1;
            nb.Remove((capR, toCol));
        }

        nb[(toRow, toCol)] = new ChessPieceInfo
        {
            Type = piece.Type, Color = piece.Color, Row = toRow, Col = toCol
        };
        return nb;
    }

    private static bool IsOnBoard(int r, int c) => (uint)r < 8 && (uint)c < 8;

    private static PieceColor Opposite(PieceColor c)
        => c == PieceColor.White ? PieceColor.Black : PieceColor.White;

    private static MoveApplicationResult Fail(string msg)
        => new() { Success = false, Error = msg };
}
