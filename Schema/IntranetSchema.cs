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
            await EnsurePlaza();

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

    private async Task EnsurePlaza()
    {
        if (_contentTypeService.Get("plaza") is not null) return;
        var ct = NewType("plaza", "Plaza/Resort", "icon-building color-blue");
        AddProp(ct, TextBox(), "nombre", "Nombre del Resort", true, 1);
        AddProp(ct, TextBox(), "ubicacion", "Ubicación", false, 2);
        AddProp(ct, TextBox(), "descripcion", "Descripción breve", false, 3);
        AddProp(ct, RichText(), "introduccion", "Mensaje de bienvenida", false, 4);
        AddProp(ct, MediaPicker(), "imagen", "Imagen del resort", false, 5);
        AddProp(ct, TextBox(), "color", "Color acento (hex)", false, 6);
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

        var hijos = new[] { "plaza", "areaPage", "aplicacion", "accesoRapido", "contacto", "banner", "seccionHome", "busqueda" };
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
            ("Plaza/Resort", "plaza", PlazaRazor),
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
            var plazaTpl = await _templateService.GetAsync("plaza");
            var children = _contentService.GetPagedChildren(home.Id, 0, 100, out _, (IQuery<IContent>?)null, (Ordering?)null);
            foreach (var ch in children)
            {
                if (ch.ContentType.Alias == "areaPage")
                {
                    ((Content)ch).TemplateId = areaTpl?.Id;
                    _contentService.Save(ch, UserId, null);
                    _contentService.Publish(ch, new[] { "*" }, UserId);
                }
                else if (ch.ContentType.Alias == "plaza")
                {
                    ((Content)ch).TemplateId = plazaTpl?.Id;
                    _contentService.Save(ch, UserId, null);
                    _contentService.Publish(ch, new[] { "*" }, UserId);
                }
            }
        }
        _logger.LogInformation("Todas las plantillas sincronizadas.");
    }

    private const string AvisoRazor =
    """
    @inherits UmbracoViewPage
    @using System.Globalization
    @{
        Layout = "Shared/_Layout.cshtml";
        var ci = new CultureInfo("es-MX");
        var titulo = Model.Value("titulo") as string ?? "";
        var area = Model.Value("area") as string ?? "";
        var etiqueta = Model.Value("etiqueta") as string ?? "";
        var contenido = Model.Value("contenidoTexto") as string ?? "";
        var portada = Model.Value<IPublishedContent>("portada")?.Url();
        var fecha = Model.CreateDate.ToString("dd 'de' MMMM 'de' yyyy", ci);
    }
    <div class="hero-bg"><div class="detail-page">
      <div class="detail-article">
        <div class="breadcrumbs" style="font-size:13px;color:rgba(255,255,255,.45);margin-bottom:12px"><a href="/" style="color:var(--gold)">Inicio</a> / @area / @titulo</div>
        @if(portada!=null){<img src="@portada" alt="@titulo">}
        <div class="detail-tags"><span class="tag-area">@area</span>@if(etiqueta!=""){<span class="tag-label">@etiqueta</span>}</div>
        <h1>@titulo</h1>
        <div class="detail-meta">@fecha</div>
        <div class="detail-content">@Html.Raw(contenido)</div>
      </div>
    </div></div>
    """;

    private const string DocumentoRazor =
    """
    @inherits UmbracoViewPage
    @{
        Layout = "Shared/_Layout.cshtml";
        var titulo = Model.Value("titulo") as string ?? "";
        var area = Model.Value("area") as string ?? "";
        var archivo = Model.Value<IPublishedContent>("archivo");
    }
    <div class="hero-bg"><div class="detail-page">
      <div class="detail-article" style="text-align:center">
        <div style="font-size:48px;margin-bottom:16px">📄</div>
        <h1>@titulo</h1>
        <p style="color:var(--text-tertiary);margin-bottom:20px">@area</p>
        @if(archivo != null){
          <a href="@archivo.Url()" target="_blank" class="detail-download">Descargar archivo</a>
        } else {
          <p style="color:var(--text-tertiary)">No hay archivo adjunto.</p>
        }
      </div>
    </div></div>
    """;

    private const string CollageRazor =
    """
    @inherits UmbracoViewPage
    @{
        Layout = "Shared/_Layout.cshtml";
        var titulo = Model.Value("titulo") as string ?? "";
        var area = Model.Value("area") as string ?? "";
        var fotos = Model.Value<IEnumerable<IPublishedContent>>("fotos");
    }
    <div class="hero-bg"><div class="detail-page">
      <div class="detail-article">
        <div class="breadcrumbs" style="font-size:13px;color:rgba(255,255,255,.45);margin-bottom:12px"><a href="/" style="color:var(--gold)">Inicio</a> / @area / @titulo</div>
        <h1>@titulo</h1>
        <span class="detail-tags"><span class="tag-area">@area</span></span>
        @if(fotos != null && fotos.Any()){
          <div class="gallery-grid">
            @foreach(var foto in fotos){
              <a href="@foto.Url()" target="_blank"><img src="@foto.Url()" alt="@foto.Name" loading="lazy"></a>
            }
          </div>
        } else {
          <div class="empty-state">No hay fotos en este collage.</div>
        }
      </div>
    </div></div>
    """;

    private const string AplicacionRazor =
    """
    @inherits UmbracoViewPage
    @{
        Layout = "Shared/_Layout.cshtml";
        var nombre = Model.Value("nombre") as string ?? Model.Name;
        var descripcion = Model.Value("descripcion") as string ?? "";
        var url = Model.Value("url") as string ?? "#";
        var categoria = Model.Value("categoria") as string ?? "";
        var icono = Model.Value("icono") as string ?? "🔗";
    }
    <div class="hero-bg"><div class="detail-page" style="text-align:center">
      <div class="detail-article" style="text-align:center">
        <div style="font-size:64px;margin-bottom:16px">@icono</div>
        <h1>@nombre</h1>
        @if(categoria!=""){<p style="color:var(--text-tertiary);margin:8px 0">@categoria</p>}
        @if(descripcion!=""){<p style="max-width:500px;margin:12px auto;line-height:1.8;color:var(--text-secondary)">@descripcion</p>}
        <a href="@url" target="_blank" class="detail-download" style="margin-top:10px">Abrir aplicación</a>
      </div>
    </div></div>
    """;

    private const string PlazaRazor = """
@inherits UmbracoViewPage
@using System.Globalization
@{
    Layout = "Shared/_Layout.cshtml";
    var ci = new CultureInfo("es-MX");
    var children = Model.Children().Where(x => x.ContentType.Alias == "areaPage").ToList();
    var avisos = Model.ChildrenOfType("aviso").OrderByDescending(x => x.CreateDate).Take(6).ToList();
    var documentos = Model.ChildrenOfType("documento").OrderByDescending(x => x.CreateDate).Take(6).ToList();
    var collages = Model.ChildrenOfType("collage").OrderByDescending(x => x.CreateDate).Take(6).ToList();
    var apps = Model.Children().Where(x => x.ContentType.Alias == "aplicacion" && (x.Value("url") as string) != "#").ToList();
    var accesos = Model.Children().Where(c => c.ContentType.Alias == "accesoRapido").ToList();
    var intro = Model.Value("introduccion") as string ?? "";
    var nombre = Model.Value("nombre") as string ?? Model.Name;
    var bienvenido = Model.Value("bienvenido") as string ?? "Bienvenido al portal de";
}
<div class="hero-bg">
    <div class="topbar-overlay">
        <div class="greeting">
            <h1>@bienvenido @nombre</h1>
            <p>@(Model.Value("ubicacion") as string ?? "") · @DateTime.Now.ToString("dddd, dd 'de' MMMM 'de' yyyy", ci)</p>
        </div>
        <div class="topbar-actions">
            <div class="search-glass">
                <form action="/busqueda" method="get">
                    <svg class="search-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="M21 21l-4-4"/></svg>
                    <input name="q" placeholder="Buscar…" aria-label="Buscar">
                </form>
            </div>
            <div class="topbar-avatar" title="Perfil">VI</div>
        </div>
    </div>
    <div class="dashboard-grid">
        @if (!string.IsNullOrWhiteSpace(intro)) { <div class="dashboard-row full"><div class="glass-card"><div class="page-sub" style="font-size:15px;color:var(--text-secondary);line-height:1.8">@Html.Raw(intro)</div></div></div> }
        @if (children.Any()) { <div class="dashboard-row"><div class="glass-card"><div class="section-title-glass"><h2>Ãreas</h2></div><div class="content-cards" style="margin-top:14px">@foreach (var child in children) { <a class="content-card" href="@child.Url()"><div class="card-head"><div class="card-icon"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg></div><h3>@child.Name</h3></div><p>Accede a informaciÃ³n y recursos.</p><span class="card-action">Explorar â†’</span></a> }</div></div></div> }
        @if (avisos.Any()) { <div class="dashboard-row"><div class="glass-card"><div class="section-title-glass"><h2>Avisos</h2></div><div class="events-list" style="margin-top:14px">@foreach (var av in avisos) { <a class="event-item" href="@av.Url()"><span class="event-dot gold"></span><div class="event-info"><strong>@(av.Value("titulo") as string ?? av.Name)</strong><span>@(av.Value("area") as string ?? "")</span></div><span class="event-date">@av.CreateDate.ToString("dd MMM", ci)</span></a> }</div></div></div> }
        @if (documentos.Any()) { <div class="dashboard-row"><div class="glass-card"><div class="section-title-glass"><h2>Documentos</h2></div><div class="content-list" style="margin-top:14px">@foreach (var d in documentos) { var archivo = d.Value<IPublishedContent>("archivo"); <a class="content-list-item" href="@(archivo?.Url() ?? d.Url())"><div class="cli-icon">ðŸ“„</div><div class="cli-body"><strong>@(d.Value("titulo") as string ?? d.Name)</strong><small>@(d.Value("area") as string ?? "")</small></div><span class="cli-meta">@d.CreateDate.ToString("dd MMM", ci)</span><span class="cli-action">â†’</span></a> }</div></div></div> }
        @if (apps.Any()) { <div class="dashboard-row full"><div class="section-title-glass"><h2>Aplicaciones</h2></div><div class="apps-grid" style="margin-top:4px">@foreach (var app in apps) { <a class="app-card" href="@(app.Value("url") as string ?? "#")" target="_blank"><span class="app-badge">@(app.Value("categoria") as string ?? "App")</span><div class="app-icon">@(app.Value("icono") as string ?? "ðŸ”—")</div><h3>@(app.Value("nombre") as string ?? app.Name)</h3><p>@(app.Value("descripcion") as string ?? "")</p><span class="app-link">Abrir</span></a> }</div></div> }
        @if (accesos.Any()) { <div class="dashboard-row full"><div class="quick-access glass-card">@foreach (var q in accesos) { <a class="qa-item" href="@(q.Value("url") as string ?? "#")" target="_blank"><span class="qa-icon">@(q.Value("icono") as string ?? "â†’")</span>@(q.Value("nombre") as string ?? q.Name)</a> }</div></div> }
    </div>
</div>
""";

    private const string ErrorRazor =
    """
    @inherits UmbracoViewPage
    @{
        Layout = "Shared/_Layout.cshtml";
    }
    <div class="hero-bg" style="display:flex;align-items:center;justify-content:center;min-height:100vh">
      <div style="text-align:center;padding:40px 24px;max-width:520px">
        <div style="font-size:120px;font-weight:800;background:linear-gradient(135deg,var(--gold),var(--gold-light));-webkit-background-clip:text;-webkit-text-fill-color:transparent;line-height:1;margin-bottom:8px">404</div>
        <h1 style="font-size:24px;color:var(--white);margin-bottom:10px">Página no encontrada</h1>
        <p style="font-size:15px;color:var(--text-secondary);max-width:460px;margin:0 auto 28px;line-height:1.7">La página que buscas no existe o fue movida. Verifica la URL o regresa al inicio de la intranet.</p>
        <div style="display:flex;gap:14px;justify-content:center;flex-wrap:wrap">
          <a href="/" style="display:inline-flex;align-items:center;gap:8px;padding:12px 28px;border-radius:16px;font-weight:600;font-size:14px;background:var(--gold);color:var(--emerald)}">🏠 Inicio</a>
          <a href="javascript:history.back()" style="display:inline-flex;align-items:center;gap:8px;padding:12px 28px;border-radius:16px;font-weight:600;font-size:14px;background:var(--glass-bg);backdrop-filter:blur(20px);border:1px solid var(--glass-border);color:var(--text-secondary)">← Regresar</a>
        </div>
      </div>
    </div>
    """;

    private const string BusquedaRazor =
    """
    @inherits UmbracoViewPage
    @using System.Globalization
    @{
        Layout = "Shared/_Layout.cshtml";
        var ci = new CultureInfo("es-MX");
        var q = Context.Request.Query["q"].ToString();
        var results = new List<dynamic>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var root = Model.Root();
            var searchTerms = q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var allContent = root.Descendants();
            foreach (var item in allContent)
            {
                var title = item.Value("titulo") as string ?? item.Value("nombre") as string ?? item.Name ?? "";
                var area = item.Value("area") as string ?? "";
                var content = item.Value("contenidoTexto") as string ?? item.Value("descripcion") as string ?? "";
                var externalUrl = item.Value("url") as string ?? "";
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
    <div class="hero-bg"><div class="search-page">
      <h1>@(q != "" ? "Resultados para: \"" + q + "\"" : "Escribe algo para buscar")</h1>
      @if (q != "" && results.Any()) { <p class="search-count">@results.Count resultado(s) encontrado(s)</p> }
      @if (results.Any())
      {
        <div class="search-results">
          @foreach (var r in results)
          {
            <a class="content-list-item" href="@r.Url">
              <div class="cli-icon">@(r.Tipo == "aviso" ? "📣" : r.Tipo == "documento" ? "📄" : r.Tipo == "aplicacion" ? "🔗" : "📁")</div>
              <div class="cli-body">
                <strong>@r.Titulo</strong>
                <small>@r.Area · @r.Fecha</small>
              </div>
              <span class="cli-meta" style="text-transform:uppercase;font-size:11px;letter-spacing:.5px">@r.Tipo</span>
            </a>
          }
        </div>
      }
      else if (q != "")
      {
        <div class="empty-state">No se encontraron resultados para "<b>@q</b>". Intenta con otros términos.</div>
      }
      else
      {
        <div class="empty-state">Ingresa un término de búsqueda en la barra superior.</div>
      }
    </div></div>
    """;

    // ---- Razor: cabecera/pie compartidos incrustados en cada plantilla ----

    private const string HomeRazor = """
@inherits UmbracoViewPage
@using System.Globalization
@{
    Layout = "Shared/_Layout.cshtml";
    var ci = new CultureInfo("es-MX");
    var root = Model.Root();
    var allAvisos = root.DescendantsOfType("aviso").OrderByDescending(a => a.CreateDate).Take(6).ToList();
    var allDocs = root.DescendantsOfType("documento").OrderByDescending(d => d.CreateDate).Take(6).ToList();
    var apps = root.DescendantsOfType("aplicacion").Where(a => (a.Value("url") as string) != "#").ToList();
    var accesos = Model.Children().Where(c => c.ContentType.Alias == "accesoRapido").ToList();
    var contactos = Model.Children().Where(c => c.ContentType.Alias == "contacto").ToList();
    var banners = Model.Children().Where(c => c.ContentType.Alias == "banner").OrderBy(b => b.Value("orden") as string ?? "99").ToList();
    var hoy = DateTime.Now;
}
<div class="hero-bg">
    <div class="topbar-overlay">
        <div class="greeting">
            <h1>Bienvenido, Colaborador</h1>
            <p>Sistemas · TI @hoy.ToString("dddd, dd 'de' MMMM 'de' yyyy", ci)</p>
        </div>
        <div class="topbar-actions">
            <div class="search-glass">
                <form action="/busqueda" method="get">
                    <svg class="search-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="7"/><path d="M21 21l-4-4"/></svg>
                    <input name="q" placeholder="Buscar documentos, personas, aplicaciones…" aria-label="Buscar">
                </form>
            </div>
            @if (allAvisos.Any()) { <div class="icon-btn" title="Notificaciones"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg><span class="notif-dot"></span></div> }
            <div class="topbar-avatar" title="Perfil">VI</div>
        </div>
    </div>
    <div class="dashboard-grid">
        @if (banners.Any())
        {
            var slideId = "carousel-" + Model.Id;
            <div class="dashboard-row full">
                <div class="glass-card" style="padding:0;overflow:hidden;position:relative;height:280px" id="@slideId">
                    <div style="display:flex;height:100%;transition:transform .6s ease" class="carousel-track">
                        @for (var i = 0; i < banners.Count; i++)
                        {
                            var b = banners[i];
                            var bTitulo = b.Value("titulo") as string ?? "";
                            var bSubtitulo = b.Value("subtitulo") as string ?? "";
                            <div class="carousel-slide" style="min-width:100%;height:100%;background:url('@(b.Value<IPublishedContent>("imagen")?.Url() ?? "")') center/cover no-repeat;display:flex;align-items:flex-end">
                                <div style="padding:28px 32px;background:linear-gradient(0deg,rgba(8,63,53,.9),transparent);width:100%">
                                    @if (!string.IsNullOrWhiteSpace(bTitulo)) { <h2 style="font-size:26px;color:#fff;margin:0 0 4px">@bTitulo</h2> }
                                    @if (!string.IsNullOrWhiteSpace(bSubtitulo)) { <p style="font-size:14px;color:rgba(255,255,255,.8);margin:0">@bSubtitulo</p> }
                                </div>
                            </div>
                        }
                    </div>
                    @if (banners.Count > 1)
                    {
                        <div style="position:absolute;bottom:16px;right:20px;display:flex;gap:8px;z-index:5">@for (var i = 0; i < banners.Count; i++) { <button class="carousel-dot @(i == 0 ? "active" : "")" onclick="goToSlide('@slideId', @i)"></button> }</div>
                        <button class="carousel-btn prev" onclick="changeSlide('@slideId', -1)">â€¹</button>
                        <button class="carousel-btn next" onclick="changeSlide('@slideId', 1)">â€º</button>
                        <script>(function(){var c=document.getElementById('@slideId');if(!c)return;var t=c.querySelector('.carousel-track');var dots=c.querySelectorAll('.carousel-dot');var idx=0;var total=t.children.length;if(total<=1)return;function go(i){idx=(i+total)%total;t.style.transform='translateX(-'+(idx*100)+'%)';dots.forEach(function(d,j){d.classList.toggle('active',j===idx)})}window.goToSlide=function(id,i){go(i)};window.changeSlide=function(id,d){go(idx+d)};setInterval(function(){go(idx+1)},5000)})();</script>
                    }
                </div>
            </div>
        }
        <div class="dashboard-row">
            <div class="events-panel glass-card">
                <div class="events-header"><h3>Avisos y comunicados</h3></div>
                <div class="events-list">
                    @if (allAvisos.Any()) { @foreach (var av in allAvisos.Take(4)) { <a class="event-item" href="@av.Url()"><span class="event-dot gold"></span><div class="event-info"><strong>@(av.Value("titulo") as string ?? av.Name)</strong><span>@(av.Value("area") as string ?? "")</span></div><span class="event-date">@av.CreateDate.ToString("dd MMM", ci)</span></a> } }
                    else { <div class="empty-state" style="padding:20px">Aún no hay avisos publicados.</div> }
                </div>
            </div>
            <div class="small-cards">
                <div class="weather-card glass-card"><div class="weather-icon">ðŸŒ´</div><div class="weather-temp">28Â°</div><div class="weather-desc">Puerto Vallarta · Soleado</div></div>
                <div class="occupancy-card glass-card"><div class="occupancy-pct">81%</div><div class="occupancy-bar"><div class="fill" style="width:81%"></div></div><div class="occupancy-sub">OcupaciÃ³n · Temporada alta</div></div>
            </div>
        </div>
        <div class="dashboard-row">
            <div class="glass-card">
                <div class="section-title-glass"><h2>ðŸ“‘ Documentos recientes</h2></div>
                @if (allDocs.Any()) { <div class="content-list" style="margin-top:14px">@foreach (var d in allDocs) { <a class="content-list-item" href="@d.Url()"><div class="cli-icon">ðŸ“„</div><div class="cli-body"><strong>@(d.Value("titulo") as string ?? d.Name)</strong><small>@(d.Value("area") as string ?? "")</small></div><span class="cli-meta">@d.CreateDate.ToString("dd MMM", ci)</span><span class="cli-action">â†’</span></a> }</div> }
                else { <div class="empty-state">AÃºn no hay documentos publicados.</div> }
            </div>
            <div class="glass-card">
                <div class="section-title-glass"><h2>ðŸ‘¥ Directorio</h2></div>
                @if (contactos.Any()) { <div class="content-list" style="margin-top:14px">@foreach (var c in contactos) { var cNombre = c.Value("nombre") as string ?? c.Name; var cEmail = c.Value("email") as string ?? ""; var cPuesto = c.Value("puesto") as string ?? ""; var cExtension = c.Value("extension") as string ?? ""; var cIniciales = c.Value("iniciales") as string ?? (cNombre.Length >= 2 ? cNombre.Substring(0,2).ToUpper() : "??"); <a class="content-list-item" href="mailto:@(cEmail)"><div class="cli-icon" style="border-radius:50%">@cIniciales</div><div class="cli-body"><strong>@cNombre</strong><small>@cPuesto@(cExtension != "" ? " · Ext. " + cExtension : "")</small></div></a> }</div> }
                else { <div class="empty-state">No hay contactos en el directorio.</div> }
            </div>
        </div>
        @if (apps.Any()) { <div class="dashboard-row full" id="apps"><div class="section-title-glass"><h2>Aplicaciones</h2></div><div class="apps-grid" style="margin-top:4px">@foreach (var app in apps) { var appUrl = app.Value("url") as string ?? "#"; var appCategoria = app.Value("categoria") as string ?? ""; var appIcono = app.Value("icono") as string ?? "ðŸ”—"; var appNombre = app.Value("nombre") as string ?? app.Name; var appDesc = app.Value("descripcion") as string ?? ""; <a class="app-card" href="@(appUrl)" target="_blank" rel="noopener noreferrer">@if (appCategoria != "") { <span class="app-badge">@appCategoria</span> }<div class="app-icon">@appIcono</div><h3>@appNombre</h3><p>@appDesc</p><span class="app-link">Abrir aplicaciÃ³n</span></a> }</div></div> }
        @if (accesos.Any()) { <div class="dashboard-row full" style="margin-top:auto"><div class="quick-access glass-card">@foreach (var q in accesos) { var qUrl = q.Value("url") as string ?? "#"; var qIcono = q.Value("icono") as string ?? "â†’"; var qNombre = q.Value("nombre") as string ?? q.Name; <a class="qa-item" href="@(qUrl)" target="_blank" rel="noopener noreferrer"><span class="qa-icon">@qIcono</span>@qNombre</a> }</div></div> }
    </div>
</div>
""";

    private const string AreaRazor =
