using FilmStudio.Web.Components;
using FilmStudio.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<EngineApiOptions>(
    builder.Configuration.GetSection(EngineApiOptions.SectionName));

builder.Services.AddScoped<AdminSessionService>();
// ProtectedSessionStorage is used by AdminSessionService to survive per-page circuits
builder.Services.AddHttpClient("FilmStudio.Api", (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EngineApiOptions>>().Value;
    var baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl)
        ? "http://127.0.0.1:5088"
        : opts.BaseUrl.TrimEnd('/') + "/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddScoped(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("FilmStudio.Api");
    return new EngineApiClient(http, sp.GetRequiredService<AdminSessionService>());
});

builder.Services.AddScoped(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EngineApiOptions>>();
    return new JobHubClient(opts, sp.GetRequiredService<AdminSessionService>());
});

// Antiforgery: allow HTTP localhost in Development (avoid Secure cookie blocked on http://)
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "FilmStudio.Antiforgery";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = appEnvIsDev(builder)
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}
// Development: no HTTPS redirect — API is http://127.0.0.1:5088; mixed https Web + http
// breaks SameSite cookies (admin login antiforgery, etc.).

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static bool appEnvIsDev(WebApplicationBuilder b) =>
    b.Environment.IsDevelopment();
