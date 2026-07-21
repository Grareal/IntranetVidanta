

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddComposers()
    .Build();

WebApplication app = builder.Build();

await app.BootUmbracoAsync();

app.Use(async (context, next) =>
{
    await next();
    if (context.Response.StatusCode == 404 && !context.Request.Path.StartsWithSegments("/umbraco"))
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html";
        var errorHtml = """
        <!DOCTYPE html>
        <html lang="es">
        <head>
        <meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>404 — Página no encontrada — Intranet Vidanta</title>
        <link rel="stylesheet" href="/css/intranet.css">
        <style>
          .error-page{display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:80vh;text-align:center;padding:40px 24px}
          .error-code{font-size:120px;font-weight:800;background:linear-gradient(135deg,var(--navy),var(--teal));-webkit-background-clip:text;-webkit-text-fill-color:transparent;line-height:1;margin-bottom:8px}
          .error-title{font-size:24px;color:var(--navy);margin-bottom:10px}
          .error-msg{font-size:15px;color:var(--gray);max-width:460px;margin-bottom:28px;line-height:1.6}
          .error-actions{display:flex;gap:14px}
          .error-btn{display:inline-flex;align-items:center;gap:8px;padding:12px 28px;border-radius:10px;font-weight:600;font-size:14px;text-decoration:none;transition:.2s}
          .error-btn.primary{background:linear-gradient(135deg,var(--blue),var(--teal));color:#fff;box-shadow:0 4px 14px rgba(31,95,168,.3)}
          .error-btn.primary:hover{transform:translateY(-2px);box-shadow:0 6px 20px rgba(31,95,168,.4)}
          .error-btn.secondary{background:#fff;color:var(--navy);border:1px solid var(--line)}
          .error-btn.secondary:hover{border-color:var(--blue2);color:var(--blue)}
        </style>
        </head>
        <body>
        <header class="topbar"><div class="wrap">
          <a class="brand" href="/">
            <svg class="logo" viewBox="0 0 200 200"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#16305c"/><stop offset="1" stop-color="#46a8da"/></linearGradient></defs><g fill="url(#g)"><g id="pt"><path d="M100,100 C86,72 88,42 106,22 C114,50 112,80 100,100 Z"/></g><use href="#pt" transform="rotate(45 100 100)"/><use href="#pt" transform="rotate(90 100 100)"/><use href="#pt" transform="rotate(135 100 100)"/><use href="#pt" transform="rotate(180 100 100)"/><use href="#pt" transform="rotate(225 100 100)"/><use href="#pt" transform="rotate(270 100 100)"/><use href="#pt" transform="rotate(315 100 100)"/></g></svg>
        <span class="name">GRUPO <b>VIDANTA</b></span>
        </a></div></header>
        <main class="wrap"><div class="error-page">
        <div class="error-code">404</div>
        <h1 class="error-title">Página no encontrada</h1>
        <p class="error-msg">La página que buscas no existe o fue movida. Verifica la URL o regresa al inicio de la intranet.</p>
        <div class="error-actions">
        <a class="error-btn primary" href="/">🏠 Inicio</a>
        <a class="error-btn secondary" href="javascript:history.back()">← Regresar</a>
        </div></div></main>
        <footer><div class="wrap"><div>© 2026 Grupo Vidanta · Intranet corporativa — <b>uso interno</b></div></div></footer>
        </body></html>
        """;
        await context.Response.WriteAsync(errorHtml);
    }
});

app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
    });

await app.RunAsync();
