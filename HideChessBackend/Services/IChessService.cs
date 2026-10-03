using HideChessBackend.Enums;
using HideChessBackend.Models;

namespace HideChessBackend.Services;

public interface IChessService
{
    // ── Setup helpers ────────────────────────────────────────────────────────
    Dictionary<(int row, int col), ChessPieceInfo> CreateDefaultSetup(PieceColor color);
    bool ValidateSetupFormation(Dictionary<(int row, int col), ChessPieceInfo> board, PieceColor color);
    Dictionary<(int row, int col), ChessPieceInfo> CombineBoards(
        Dictionary<(int row, int col), ChessPieceInfo> whiteBoard,
        Dictionary<(int row, int col), ChessPieceInfo> blackBoard);
    bool AreKingsAdjacent(Dictionary<(int row, int col), ChessPieceInfo> board);

    // ── Move generation ──────────────────────────────────────────────────────
    List<(int row, int col)> GetLegalMoves(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        int fromRow, int fromCol,
        (int row, int col)? enPassantTarget);

    MoveApplicationResult ApplyMove(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        int fromRow, int fromCol,
        int toRow, int toCol,
        PieceType? promotionPiece,
        (int row, int col)? enPassantTarget);

    // ── Game-state queries ────────────────────────────────────────────────────
    bool IsInCheck(Dictionary<(int row, int col), ChessPieceInfo> board, PieceColor color);
    bool IsSquareAttacked(Dictionary<(int row, int col), ChessPieceInfo> board, int targetRow, int targetCol, PieceColor attackerColor);
    bool IsSquareAttackedByPiece(Dictionary<(int row, int col), ChessPieceInfo> board, int targetRow, int targetCol, int pieceRow, int pieceCol);
    bool IsCheckmate(Dictionary<(int row, int col), ChessPieceInfo> board, PieceColor color, (int row, int col)? enPassantTarget);
    bool IsStalemate(Dictionary<(int row, int col), ChessPieceInfo> board, PieceColor color, (int row, int col)? enPassantTarget);
    bool HasInsufficientMaterial(Dictionary<(int row, int col), ChessPieceInfo> board);

    // ── Notation helpers ─────────────────────────────────────────────────────
    string ToAlgebraic(int row, int col);
    (int row, int col) FromAlgebraic(string algebraic);

    /// <summary>Stable hash of a board position used for threefold-repetition detection.</summary>
    string GetPositionKey(
        Dictionary<(int row, int col), ChessPieceInfo> board,
        PieceColor activeColor,
        string? enPassantTarget);
}
