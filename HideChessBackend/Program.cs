using HideChessBackend.Hubs;
using HideChessBackend.Services;
using HideChessBackend.Settings;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Services.Configure<GameSettings>(
    builder.Configuration.GetSection(GameSettings.Section));

// ── CORS ──────────────────────────────────────────────────────────────────────
// AllowCredentials() is required for SignalR WebSocket negotiation
builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularClient", policy =>
        policy.SetIsOriginAllowed(origin => 
                  string.IsNullOrEmpty(origin) || 
                  new Uri(origin).Host is "localhost" or "127.0.0.1")
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

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors("AngularClient");        // Must come before UseAuthorization and MapHub
app.UseAuthorization();
app.MapControllers();
app.MapHub<GameHub>("/gamehub");     // SignalR endpoint

app.Run();
