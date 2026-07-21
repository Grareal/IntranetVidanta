# AGENTS.md — Intranet Vidanta

## Proyecto

Intranet corporativa de Grupo Vidanta. CMS Umbraco 18 sobre ASP.NET Core 10 (.NET 10). Base de datos SQL Server. Esquema definido por codigo (code-first via `IntranetSchemaInstaller`).

- **URL app:** `https://localhost:5011/`
- **URL backoffice:** `https://localhost:5011/umbraco`
- **Admin:** `enriquemeza@vidanta.com` / `Vidanta2026!`
- **DB:** `.\SQLEXPRESS` / `IntranetVidanta`
- **Maquina:** NVO03WINSUPTI03

## Stack

| Componente | Version |
|---|---|
| .NET | 10.0 |
| Umbraco CMS | 18.0.2 |
| SQL Server | Express |
| ModelsBuilder | InMemoryAuto (compila en runtime) |
| Auth | OpenIddict (backoffice OAuth/PKCE) |

## Arquitectura

```
IntranetVidanta/
├── Program.cs                          # Minimal API: builder + app
├── Schema/
│   └── IntranetSchema.cs               # INSTALADOR DE ESQUEMA: document types,
│                                       #   contenido, grupos, plantillas.
│                                       #   Se ejecuta al iniciar (UmbracoApplicationStartedNotification).
│                                       #   Es IDEMPOTENTE: verifica existencia antes de crear.
├── Views/
│   ├── _ViewImports.cshtml
│   ├── home.cshtml                     # Template pagina principal
│   ├── areaPage.cshtml                 # Template pagina de area
│   ├── aviso.cshtml                    # Template aviso individual
│   ├── documento.cshtml                # Template documento individual
│   ├── collage.cshtml                  # Template collage individual
│   └── aplicacion.cshtml               # Template aplicacion individual
├── wwwroot/css/intranet.css            # Estilos de toda la intranet
├── appsettings.json                    # Config Umbraco (Global, WebRouting, Imaging)
├── appsettings.Development.json        # Config dev: connection string, unattended install
└── Properties/launchSettings.json      # Perfiles IIS Express y Kestrel
```

## Document Types (definidos en IntranetSchema.cs)

### home
- Alias: `home` | Icono: icon-home | AllowAsRoot: true
- Hijos permitidos: `areaPage`, `aplicacion`, `accesoRapido`
- Propiedades: titulo (TextBox), mensaje (RichText)

### areaPage
- Alias: `areaPage` | Icono: icon-folder
- Hijos permitidos: `aviso`, `documento`, `collage`, `areaPage` (anidado para submenus)
- Propiedades: titulo* (TextBox), introduccion (RichText)

### aviso
- Alias: `aviso` | Icono: icon-megaphone
- Propiedades: titulo* (TextBox), area* (TextBox), etiqueta (TextBox), contenidoTexto (RichText), portada (MediaPicker3)

### documento
- Alias: `documento` | Icono: icon-document
- Propiedades: titulo* (TextBox), area* (TextBox), archivo* (MediaPicker3)

### collage
- Alias: `collage` | Icono: icon-pictures
- Propiedades: titulo* (TextBox), area* (TextBox), layout (TextBox), fotos* (MultipleMediaPicker)

### aplicacion
- Alias: `aplicacion` | Icono: icon-app
- Propiedades: nombre* (TextBox), descripcion (TextBox), url* (TextBox), categoria (TextBox), icono (TextBox)

### accesoRapido
- Alias: `accesoRapido` | Icono: icon-link
- Propiedades: nombre* (TextBox), url* (TextBox), icono (TextBox)

(* = obligatorio)

## Arbol de contenido

```
Inicio (home, id ~1070)
├── Operacion (areaPage)
│   └── [sub-areas anidadas] (areaPage)
├── Recursos Humanos (areaPage)
├── Administracion (areaPage)
├── Mantenimiento (areaPage)
├── Seguridad (areaPage)
├── Aplicaciones (aplicacion, url:#)
│   └── [cada app]
└── [Accesos Rapidos] (accesoRapido)
    └── Outlook, Dynamics, etc.
```

