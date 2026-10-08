using HideChessBackend.Hubs;
using HideChessBackend.Services;
using HideChessBackend.Settings;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Services.Configure<GameSettings>(
    builder.Configuration.GetSection(GameSettings.Section));

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularClient", policy =>
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});
// ── Controllers ───────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ── SignalR ───────────────────────────────────────────────────────────────────
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ── Swagger ───────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Domain services (Singleton — in-memory state must persist across requests)
builder.Services.AddSingleton<IChessService, ChessService>();
builder.Services.AddSingleton<IGameService, GameService>();
builder.Services.AddSingleton<IBattleshipService, BattleshipService>();

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("AngularClient");        // Must come before UseAuthorization and MapHub
app.UseWebSockets();
app.UseAuthorization();
app.MapControllers();
app.MapHub<GameHub>("/gamehub");           // Chess SignalR endpoint
app.MapHub<BattleshipHub>("/battleshiphub"); // Battleship SignalR endpoint

app.Run();