"""
@inherits UmbracoViewPage
@using System.Globalization
@{
    Layout = "Shared/_Layout.cshtml";
    var ci = new CultureInfo("es-MX");
    var children = Model.Children().Where(x => x.ContentType.Alias == "areaPage").ToList();
    var avisos = Model.ChildrenOfType("aviso").OrderByDescending(x => x.CreateDate).ToList();
    var documentos = Model.ChildrenOfType("documento").OrderByDescending(x => x.CreateDate).ToList();
    var collages = Model.ChildrenOfType("collage").OrderByDescending(x => x.CreateDate).ToList();
    var intro = Model.Value("introduccion") as string ?? "";
    var contenido = Model.Value("contenido") as string ?? "";
    var titulo = Model.Value("titulo") as string ?? Model.Name;
}
<div class="hero-bg">
    <div class="content-page">
        <div class="page-header-glass">
            <div class="breadcrumbs"><a href="/">Inicio</a> / @titulo</div>
            <h1>@titulo</h1>
            @if (!string.IsNullOrWhiteSpace(intro)) { <div class="page-sub">@Html.Raw(intro)</div> }
        </div>
        @if (children.Any()) { <div class="section-title-glass"><h2>Ãreas internas</h2></div><div class="content-cards">@foreach (var child in children) { <a class="content-card" href="@child.Url()"><div class="card-head"><div class="card-icon"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg></div><h3>@child.Name</h3></div><p>Accede a informaciÃ³n y recursos del Ã¡rea.</p><span class="card-action">Explorar Ã¡rea â†’</span></a> }</div> }
        @if (!string.IsNullOrWhiteSpace(contenido)) { <div class="section-title-glass"><h2>Contenido</h2></div><div class="glass-card rich-content" style="line-height:1.9;font-size:15px;color:var(--text-secondary)">@Html.Raw(contenido)</div> }
        @if (avisos.Any()) { <div class="section-title-glass"><h2>Avisos y comunicados</h2></div><div class="content-list">@foreach (var aviso in avisos) { <a class="content-list-item" href="@aviso.Url()"><div class="cli-icon">ðŸ“£</div><div class="cli-body"><strong>@(aviso.Value("titulo") as string ?? aviso.Name)</strong><small>@(aviso.Value("area") as string ?? "")@((aviso.Value("etiqueta") as string) != "" ? " Â· " + (aviso.Value("etiqueta") as string) : "")</small></div><span class="cli-meta">@aviso.CreateDate.ToString("dd MMM yyyy", ci)</span><span class="cli-action">â†’</span></a> }</div> }
        @if (documentos.Any()) { <div class="section-title-glass"><h2>Documentos</h2></div><div class="content-list">@foreach (var doc in documentos) { var archivo = doc.Value<IPublishedContent>("archivo"); <a class="content-list-item" href="@(archivo?.Url() ?? doc.Url())" @(archivo != null ? "target=_blank" : "")><div class="cli-icon">ðŸ“„</div><div class="cli-body"><strong>@(doc.Value("titulo") as string ?? doc.Name)</strong><small>@(doc.Value("area") as string ?? "")</small></div><span class="cli-meta">@doc.CreateDate.ToString("dd MMM yyyy", ci)</span><span class="cli-action">â†“</span></a> }</div> }
        @if (collages.Any()) { <div class="section-title-glass"><h2>GalerÃ­a</h2></div><div class="content-cards">@foreach (var col in collages) { var fotos = col.Value<IEnumerable<IPublishedContent>>("fotos"); <a class="content-card" href="@col.Url()"><div class="card-head"><div class="card-icon"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2" ry="2"/><circle cx="8.5" cy="8.5" r="1.5"/><polyline points="21 15 16 10 5 21"/></svg></div><h3>@(col.Value("titulo") as string ?? col.Name)</h3></div>@if (fotos != null) { <p>@fotos.Count() fotografÃ­as</p> }<span class="card-action">Ver galerÃ­a â†’</span></a> }</div> }
        @if (!avisos.Any() && !documentos.Any() && !children.Any() && string.IsNullOrWhiteSpace(contenido) && !collages.Any()) { <div class="empty-state">No hay contenido disponible en esta Ã¡rea.</div> }
    </div>
</div>
""";
}