## Grupos de usuarios

| Grupo Alias | Nombre | Start Node | Secciones |
|---|---|---|---|
| admin | Administrators | (todo) | Todo |
| editorOperacion | Editor Operacion | Nodo Operacion | Content, Media |
| editorRh | Editor Recursos Humanos | Nodo RH | Content, Media |
| editorAdmon | Editor Administracion | Nodo Admin | Content, Media |
| editorMantto | Editor Mantenimiento | Nodo Mantenimiento | Content, Media |
| editorSeguridad | Editor Seguridad | Nodo Seguridad | Content, Media |

Permisos de editor: browse(F), create(C), update(A), delete(D), publish(U), sort(S), move(M), copy(O), unpublish(Z), rollback(K).

## Navegacion (nav bar)

La nav se genera dinamicamente desde el contenido:
- Itera `areaPage` hijos de Home
- Si un area tiene hijos `areaPage`, se renderiza como **dropdown hover** (CSS `.nav-dropdown`)
- El area actual se marca con clase `active`
- Los "Accesos Rapidos" se renderizan desde nodos `accesoRapido` hijos de Home
- Las "Aplicaciones" se renderizan desde nodos `aplicacion` hijos de Home (excepto el nodo contenedor "Aplicaciones" que tiene url:#)

## Contenido que se muestra en Home

| Seccion | Fuente | Limite |
|---|---|---|
| Accesos rapidos | Nodos `accesoRapido` hijos de Home | Sin limite |
| Avisos | Todos los `aviso` descendientes de Home | 6 mas recientes |
| Documentos | Todos los `documento` descendientes de Home | 6 mas recientes |
| Aplicaciones | Todos los `aplicacion` descendientes de Home (url != #) | Sin limite |

## CSS

Un solo archivo: `wwwroot/css/intranet.css`. Variables CSS en `:root`:
- `--navy:#16305c` (azul oscuro marca)
- `--blue:#1f5fa8`
- `--teal:#46a8da`
- `--radius:14px`

## Configuracion importante

- `appsettings.json` > `Umbraco:CMS:WebRouting:ApplicationUrlDetection: "None"` (corregido de "Static" que causaba error en save/publish)
- `appsettings.json` > `Umbraco:CMS:Content:AllowEditInvariantFromNonDefault: true`
- `appsettings.Development.json` > Connection string: `Server=.\SQLEXPRESS;Database=IntranetVidanta`

## Tareas comunes

### Agregar un nuevo tipo de contenido
1. Crear metodo `EnsureXxx()` en `IntranetSchema.cs`
2. Llamarlo en `HandleAsync()` despues de los tipos base
3. Si es hijo de un area: agregar alias a `hijos` en `EnsureAreaPage()`
4. Si es hijo de Home: agregar alias a `hijos` en `EnsureHome()`
5. Crear template Razor en `Views/xxx.cshtml`
6. Agregar template al array `templates` en `EnsureTemplates()`

### Modificar la nav o el home
- **Archivos a editar:** `Views/home.cshtml` + `Views/areaPage.cshtml`
- **Tambien:** Las constantes `HomeRazor` y `AreaRazor` en `Schema/IntranetSchema.cs` (son los templates registrados en Umbraco, deben coincidir con los archivos .cshtml)

### Agregar CSS
- Solo en `wwwroot/css/intranet.css`
- Las clases de nav dropdown: `.nav-dropdown`, `.dropdown-menu`

## Errores conocidos

- `NU1903` warnings de paquetes con vulnerabilidades (Microsoft.OpenApi, SQLitePCLRaw) - no críticos
- `CS0618` warnings de APIs obsoletas en Umbraco 18 programadas para removal en Umbraco 19
- La culture `es-MX` no esta configurada en Umbraco (warnings en logs pero no afecta funcionalidad)
