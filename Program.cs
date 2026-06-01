using FRAGA.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddScoped<FreteService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => Results.Ok(new
{
    nome = "FRAGA API",
    status = "online",
    endpoints = new[]
    {
        "POST /api/fraga/frete/cotar",
        "GET /api/fraga/frete/regras/{cepDestino}",
        "GET /api/fraga/health"
    }
}));

app.MapGet("/api/fraga/health", () => Results.Ok(new
{
    sucesso = true,
    servico = "FRAGA",
    status = "online",
    dataHora = DateTimeOffset.Now
}));

app.MapControllers();

app.Run();
