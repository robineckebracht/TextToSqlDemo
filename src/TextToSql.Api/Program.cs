using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpClient<OllamaClient>(client =>
{
    client.BaseAddress = new Uri("http://localhost:11434"); 
    client.Timeout = TimeSpan.FromSeconds(120); 
});



var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("Health")
   .WithOpenApi();

app.MapPost("/generate-sql",async (GenerateSqlRequest req, OllamaClient ollama) =>
{
    var schema = string.IsNullOrWhiteSpace(req.Schema) ? "customers(id, name, email)" : req.Schema;

     var prompt = PromptBuilder.BuildSQLitePrompt(req.Question, schema);

    
    
    var raw = await ollama.GenerateAsync(model: "llama3.1:8b", prompt: prompt);

    var sql = SqlSafety.ExtractSql(raw);
    if (!SqlSafety.IsSelectOnly(sql))
        return Results.BadRequest(new { error = "Generated SQL was not SELECT-only.", raw });

    return Results.Ok(new GenerateSqlResponse(sql));
    
})
.WithName("GenerateSql")
.WithOpenApi();

app.Run();

public record GenerateSqlRequest(string Question, string? Schema);
public record GenerateSqlResponse(string Sql);


public sealed class OllamaClient
{
    private readonly HttpClient _http;

    public OllamaClient(HttpClient http) => _http = http;

    public async Task<string> GenerateAsync(string model, string prompt)
    {
        var payload = new
        {
            model,
            prompt,
            stream = false
        };

        using var resp = await _http.PostAsJsonAsync("api/generate",payload);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
        return json?.response ?? "";

    }

    private sealed record OllamaGenerateResponse(string response);

}

public static class PromptBuilder
{
    public static string BuildSQLitePrompt(string question, string schema)
    {return $"""
            Du bist ein SQL-Generator. Erzeuge eine gültige SQLite SELECT-Abfrage.

            REGELN:
            - Gib NUR SQL zurück (keine Erklärungen, kein Markdown).
            - Nur SELECT. KEIN INSERT/UPDATE/DELETE/DROP/ALTER/CREATE.
            - Nutze nur Tabellen/Spalten aus dem Schema.
            - Wenn etwas nicht möglich ist, gib ein leeres SELECT zurück: SELECT 1;

            SCHEMA:
            {schema}

            FRAGE:
            {question}
            """;
    }


}

public static class SqlSafety
{
    public static string ExtractSql(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "SELECT 1;";

        var s = raw.Trim();
        s = s.Replace("```sql", "", StringComparison.OrdinalIgnoreCase)
             .Replace("```", "");

        s = s.Trim();

        return s;
    }

    public static bool IsSelectOnly(string sql)
{
    if (string.IsNullOrWhiteSpace(sql)) return false;

    var s = sql.TrimStart();

    if (!Regex.IsMatch(s, @"^\s*SELECT\b", RegexOptions.IgnoreCase))
        return false;

    var forbiddenPattern =
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|EXEC|MERGE|REPLACE|PRAGMA|ATTACH|DETACH)\b";

    if (Regex.IsMatch(s, forbiddenPattern, RegexOptions.IgnoreCase))
        return false;

    return true;
}


}


