using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

namespace IntranetVidanta.Schema;

/// <summary>
/// Registra el instalador de esquema de la Intranet.
/// Se ejecuta automáticamente porque Program.cs llama a .AddComposers().
/// </summary>
public class IntranetSchemaComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, IntranetSchemaInstaller>();
    }
}

/// <summary>
/// Construye por código (idempotente, versionable en git) el esquema de la intranet:
///  - Document Types: Home, Área, Aviso, Documento, Collage, Aplicación
///  - Árbol de contenido: Inicio > (5 áreas) y nodo Aplicaciones
///  - Grupos de usuarios por área con su "start node" (cada editor solo ve/edita su área)
/// </summary>
public class IntranetSchemaInstaller : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private readonly IContentTypeService _contentTypeService;
    private readonly IDataTypeService _dataTypeService;
    private readonly IContentService _contentService;
    private readonly IUserGroupService _userGroupService;
    private readonly ITemplateService _templateService;
    private readonly IShortStringHelper _shortStringHelper;
    private readonly ILogger<IntranetSchemaInstaller> _logger;

    private List<IDataType> _dataTypes = new();
    private static readonly Guid UserKey = Constants.Security.SuperUserKey;
    private const int UserId = Constants.Security.SuperUserId;

    // Áreas: nombre visible -> alias del grupo de editores
    private static readonly (string Nombre, string GrupoAlias, string Icono)[] Areas =
    {
        ("Operación",        "editorOperacion",  "icon-coffee-cup color-blue"),
        ("Recursos Humanos", "editorRh",         "icon-users color-blue"),
        ("Administración",   "editorAdmon",      "icon-bar-chart color-blue"),
        ("Mantenimiento",    "editorMantto",     "icon-wrench color-blue"),
        ("Seguridad",        "editorSeguridad",  "icon-lock color-blue"),
    };

    // Permisos de editor (letras clásicas de Umbraco): browse, create, update, delete,
    // publish, sort, move, copy, unpublish, rollback.
    private static readonly string[] EditorPerms = { "F", "C", "A", "D", "U", "S", "M", "O", "Z", "K" };

    public IntranetSchemaInstaller(
        IContentTypeService contentTypeService,
        IDataTypeService dataTypeService,
        IContentService contentService,
        IUserGroupService userGroupService,
        ITemplateService templateService,
        IShortStringHelper shortStringHelper,
        ILogger<IntranetSchemaInstaller> logger)
    {
        _contentTypeService = contentTypeService;
        _dataTypeService = dataTypeService;
        _contentService = contentService;
        _userGroupService = userGroupService;
        _templateService = templateService;
        _shortStringHelper = shortStringHelper;
        _logger = logger;
    }

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            _dataTypes = (await _dataTypeService.GetAllAsync()).ToList();

            // 1) Document Types de contenido
            await EnsureAviso();
            await EnsureDocumento();
            await EnsureCollage();
            await EnsureAplicacion();
            await EnsureAccesoRapido();
            await EnsureContacto();
            await EnsureBusqueda();
            await EnsureBanner();
            await EnsureSeccionHome();

            // 2) Document Types de estructura + hijos permitidos
            await EnsureAreaPage();
            await EnsureHome();

            // 3) Árbol de contenido + grupos de usuarios
            await EnsureContentTreeAndGroups();

            // 4) Plantillas Razor (diseño) + asignación al contenido
            await EnsureTemplates();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creando el esquema de la intranet");
        }
    }

    // ============ Helpers ============

    private IDataType? FindDataType(params string[] editorAliases) =>
        editorAliases.SelectMany(a => _dataTypes.Where(d => d.EditorAlias == a)).FirstOrDefault()
        ?? _dataTypes.FirstOrDefault(d => editorAliases.Any(a => d.Name?.InvariantContains(a.Replace("Umbraco.", "")) == true));

    private IDataType? TextBox() => FindDataType("Umbraco.TextBox", "Umbraco.TextArea", "Umbraco.TextBoxLine", "STRING");
    private IDataType? RichText() => FindDataType("Umbraco.RichText", "Umbraco.TinyMce", "Umbraco.TextArea", "Umbraco.TextBox");
    private IDataType? MediaPicker() => FindDataType("Umbraco.MediaPicker3", "Umbraco.MediaPicker", "Umbraco.MediaPicker2");
    private IDataType? MultipleMedia() => FindDataType("Umbraco.MultipleMediaPicker", "Umbraco.MediaPicker3");
    private IDataType? CheckBox() => FindDataType("Umbraco.TrueFalse", "Umbraco.CheckBox");

    private void AddProp(IContentType ct, IDataType? dataType, string alias, string name,
        bool mandatory = false, int sort = 0)
    {
        if (dataType is null)
        {
            _logger.LogWarning("Data type no encontrado para '{Alias}' ({Name}); se omite.", alias, name);
            return;
        }
        if (ct.PropertyTypes.Any(p => p.Alias == alias)) return;
        var prop = new PropertyType(_shortStringHelper, dataType)
        {
            Alias = alias, Name = name, Mandatory = mandatory, SortOrder = sort
        };
        ct.AddPropertyType(prop, "contenido", "Contenido");
    }

    private ContentType NewType(string alias, string name, string icon, bool allowedAsRoot = false) =>
        new ContentType(_shortStringHelper, -1)
        {
            Alias = alias, Name = name, Icon = icon, AllowedAsRoot = allowedAsRoot
        };

    private async Task<IContentType> Persist(ContentType ct)
    {
        await _contentTypeService.CreateAsync(ct, UserKey);
        _logger.LogInformation("Document Type '{Name}' creado.", ct.Name);
        return _contentTypeService.Get(ct.Alias)!;
    }

    // ============ Document Types de contenido ============

    private async Task EnsureAviso()
    {
        if (_contentTypeService.Get("aviso") is not null) return;
        var ct = NewType("aviso", "Aviso", "icon-megaphone color-blue");
        AddProp(ct, TextBox(), "titulo", "Título", true, 1);
        AddProp(ct, TextBox(), "area", "Área", true, 2);
        AddProp(ct, TextBox(), "etiqueta", "Etiqueta", false, 3);
        AddProp(ct, RichText(), "contenidoTexto", "Contenido", false, 4);
        AddProp(ct, MediaPicker(), "portada", "Imagen de portada", false, 5);
        await Persist(ct);
    }

    private async Task EnsureDocumento()
    {
        if (_contentTypeService.Get("documento") is not null) return;
        var ct = NewType("documento", "Documento", "icon-document color-green");
        AddProp(ct, TextBox(), "titulo", "Título", true, 1);
        AddProp(ct, TextBox(), "area", "Área", true, 2);
        AddProp(ct, MediaPicker(), "archivo", "Archivo", true, 3);
        await Persist(ct);
    }

    private async Task EnsureCollage()
    {
        if (_contentTypeService.Get("collage") is not null) return;
        var ct = NewType("collage", "Collage", "icon-pictures color-pink");
        AddProp(ct, TextBox(), "titulo", "Título", true, 1);
        AddProp(ct, TextBox(), "area", "Área", true, 2);
        AddProp(ct, TextBox(), "layout", "Diseño (grid4/strip/feature)", false, 3);
        AddProp(ct, MultipleMedia(), "fotos", "Fotos", true, 4);
        await Persist(ct);
    }

    private async Task EnsureAplicacion()
    {
        if (_contentTypeService.Get("aplicacion") is not null) return;
        var ct = NewType("aplicacion", "Aplicación", "icon-app color-deep-orange");
        AddProp(ct, TextBox(), "nombre", "Nombre", true, 1);
        AddProp(ct, TextBox(), "descripcion", "Descripción", false, 2);
        AddProp(ct, TextBox(), "url", "URL", true, 3);
        AddProp(ct, TextBox(), "categoria", "Categoría", false, 4);
        AddProp(ct, TextBox(), "icono", "Icono (emoji o clase)", false, 5);
        await Persist(ct);
    }

    private async Task EnsureAccesoRapido()
    {
        if (_contentTypeService.Get("accesoRapido") is not null) return;
        var ct = NewType("accesoRapido", "Acceso Rápido", "icon-link color-green");
        AddProp(ct, TextBox(), "nombre", "Nombre", true, 1);
        AddProp(ct, TextBox(), "url", "URL", true, 2);
        AddProp(ct, TextBox(), "icono", "Icono (emoji)", false, 3);
        await Persist(ct);
    }

    private async Task EnsureContacto()
    {
        if (_contentTypeService.Get("contacto") is not null) return;
        var ct = NewType("contacto", "Contacto", "icon-user color-blue");
        AddProp(ct, TextBox(), "nombre", "Nombre completo", true, 1);
        AddProp(ct, TextBox(), "puesto", "Puesto / Departamento", true, 2);
        AddProp(ct, TextBox(), "extension", "Extensión", false, 3);
        AddProp(ct, TextBox(), "email", "Email", false, 4);
        AddProp(ct, TextBox(), "iniciales", "Iniciales (2-3 letras)", false, 5);
        await Persist(ct);
    }

    private async Task EnsureBusqueda()
    {
        if (_contentTypeService.Get("busqueda") is not null) return;
        var ct = NewType("busqueda", "Búsqueda", "icon-search color-green");
        AddProp(ct, TextBox(), "titulo", "Título", false, 1);
        await Persist(ct);
    }

    private async Task EnsureBanner()
    {
        if (_contentTypeService.Get("banner") is not null) return;
        var ct = NewType("banner", "Banner", "icon-picture color-blue");
        AddProp(ct, MediaPicker(), "imagen", "Imagen del banner", true, 1);
        AddProp(ct, TextBox(), "titulo", "Título", false, 2);
        AddProp(ct, TextBox(), "subtitulo", "Subtítulo", false, 3);
        AddProp(ct, TextBox(), "url", "Enlace (URL)", false, 4);
        AddProp(ct, TextBox(), "orden", "Orden (número)", false, 5);
        await Persist(ct);
    }

    private async Task EnsureSeccionHome()
    {
        if (_contentTypeService.Get("seccionHome") is not null) return;
        var ct = NewType("seccionHome", "Sección de Inicio", "icon-settings color-purple");
        AddProp(ct, TextBox(), "titulo", "Título de sección", false, 1);
        AddProp(ct, TextBox(), "tipo", "Tipo (avisos|documentos|directorio|aplicaciones|custom)", true, 2);
        AddProp(ct, TextBox(), "orden", "Orden (número)", false, 3);
        AddProp(ct, CheckBox(), "visible", "Visible", false, 4);
        AddProp(ct, TextBox(), "limite", "Límite de elementos (0=sin límite)", false, 5);
        AddProp(ct, RichText(), "contenidoCustom", "Contenido personalizado (si tipo=custom)", false, 6);
        await Persist(ct);
    }

    // ============ Document Types de estructura ============

    private async Task EnsureAreaPage()
    {
        var ct = _contentTypeService.Get("areaPage");
        if (ct is null)
        {
            var newCt = NewType("areaPage", "Área", "icon-folder color-blue");
            AddProp(newCt, TextBox(), "titulo", "Título", true, 1);
            AddProp(newCt, RichText(), "introduccion", "Introducción", false, 2);
            AddProp(newCt, RichText(), "contenido", "Contenido enriquecido (collages, HTML, imágenes)", false, 3);
            await Persist(newCt);
        }

        ct = _contentTypeService.Get("areaPage")!;
        if (ct.PropertyTypes.All(p => p.Alias != "contenido"))
        {
            AddProp((ContentType)ct, RichText(), "contenido", "Contenido enriquecido (collages, HTML, imágenes)", false, 3);
            await _contentTypeService.UpdateAsync((ContentType)ct, UserKey);
            _logger.LogInformation("areaPage: campo 'contenido' agregado.");
        }

        var hijos = new[] { "aviso", "documento", "collage", "areaPage" };
        var allowed = hijos
            .Select(_contentTypeService.Get)
            .Where(c => c is not null)
            .Select((c, i) => new ContentTypeSort(c!.Key, i, c.Alias))
            .ToList();

        var current = ct.AllowedContentTypes?.ToList() ?? new();
        var needsUpdate = current.Count != allowed.Count;
        if (!needsUpdate)
        {
            for (var i = 0; i < current.Count; i++)
            {
                if (current[i].Alias != allowed[i].Alias)
                { needsUpdate = true; break; }
            }
        }
        if (needsUpdate)
        {
            ((ContentType)ct).AllowedContentTypes = allowed;
            await _contentTypeService.UpdateAsync((ContentType)ct, UserKey);
            _logger.LogInformation("areaPage: hijos permitidos actualizados con {Count} tipos.", allowed.Count);
        }
    }

    private async Task EnsureHome()
    {
        var ct = _contentTypeService.Get("home");
        if (ct is null)
        {
            var newCt = NewType("home", "Home", "icon-home color-blue", allowedAsRoot: true);
            AddProp(newCt, TextBox(), "titulo", "Título de bienvenida", false, 1);
            AddProp(newCt, RichText(), "mensaje", "Mensaje", false, 2);
            await Persist(newCt);
        }

        ct = _contentTypeService.Get("home")!;

        var hijos = new[] { "areaPage", "aplicacion", "accesoRapido", "contacto", "banner", "seccionHome", "busqueda" };
        var allowed = hijos
            .Select(_contentTypeService.Get)
            .Where(c => c is not null)
            .Select((c, i) => new ContentTypeSort(c!.Key, i, c.Alias))
            .ToList();

        var current = ct.AllowedContentTypes?.ToList() ?? new();
        var needsUpdate = current.Count != allowed.Count;
        if (!needsUpdate)
        {
            for (var i = 0; i < current.Count; i++)
            {
                if (current[i].Alias != allowed[i].Alias)
                { needsUpdate = true; break; }
            }
        }
        if (needsUpdate)
        {
            ((ContentType)ct).AllowedContentTypes = allowed;
            await _contentTypeService.UpdateAsync((ContentType)ct, UserKey);
            _logger.LogInformation("home: hijos permitidos actualizados con {Count} tipos.", allowed.Count);
        }
    }

    // ============ Árbol de contenido + grupos ============

    private async Task EnsureContentTreeAndGroups()
    {
        IContent? home = _contentService.GetRootContent().FirstOrDefault(c => c.ContentType.Alias == "home");

        if (home is null)
        {
            home = _contentService.Create("Inicio", Constants.System.Root, "home");
            home.SetValue("titulo", "Bienvenido a la Intranet Vidanta");
            _contentService.Save(home, UserId, null);
            _contentService.Publish(home, new[] { "*" }, UserId);
            _logger.LogInformation("Contenido raíz 'Inicio' creado (id {Id}).", home.Id);
        }

        foreach (var area in Areas)
        {
            var node = _contentService.GetPagedChildren(home.Id, 0, 100, out _, (IQuery<IContent>?)null, (Ordering?)null)
                .FirstOrDefault(c => c.Name == area.Nombre);

            if (node is null)
            {
                node = _contentService.Create(area.Nombre, home.Id, "areaPage");
                node.SetValue("titulo", area.Nombre);
                _contentService.Save(node, UserId, null);
                _contentService.Publish(node, new[] { "*" }, UserId);
                _logger.LogInformation("Área '{Nombre}' creada (id {Id}).", area.Nombre, node.Id);
            }

            await EnsureEditorGroup(area.GrupoAlias, "Editor " + area.Nombre, area.Icono, node.Id);
        }

        var appsNode = _contentService.GetPagedChildren(home.Id, 0, 100, out _, (IQuery<IContent>?)null, (Ordering?)null)
            .FirstOrDefault(c => c.Name == "Aplicaciones");
        if (appsNode is null)
        {
            appsNode = _contentService.Create("Aplicaciones", home.Id, "aplicacion");
            appsNode.SetValue("nombre", "Aplicaciones");
            appsNode.SetValue("url", "#");
            _contentService.Save(appsNode, UserId, null);
            _contentService.Publish(appsNode, new[] { "*" }, UserId);
        }

        var searchNode = _contentService.GetPagedChildren(home.Id, 0, 100, out _, (IQuery<IContent>?)null, (Ordering?)null)
            .FirstOrDefault(c => c.ContentType.Alias == "busqueda");
        if (searchNode is null)
        {
            searchNode = _contentService.Create("Búsqueda", home.Id, "busqueda");
            searchNode.SetValue("titulo", "Búsqueda");
            _contentService.Save(searchNode, UserId, null);
            _contentService.Publish(searchNode, new[] { "*" }, UserId);
        }
    }

    private async Task EnsureEditorGroup(string alias, string name, string icon, int startContentId)
    {
        var existing = await _userGroupService.GetAsync(alias);
        if (existing is not null) return;

        var group = new UserGroup(_shortStringHelper)
        {
            Alias = alias,
            Name = name,
            Icon = icon,
            StartContentId = startContentId,
            HasAccessToAllLanguages = true,
            Permissions = new HashSet<string>(EditorPerms),
        };
        group.ClearAllowedSections();
        group.AddAllowedSection(Constants.Applications.Content);
        group.AddAllowedSection(Constants.Applications.Media);

        var result = await _userGroupService.CreateAsync(group, UserKey, Array.Empty<Guid>());
        if (result.Success)
            _logger.LogInformation("Grupo de usuarios '{Name}' creado (start node {Id}).", name, startContentId);
        else
            _logger.LogWarning("No se pudo crear el grupo '{Name}': {Status}", name, result.Status);
    }

    // ============ Plantillas (diseño) ============

    private async Task EnsureTemplates()
    {
        var templates = new (string Name, string Alias, string Razor)[]
        {
            ("Home", "home", HomeRazor),
            ("Área", "areaPage", AreaRazor),
            ("Aviso", "aviso", AvisoRazor),
            ("Documento", "documento", DocumentoRazor),
            ("Collage", "collage", CollageRazor),
            ("Aplicación", "aplicacion", AplicacionRazor),
            ("Búsqueda", "busqueda", BusquedaRazor),
            ("Error 404", "error", ErrorRazor),
        };

        foreach (var (name, alias, razor) in templates)
        {
            var tpl = await _templateService.GetAsync(alias);
            if (tpl is not null)
            {
                if (tpl.Content != razor) { tpl.Content = razor; await _templateService.UpdateAsync(tpl, UserKey); }
            }
            else
            {
                await _templateService.CreateAsync(name, alias, razor, UserKey, null);
                tpl = await _templateService.GetAsync(alias);
            }

            var ct = _contentTypeService.Get(alias);
            if (ct is not null && tpl is not null)
            {
                var current = ct.AllowedTemplates?.ToList() ?? new();
                if (current.All(t => t.Alias != alias))
                {
                    current.Add(tpl);
                    ct.AllowedTemplates = current;
                }
                ct.SetDefaultTemplate(tpl);
                await _contentTypeService.UpdateAsync(ct, UserKey);
            }
        }

        var homeType = _contentTypeService.Get("home");
        var homeTpl = await _templateService.GetAsync("home");
        if (homeType is not null && homeTpl is not null)
        {
            ((ContentType)homeType).AllowedTemplates = new[] { homeTpl };
            homeType.SetDefaultTemplate(homeTpl);
            await _contentTypeService.UpdateAsync(homeType, UserKey);
        }

        var home = _contentService.GetRootContent().FirstOrDefault(c => c.ContentType.Alias == "home");
        if (home is not null && homeTpl is not null)
        {
            ((Content)home).TemplateId = homeTpl.Id;
            _contentService.Save(home, UserId, null);
            _contentService.Publish(home, new[] { "*" }, UserId);

            var areaTpl = await _templateService.GetAsync("areaPage");
            var children = _contentService.GetPagedChildren(home.Id, 0, 100, out _, (IQuery<IContent>?)null, (Ordering?)null);
            foreach (var ch in children.Where(c => c.ContentType.Alias == "areaPage"))
            {
                ((Content)ch).TemplateId = areaTpl?.Id;
                _contentService.Save(ch, UserId, null);
                _contentService.Publish(ch, new[] { "*" }, UserId);
            }
        }
        _logger.LogInformation("Todas las plantillas sincronizadas.");
    }

    private const string AvisoRazor =
    """
    @inherits UmbracoViewPage
    @using System.Globalization
    @{
        Layout = null;
        var ci = new CultureInfo("es-MX");
        var titulo = Model.Value<string>("titulo") ?? "";
        var area = Model.Value<string>("area") ?? "";
        var etiqueta = Model.Value<string>("etiqueta") ?? "";
        var contenido = Model.Value<string>("contenidoTexto") ?? "";
        var portada = Model.Value<IPublishedContent>("portada")?.Url();
        var fecha = Model.CreateDate.ToString("dd 'de' MMMM 'de' yyyy", ci);
    }
    <!DOCTYPE html><html lang="es"><head>
    <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>@titulo — Intranet Vidanta</title>
    <link rel="stylesheet" href="/css/intranet.css">
    </head><body>
    <header class="topbar"><div class="wrap">
      <a class="brand" href="/"><span class="name">GRUPO <b>VIDANTA</b></span></a>
    </div></header>
    <main class="wrap" style="padding-top:24px">
      <div class="crumbs"><a href="/">Inicio</a> / @area / @titulo</div>
      <article style="background:#fff;border-radius:14px;padding:32px;box-shadow:0 2px 8px rgba(0,0,0,.06);margin-top:16px">
        @if(portada!=null){<img src="@portada" alt="@titulo" style="width:100%;max-height:400px;object-fit:cover;border-radius:10px;margin-bottom:20px">}
        <span style="display:inline-block;background:var(--blue);color:#fff;padding:4px 14px;border-radius:20px;font-size:12px;margin-bottom:12px">@area</span>
        @if(etiqueta!=""){<span style="display:inline-block;background:var(--bg);color:var(--gray);padding:4px 14px;border-radius:20px;font-size:12px;margin-left:8px">@etiqueta</span>}
        <h1 style="margin:14px 0 8px;font-size:26px;color:var(--navy)">@titulo</h1>
        <div style="color:var(--gray);font-size:13px;margin-bottom:18px">@fecha</div>
        <div style="line-height:1.7;font-size:15px">@Html.Raw(contenido)</div>
      </article>
    </main>
    <footer><div class="wrap">© 2026 Grupo Vidanta · Intranet corporativa</div></footer>
    </body></html>
    """;

    private const string DocumentoRazor =
    """
    @inherits UmbracoViewPage
    @{
        Layout = null;
        var titulo = Model.Value<string>("titulo") ?? "";
        var area = Model.Value<string>("area") ?? "";
        var archivo = Model.Value<IPublishedContent>("archivo");
    }
    <!DOCTYPE html><html lang="es"><head>
    <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>@titulo — Intranet Vidanta</title>
    <link rel="stylesheet" href="/css/intranet.css">
    </head><body>
    <header class="topbar"><div class="wrap">
      <a class="brand" href="/"><span class="name">GRUPO <b>VIDANTA</b></span></a>
    </div></header>
    <main class="wrap" style="padding-top:24px">
      <div class="crumbs"><a href="/">Inicio</a> / @area / @titulo</div>
      <div style="background:#fff;border-radius:14px;padding:32px;box-shadow:0 2px 8px rgba(0,0,0,.06);margin-top:16px;text-align:center">
        <div style="font-size:48px;margin-bottom:16px">📄</div>
        <h1 style="font-size:22px;color:var(--navy);margin-bottom:8px">@titulo</h1>
        <p style="color:var(--gray);margin-bottom:20px">@area</p>
        @if(archivo != null){
          <a href="@archivo.Url()" target="_blank" style="display:inline-block;background:var(--blue);color:#fff;padding:12px 32px;border-radius:8px;font-weight:600;text-decoration:none">Descargar archivo</a>
        } else {
          <p style="color:var(--gray)">No hay archivo adjunto.</p>
        }
      </div>
    </main>
    <footer><div class="wrap">© 2026 Grupo Vidanta · Intranet corporativa</div></footer>
    </body></html>
    """;

    private const string CollageRazor =
    """
    @inherits UmbracoViewPage
    @{
        Layout = null;
        var titulo = Model.Value<string>("titulo") ?? "";
        var area = Model.Value<string>("area") ?? "";
        var fotos = Model.Value<IEnumerable<IPublishedContent>>("fotos");
    }
    <!DOCTYPE html><html lang="es"><head>
    <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>@titulo — Intranet Vidanta</title>
    <link rel="stylesheet" href="/css/intranet.css">
    <style>.gallery{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:14px;margin-top:16px}.gallery img{width:100%;border-radius:10px;aspect-ratio:4/3;object-fit:cover}</style>
    </head><body>
    <header class="topbar"><div class="wrap">
      <a class="brand" href="/"><span class="name">GRUPO <b>VIDANTA</b></span></a>
    </div></header>
    <main class="wrap" style="padding-top:24px">
      <div class="crumbs"><a href="/">Inicio</a> / @area / @titulo</div>
      <h1 style="font-size:24px;color:var(--navy);margin:16px 0">@titulo</h1>
      <span style="display:inline-block;background:var(--blue);color:#fff;padding:4px 14px;border-radius:20px;font-size:12px;margin-bottom:16px">@area</span>
      @if(fotos != null && fotos.Any()){
        <div class="gallery">
          @foreach(var foto in fotos){
            <a href="@foto.Url()" target="_blank"><img src="@foto.Url()" alt="@foto.Name" loading="lazy"></a>
          }
        </div>
      } else {
        <div class="empty">No hay fotos en este collage.</div>
      }
    </main>
    <footer><div class="wrap">© 2026 Grupo Vidanta · Intranet corporativa</div></footer>
    </body></html>
    """;

    private const string AplicacionRazor =
    """
    @inherits UmbracoViewPage
    @{
        Layout = null;
        var nombre = Model.Value<string>("nombre") ?? Model.Name;
        var descripcion = Model.Value<string>("descripcion") ?? "";
        var url = Model.Value<string>("url") ?? "#";
        var categoria = Model.Value<string>("categoria") ?? "";
        var icono = Model.Value<string>("icono") ?? "🔗";
    }
    <!DOCTYPE html><html lang="es"><head>
    <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>@nombre — Intranet Vidanta</title>
    <link rel="stylesheet" href="/css/intranet.css">
    </head><body>
    <header class="topbar"><div class="wrap">
      <a class="brand" href="/"><span class="name">GRUPO <b>VIDANTA</b></span></a>
    </div></header>
    <main class="wrap" style="padding-top:24px;text-align:center">
      <div style="font-size:64px;margin:40px 0 16px">@icono</div>
      <h1 style="font-size:26px;color:var(--navy)">@nombre</h1>
      @if(categoria!=""){<p style="color:var(--gray);margin:8px 0">@categoria</p>}
      @if(descripcion!=""){<p style="max-width:500px;margin:12px auto;line-height:1.6">@descripcion</p>}
      <a href="@url" target="_blank" style="display:inline-block;background:var(--blue);color:#fff;padding:12px 32px;border-radius:8px;font-weight:600;margin-top:20px;text-decoration:none">Abrir aplicación</a>
    </main>
    <footer><div class="wrap">© 2026 Grupo Vidanta · Intranet corporativa</div></footer>
    </body></html>
    """;

    private const string ErrorRazor =
    """
    @inherits UmbracoViewPage
    @{
        Layout = null;
    }
    <!DOCTYPE html>
    <html lang="es">
    <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
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
      </a>
    </div></header>
    <main class="wrap">
      <div class="error-page">
        <div class="error-code">404</div>
        <h1 class="error-title">Página no encontrada</h1>
        <p class="error-msg">La página que buscas no existe o fue movida. Verifica la URL o regresa al inicio de la intranet.</p>
        <div class="error-actions">
          <a class="error-btn primary" href="/">🏠 Inicio</a>
          <a class="error-btn secondary" href="javascript:history.back()">← Regresar</a>
        </div>
      </div>
    </main>
    <footer><div class="wrap"><div>© 2026 Grupo Vidanta · Intranet corporativa — <b>uso interno</b></div><div>Service Desk · Extensiones · Ayuda</div></div></footer>
    </body></html>
    """;

    private const string BusquedaRazor =
    """
    @inherits UmbracoViewPage
    @using System.Globalization
    @using Microsoft.Extensions.DependencyInjection
    @using Umbraco.Cms.Core.Services.Navigation
    @{
        Layout = null;
        var Nav = Context.RequestServices.GetRequiredService<IDocumentNavigationQueryService>();
        var Status = Context.RequestServices.GetRequiredService<IPublishedContentStatusFilteringService>();
        var ci = new CultureInfo("es-MX");
        var q = Context.Request.Query["q"].ToString();
        var results = new List<dynamic>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var root = Model.Root(Nav, Status);
            var searchTerms = q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var allContent = root.Descendants(Nav, Status);
            foreach (var item in allContent)
            {
                var title = item.Value<string>("titulo") ?? item.Value<string>("nombre") ?? item.Name ?? "";
                var area = item.Value<string>("area") ?? "";
                var content = item.Value<string>("contenidoTexto") ?? item.Value<string>("descripcion") ?? "";
                var externalUrl = item.Value<string>("url") ?? "";
                var combined = (title + " " + area + " " + content).ToLower();
                if (searchTerms.All(t => combined.Contains(t)))
                {
                    var itemUrl = item.Url();
                    if (item.ContentType.Alias == "aplicacion" && !string.IsNullOrWhiteSpace(externalUrl) && externalUrl != "#")
                        itemUrl = externalUrl;

                    results.Add(new {
                        Titulo = title,
                        Tipo = item.ContentType.Alias,
                        Url = itemUrl,
                        Area = area,
                        Fecha = item.CreateDate.ToString("dd MMM yyyy", ci)
                    });
                }
            }
        }
    }
    <!DOCTYPE html>
    <html lang="es">
    <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Búsqueda@(q != "" ? ": " + q : "") — Intranet Vidanta</title>
    <link rel="stylesheet" href="/css/intranet.css">
    </head>
    <body>
    <header class="topbar"><div class="wrap">
      <a class="brand" href="/">
        <svg class="logo" viewBox="0 0 200 200"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#16305c"/><stop offset="1" stop-color="#46a8da"/></linearGradient></defs><g fill="url(#g)"><g id="pt"><path d="M100,100 C86,72 88,42 106,22 C114,50 112,80 100,100 Z"/></g><use href="#pt" transform="rotate(45 100 100)"/><use href="#pt" transform="rotate(90 100 100)"/><use href="#pt" transform="rotate(135 100 100)"/><use href="#pt" transform="rotate(180 100 100)"/><use href="#pt" transform="rotate(225 100 100)"/><use href="#pt" transform="rotate(270 100 100)"/><use href="#pt" transform="rotate(315 100 100)"/></g></svg>
        <span class="name">GRUPO <b>VIDANTA</b></span>
      </a>
      <div class="search"><form action="/busqueda" method="get" style="display:flex;align-items:center;width:100%"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#333" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="M21 21l-4-4"/></svg><input name="q" value="@q" placeholder="Buscar documentos, personas, aplicaciones…" style="border:none;background:transparent;outline:none;flex:1;height:40px;font-size:14px;padding:0 8px"></form></div>
      <div class="top-actions"><div class="avatar"><div class="pic">VI</div><div class="who">Colaborador<br><small>Grupo Vidanta</small></div></div></div>
    </div></header>
    <nav class="nav"><div class="wrap">
      <a href="/">Inicio</a>
    </div></nav>
    <main class="wrap" style="padding-top:24px">
      <h1 style="font-size:22px;color:var(--navy);margin-bottom:4px">@(q != "" ? "Resultados para: \"" + q + "\"" : "Escribe algo para buscar")</h1>
      @if (q != "" && results.Any()) { <p style="color:#666;margin:0 0 18px">@results.Count resultado(s) encontrado(s)</p> }
      @if (results.Any())
      {
        <div style="display:flex;flex-direction:column;gap:10px">
          @foreach (var r in results)
          {
            <a href="@r.Url" style="display:flex;align-items:center;gap:12px;background:#fff;border:1px solid #e5e7eb;border-radius:10px;padding:12px 16px;text-decoration:none;color:inherit;transition:box-shadow .15s" onmouseover="this.style.boxShadow='0 2px 8px rgba(0,0,0,.08)'" onmouseout="this.style.boxShadow='none'">
              <div style="width:36px;height:36px;border-radius:8px;background:linear-gradient(135deg,#1f5fa8,#46a8da);display:flex;align-items:center;justify-content:center;color:#fff;font-size:13px;font-weight:600;flex-shrink:0">@(r.Tipo == "aviso" ? "📣" : r.Tipo == "documento" ? "📄" : r.Tipo == "aplicacion" ? "🔗" : "📁")</div>
              <div style="flex:1;min-width:0">
                <div style="font-weight:600;font-size:15px;color:var(--navy);white-space:nowrap;overflow:hidden;text-overflow:ellipsis">@r.Titulo</div>
                <div style="font-size:12px;color:#888;margin-top:2px">@r.Area · @r.Fecha</div>
              </div>
              <div style="font-size:11px;color:#aaa;text-transform:uppercase;letter-spacing:.5px">@r.Tipo</div>
            </a>
          }
        </div>
      }
      else if (q != "")
      {
        <div class="empty">No se encontraron resultados para "<b>@q</b>". Intenta con otros términos.</div>
      }
      else
      {
        <div class="empty">Ingresa un término de búsqueda en la barra superior.</div>
      }
    </main>
    <footer><div class="wrap"><div>© 2026 Grupo Vidanta · Intranet corporativa — <b>uso interno</b></div><div>Service Desk · Extensiones · Ayuda</div></div></footer>
    </body></html>
    """;

    // ---- Razor: cabecera/pie compartidos incrustados en cada plantilla ----

    private const string HomeRazor = """
@inherits UmbracoViewPage
@using System.Globalization
@using Microsoft.Extensions.DependencyInjection
@using Umbraco.Cms.Core.Services.Navigation
@{
    Layout = null;
    var Nav = Context.RequestServices.GetRequiredService<IDocumentNavigationQueryService>();
    var Status = Context.RequestServices.GetRequiredService<IPublishedContentStatusFilteringService>();
    var ci = new CultureInfo("es-MX");
    var areas = Model.Children(Nav, Status).Where(c => c.ContentType.Alias == "areaPage")
        .Select(a => new {
            a.Name, Url = a.Url(), a.Id,
            Sub = a.Children(Nav, Status).Where(c => c.ContentType.Alias == "areaPage")
                .Select(c => new { c.Name, Url = c.Url() }).ToList()
        }).ToList();
    var root = Model.Root(Nav, Status);
    var allAvisos = root.DescendantsOfType(Nav, Status, "aviso").OrderByDescending(a => a.CreateDate)
        .Select(av => new {
            Titulo = av.Value<string>("titulo"),
            Area = av.Value<string>("area"),
            Tag = av.Value<string>("etiqueta"),
            ImgUrl = av.Value<IPublishedContent>("portada")?.Url(),
            Fecha = av.CreateDate.ToString("dd MMM yyyy", ci),
            Url = av.Url()
        }).ToList();
    var allDocs = root.DescendantsOfType(Nav, Status, "documento").OrderByDescending(d => d.CreateDate)
        .Select(d => new { Titulo = d.Value<string>("titulo"), Area = d.Value<string>("area") }).ToList();
    var apps = root.DescendantsOfType(Nav, Status, "aplicacion")
        .Where(a => a.Value<string>("url") != "#")
        .Select(app => new {
            Nombre = app.Value<string>("nombre") ?? app.Name,
            Descripcion = app.Value<string>("descripcion") ?? "",
            Url = app.Value<string>("url") ?? "#",
            Categoria = app.Value<string>("categoria") ?? "",
            Icono = app.Value<string>("icono") ?? "🔗"
        }).ToList();
    var accesos = Model.Children(Nav, Status).Where(c => c.ContentType.Alias == "accesoRapido")
        .Select(q => new {
            Nombre = q.Value<string>("nombre") ?? q.Name,
            Url = q.Value<string>("url") ?? "#",
            Icono = q.Value<string>("icono") ?? "🔗"
        }).ToList();
    var contactos = Model.Children(Nav, Status).Where(c => c.ContentType.Alias == "contacto")
        .Select(c => new {
            Nombre = c.Value<string>("nombre") ?? c.Name,
            Puesto = c.Value<string>("puesto") ?? "",
            Extension = c.Value<string>("extension") ?? "",
            Email = c.Value<string>("email") ?? "",
            Iniciales = c.Value<string>("iniciales") ?? "??"
        }).ToList();
    var banners = Model.Children(Nav, Status).Where(c => c.ContentType.Alias == "banner")
        .OrderBy(b => b.Value<string>("orden") ?? "99")
        .Select(b => new {
            ImgUrl = b.Value<IPublishedContent>("imagen")?.Url() ?? "",
            Titulo = b.Value<string>("titulo") ?? "",
            Subtitulo = b.Value<string>("subtitulo") ?? "",
            Url = b.Value<string>("url") ?? "#"
        }).ToList();
    var secciones = Model.Children(Nav, Status).Where(c => c.ContentType.Alias == "seccionHome")
        .OrderBy(s => s.Value<string>("orden") ?? "99")
        .Where(s => s.Value<bool?>("visible") != false)
        .Select(s => new {
            Titulo = s.Value<string>("titulo") ?? "",
            Tipo = (s.Value<string>("tipo") ?? "").ToLower(),
            Limite = int.TryParse(s.Value<string>("limite"), out var lim) ? lim : 6,
            ContenidoCustom = s.Value<string>("contenidoCustom") ?? ""
        }).ToList();
    if (!secciones.Any())
    {
        secciones = new[] {
            new { Titulo = "Avisos y comunicados", Tipo = "avisos", Limite = 6, ContenidoCustom = "" },
            new { Titulo = "", Tipo = "documentos", Limite = 6, ContenidoCustom = "" },
            new { Titulo = "", Tipo = "directorio", Limite = 0, ContenidoCustom = "" },
            new { Titulo = "Aplicaciones", Tipo = "aplicaciones", Limite = 0, ContenidoCustom = "" }
        }.ToList();
    }
}
<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>@(Model.Value<string>("titulo") ?? "Intranet Vidanta")</title>
<link rel="stylesheet" href="/css/intranet.css">
</head>
<body>
<header class="topbar"><div class="wrap">
  <a class="brand" href="/">
    <svg class="logo" viewBox="0 0 200 200"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#16305c"/><stop offset="1" stop-color="#46a8da"/></linearGradient></defs><g fill="url(#g)"><g id="pt"><path d="M100,100 C86,72 88,42 106,22 C114,50 112,80 100,100 Z"/></g><use href="#pt" transform="rotate(45 100 100)"/><use href="#pt" transform="rotate(90 100 100)"/><use href="#pt" transform="rotate(135 100 100)"/><use href="#pt" transform="rotate(180 100 100)"/><use href="#pt" transform="rotate(225 100 100)"/><use href="#pt" transform="rotate(270 100 100)"/><use href="#pt" transform="rotate(315 100 100)"/></g></svg>
    <span class="name">GRUPO <b>VIDANTA</b></span>
  </a>
  <div class="search"><form action="/busqueda" method="get" style="display:flex;align-items:center;width:100%"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#333" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="M21 21l-4-4"/></svg><input name="q" placeholder="Buscar documentos, personas, aplicaciones…" style="border:none;background:transparent;outline:none;flex:1;height:40px;font-size:14px;padding:0 8px"></form></div>
  <div class="top-actions"><div class="avatar"><div class="pic">VI</div><div class="who">Colaborador<br><small>Grupo Vidanta</small></div></div></div>
</div></header>
<nav class="nav"><div class="wrap">
  <a class="active" href="/">Inicio</a>
  @foreach (var a in areas) {
    if (a.Sub.Any()) {
      <div class="nav-dropdown">
        <span>@a.Name</span>
        <div class="dropdown-menu">
          @foreach (var s in a.Sub) { <a href="@s.Url">@s.Name</a> }
        </div>
      </div>
    } else {
      <a href="@a.Url">@a.Name</a>
    }
  }
  <a href="#apps">Aplicaciones</a>
</div></nav>
<main class="wrap">
  @if (banners.Any())
  {
    var slideId = "carousel-" + Model.Id;
    <div class="carousel" id="@slideId">
      <div class="carousel-slides">
        @foreach (var b in banners)
        {
          <div class="carousel-slide" style="background-image:url('@(b.ImgUrl)')">
            <div class="carousel-caption">
              @if (b.Titulo != "") { <h2>@b.Titulo</h2> }
              @if (b.Subtitulo != "") { <p>@b.Subtitulo</p> }
            </div>
          </div>
        }
      </div>
      @if (banners.Count > 1)
      {
        <button class="carousel-btn prev" onclick="carouselGo(this.closest('.carousel'),-1)">&#8249;</button>
        <button class="carousel-btn next" onclick="carouselGo(this.closest('.carousel'),1)">&#8250;</button>
        <div class="carousel-nav">
          @for (var i = 0; i < banners.Count; i++)
          {
            <button class="carousel-dot @(i == 0 ? "active" : "")" onclick="carouselGoTo(this.closest('.carousel'),@i)"></button>
          }
        </div>
      }
    </div>
    <script>
    function carouselGo(c,d){var slides=c.querySelectorAll('.carousel-slide');var dots=c.querySelectorAll('.carousel-dot');var cur=0;for(var i=0;i<slides.length;i++){if(slides[i].classList.contains('active')){cur=i;break}}var next=(cur+d+slides.length)%slides.length;carouselGoTo(c,next)}
    function carouselGoTo(c,idx){var slides=c.querySelectorAll('.carousel-slide');var dots=c.querySelectorAll('.carousel-dot');for(var i=0;i<slides.length;i++){slides[i].classList.remove('active');dots[i].classList.remove('active')}slides[idx].classList.add('active');dots[idx].classList.add('active')}
    (function(){var c=document.getElementById('@slideId');if(!c)return;var slides=c.querySelectorAll('.carousel-slide');if(slides.length<=1)return;slides[0].classList.add('active');var idx=0;setInterval(function(){carouselGo(c,1)},5000)})();
    </script>
  }
  else
  {
    <section class="hero">
      <h1>@(Model.Value<string>("titulo") ?? "Bienvenido a la Intranet Vidanta")</h1>
      <p>@(Model.Value<string>("mensaje") ?? "Tu plataforma de colaboración: avisos, documentos, galería y todas las aplicaciones internas en un solo lugar.")</p>
      <div class="date">@DateTime.Now.ToString("dddd, dd 'de' MMMM 'de' yyyy", ci)</div>
    </section>
  }

  @foreach (var sec in secciones)
  {
    switch (sec.Tipo)
    {
      case "accesos":
        <div class="section-title"><h2>@(sec.Titulo != "" ? sec.Titulo : "Accesos rápidos")</h2></div>
        @if (accesos.Any()) {
          <div class="qa">
            @foreach (var q in accesos) {
              <a href="@q.Url" target="_blank"><div class="icn">@q.Icono</div><span>@q.Nombre</span></a>
            }
          </div>
        } else {
          <div class="empty">No hay accesos rápidos configurados.</div>
        }
        break;

      case "avisos":
        <div class="section-title"><h2>@(sec.Titulo != "" ? sec.Titulo : "Avisos y comunicados")</h2></div>
        var avisosMostrar = sec.Limite > 0 ? allAvisos.Take(sec.Limite).ToList() : allAvisos;
        @if (avisosMostrar.Any()) {
          <div class="news">
          @foreach (var av in avisosMostrar) {
            <a class="item" href="@av.Url">
              <div class="thumb" style="@(av.ImgUrl != null ? "background-image:url('" + av.ImgUrl + "')" : "")">@(av.ImgUrl == null ? "📣" : "")</div>
              <div class="body">
                <span class="tag">@av.Area</span>
                <h3>@av.Titulo</h3>
                <p>@av.Tag</p>
                <div class="meta">@av.Fecha</div>
              </div>
            </a>
          }
          </div>
        } else {
          <div class="empty">Aún no hay avisos publicados.</div>
        }
        break;

      case "documentos":
        var docsMostrar = sec.Limite > 0 ? allDocs.Take(sec.Limite).ToList() : allDocs;
        <div class="panel"><h2>📁 @(sec.Titulo != "" ? sec.Titulo : "Documentos recientes")</h2>
          @if (docsMostrar.Any()) {
            @foreach (var d in docsMostrar) {
              <a class="doc"><div class="ft">DOC</div><div class="info">@d.Titulo<small>@d.Area</small></div></a>
            }
          } else {
            <div class="empty" style="padding:14px">Sin documentos aún.</div>
          }
        </div>
        break;

      case "directorio":
        <div class="panel"><h2>👥 @(sec.Titulo != "" ? sec.Titulo : "Directorio")</h2>
          @if (contactos.Any()) {
            @foreach (var c in contactos) {
              <a class="doc" href="mailto:@c.Email"><div class="ft" style="background:linear-gradient(135deg,#1f5fa8,#46a8da)">@c.Iniciales</div><div class="info">@c.Nombre<small>@c.Puesto@(c.Extension != "" ? " · Ext. " + c.Extension : "")</small></div></a>
            }
          } else {
            <div class="empty" style="padding:14px">No hay contactos en el directorio.</div>
          }
        </div>
        break;

      case "aplicaciones":
        <div class="section-title" id="apps"><h2>@(sec.Titulo != "" ? sec.Titulo : "Aplicaciones")</h2></div>
        @if (apps.Any()) {
          <div class="apps-grid">
            @foreach (var app in apps) {
              <a class="app" href="@app.Url" target="_blank">
                @if (app.Categoria != "") { <span class="badge">@app.Categoria</span> }
                <div class="icn">@app.Icono</div>
                <h3>@app.Nombre</h3>
                <p>@app.Descripcion</p>
              </a>
            }
          </div>
        } else {
          <div class="empty">Aún no hay aplicaciones configuradas.</div>
        }
        break;

      case "custom":
        @if (sec.ContenidoCustom != "") {
          <div class="panel">@Html.Raw(sec.ContenidoCustom)</div>
        }
        break;
    }
  }

  @if (!secciones.Any(s => s.Tipo == "accesos"))
  {
    <div class="section-title"><h2>Accesos rápidos</h2></div>
    @if (accesos.Any()) {
      <div class="qa">
        @foreach (var q in accesos) {
          <a href="@q.Url" target="_blank"><div class="icn">@q.Icono</div><span>@q.Nombre</span></a>
        }
      </div>
    }
  }

  <div class="ribbon">Las aplicaciones internas se enlazan tal como están hoy y se modernizan una por una, sin interrumpir su uso.</div>
</main>
<footer><div class="wrap"><div>© 2026 Grupo Vidanta · Intranet corporativa — <b>uso interno</b></div><div>Service Desk · Extensiones · Ayuda</div></div></footer>
</body>
</html>
""";

    private const string AreaRazor =
"""
@inherits UmbracoViewPage
@using System.Globalization
@using Microsoft.Extensions.DependencyInjection
@using Umbraco.Cms.Core.Services.Navigation
@{
    Layout = null;
    var Nav = Context.RequestServices.GetRequiredService<IDocumentNavigationQueryService>();
    var Status = Context.RequestServices.GetRequiredService<IPublishedContentStatusFilteringService>();
    var ci = new CultureInfo("es-MX");
    var currentId = Model.Id;
    var areas = Model.Root(Nav, Status).Children(Nav, Status).Where(c => c.ContentType.Alias == "areaPage")
        .Select(a => new {
            a.Name, Url = a.Url(), a.Id,
            Sub = a.Children(Nav, Status).Where(c => c.ContentType.Alias == "areaPage")
                .Select(c => new { c.Name, Url = c.Url(), c.Id }).ToList()
        }).ToList();
    var avisos = Model.ChildrenOfType(Nav, Status, "aviso").OrderByDescending(a => a.CreateDate)
        .Select(av => new {
            Titulo = av.Value<string>("titulo"),
            Area = av.Value<string>("area"),
            Tag = av.Value<string>("etiqueta"),
            Fecha = av.CreateDate.ToString("dd MMM yyyy", ci),
            Url = av.Url()
        }).ToList();
    var docs = Model.ChildrenOfType(Nav, Status, "documento").OrderByDescending(d => d.CreateDate)
        .Select(d => new { Titulo = d.Value<string>("titulo") }).ToList();
}
<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>@Model.Name — Intranet Vidanta</title>
<link rel="stylesheet" href="/css/intranet.css">
</head>
<body>
<header class="topbar"><div class="wrap">
  <a class="brand" href="/">
    <svg class="logo" viewBox="0 0 200 200"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#16305c"/><stop offset="1" stop-color="#46a8da"/></linearGradient></defs><g fill="url(#g)"><g id="pt"><path d="M100,100 C86,72 88,42 106,22 C114,50 112,80 100,100 Z"/></g><use href="#pt" transform="rotate(45 100 100)"/><use href="#pt" transform="rotate(90 100 100)"/><use href="#pt" transform="rotate(135 100 100)"/><use href="#pt" transform="rotate(180 100 100)"/><use href="#pt" transform="rotate(225 100 100)"/><use href="#pt" transform="rotate(270 100 100)"/><use href="#pt" transform="rotate(315 100 100)"/></g></svg>
    <span class="name">GRUPO <b>VIDANTA</b></span>
  </a>
  <div class="search"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#333" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="M21 21l-4-4"/></svg><input placeholder="Buscar…"></div>
  <div class="top-actions"><div class="avatar"><div class="pic">VI</div><div class="who">Colaborador<br><small>Grupo Vidanta</small></div></div></div>
</div></header>
<nav class="nav"><div class="wrap">
  <a href="/">Inicio</a>
  @foreach (var a in areas) {
    var isActive = a.Id == currentId || a.Sub.Any(s => s.Id == currentId);
    if (a.Sub.Any()) {
      <div class="nav-dropdown">
        <span class="@(isActive ? "active" : "")">@a.Name</span>
        <div class="dropdown-menu">
          @foreach (var s in a.Sub) { <a href="@s.Url" class="@(s.Id == currentId ? "active" : "")">@s.Name</a> }
        </div>
      </div>
    } else {
      <a class="@(isActive ? "active" : "")" href="@a.Url">@a.Name</a>
    }
  }
</div></nav>
<main class="wrap">
  <div class="crumbs"><a href="/">Inicio</a> / @Model.Name</div>
  <div class="areahead"><h1>@Model.Name</h1>
    @if (Model.Value<string>("introduccion") != "") { <p style="margin-top:8px;opacity:.9">@Html.Raw(Model.Value<string>("introduccion"))</p> }
  </div>

  @if (Model.Value<string>("contenido") != "")
  {
    <div class="panel rich-content" style="margin-top:18px">
      @Html.Raw(Model.Value<string>("contenido"))
    </div>
  }

  <div class="section-title"><h2>Avisos</h2></div>
  @if (avisos.Any())
  {
    <div class="news">
    @foreach (var av in avisos)
    {
      <a class="item" href="@av.Url"><div class="thumb">📣</div><div class="body"><span class="tag">@av.Area</span><h3>@av.Titulo</h3><p>@av.Tag</p><div class="meta">@av.Fecha</div></div></a>
    }
    </div>
  }
  else
  {
    <div class="empty">Aún no hay avisos en esta área.</div>
  }

  <div class="section-title"><h2>Documentos</h2></div>
  <div class="panel">
    @if (docs.Any())
    {
      @foreach (var d in docs)
      {
        <a class="doc"><div class="ft">DOC</div><div class="info">@d.Titulo<small>@Model.Name</small></div></a>
      }
    }
    else
    {
      <div class="empty" style="padding:14px">Sin documentos en esta área.</div>
    }
  </div>
</main>
<footer><div class="wrap"><div>© 2026 Grupo Vidanta · Intranet corporativa — <b>uso interno</b></div><div>Service Desk · Extensiones · Ayuda</div></div></footer>
</body>
</html>
""";
}
