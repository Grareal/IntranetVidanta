# Intranet Vidanta (Umbraco) — Documento de avance

**Última actualización:** 2026-07-16
**Responsable:** Enrique Meza (Sistemas · TI)
**Objetivo:** Reemplazar la intranet legacy (IIS + ColdFusion 9 + HTML/frameset, gestionada por carpetas compartidas) por un CMS moderno donde cada área publique su contenido con permisos por rol, y las apps existentes se enlacen tal cual para migrarlas después una por una.

---

## 1. Decisión de arquitectura

**Plan elegido: Umbraco CMS sobre .NET (ASP.NET Core), on-premise.**
- Open source (sin licencia de CMS), control total del código y el diseño.
- Reaprovecha el equipo .NET y el SQL Server existentes.
- Puede correr on-premise (datos sensibles no salen a la nube).

---

## 2. Estado actual — 4 de 5 etapas completas

| Etapa | Descripción | Estado |
|---|---|---|
| **1** | Umbraco instalado y arrancando + backoffice (HTTPS) | ✅ |
| **2** | Base de datos en SQL Server (SQLEXPRESS, Windows Auth) | ✅ |
| **3** | Document Types + árbol de contenido + grupos/permisos por área | ✅ |
| **4** | Plantillas Razor + diseño con la paleta de marca (front renderiza) | ✅ |
| **5** | SSO con Entra ID + publicación en IIS (producción) | ⏳ Pendiente |

---

## 3. Entorno técnico

| Componente | Detalle |
|---|---|
| CMS | **Umbraco 18.0.2** (`Umbraco.Templates`) |
| Framework | **.NET 10** (net10.0) |
| Base de datos | **SQL Server 2022 · instancia `.\SQLEXPRESS` · base `IntranetVidanta`** |
| Autenticación BD | Windows / Trusted Connection (`VIDAMEX\adminemeza`) |
| Runtime web | Kestrel (dev) — **requiere HTTPS** (OpenIddict) |

**Prerequisitos ya presentes en el equipo:** .NET SDK 8 y 10, SQL Server (SQLEXPRESS + MSSQLSERVER), Git 2.54, Node v24.

---

## 4. Cómo levantar el sitio (desarrollo)

```powershell
cd "C:\Users\adminemeza\Documents\Rediseno_Intranet\IntranetVidanta"
dotnet run --urls https://localhost:5011
```

| Recurso | URL |
|---|---|
| Sitio público | **https://localhost:5011/** |
| Backoffice (admin) | **https://localhost:5011/umbraco** |
| Página de área (ej.) | https://localhost:5011/operacion |

**Credenciales de administrador (temporales de desarrollo):**
- Usuario: `enriquemeza@vidanta.com`
- Contraseña: `Vidanta2026!`
- ⚠️ Se reemplazan por SSO Entra ID en la Etapa 5.

> **Debe ser `https://`** — el backoffice usa OpenIddict, que rechaza HTTP (error `ID2083`). El certificado de desarrollo ya está confiado (`dotnet dev-certs https --trust`).

---

## 5. Qué se construyó (por código, versionable en git)

Todo el modelo se crea automáticamente al arrancar, mediante un instalador idempotente:
`IntranetVidanta\Schema\IntranetSchema.cs` (un `IComposer` + `INotificationAsyncHandler`).

### Document Types
| Tipo | Uso | Propiedades |
|---|---|---|
| **Home** | Raíz del sitio | título de bienvenida, mensaje |
| **Área** | Página de cada departamento | título, introducción |
| **Aviso** | Comunicado/noticia | título, área, etiqueta, contenido, portada |
| **Documento** | Archivo publicado | título, área, archivo |
| **Collage** | Galería de fotos | título, área, layout, fotos |
| **Aplicación** | Enlace a app externa/interna | nombre, descripción, URL, categoría, icono |

### Árbol de contenido (publicado)
```
Inicio (Banner pricipal con diferentes plazas donde yo pueda seleccionar)
Al entrar tener diferentes Areas o zonas que tenga una barra de seleccion lateral para poder entrar a su departamento 


```

### Grupos de usuarios con permisos por área  
| Grupo | Solo puede ver/editar |
|---|---|
| Editor Operación | → Operación |
| Editor Recursos Humanos | → Recursos Humanos |
| Editor Administración | → Administración |
| Editor Mantenimiento | → Mantenimiento |
| Editor Seguridad | → Seguridad |

En cada area se pueden generar mas campos internos

**Esto cumple el requisito central:** cada área publica lo suyo desde el panel web, sin abrir carpetas de red ni tocar código, y sin poder alterar contenido de otras áreas.

### Diseño / plantillas
- Estilos de marca en `wwwroot/css/intranet.css` (paleta Vidanta: marino → azul → celeste).
- Plantillas Razor `Home` y `Área` con menú dinámico, hero, accesos rápidos, avisos, documentos, directorio y lanzador de aplicaciones.

---

## 6. Entregables en `Documents\Rediseno_Intranet\`

| Archivo | Qué es |
|---|---|
| `IntranetVidanta\` | Proyecto Umbraco real (código fuente) |
| `mockup_intranet_home.html` | Mockup visual estático de la home |
| `prototipo_intranet.html` | Prototipo funcional (usuarios/roles, carga de fotos, collages) |
| `ESTADO_PROYECTO_INTRANET.md` | Este documento |

*(La propuesta comparativa de los 3 planes está en `Desktop\Reporte de apps old\PROPUESTA_REEMPLAZO_INTRANET.md`.)*

---

## 7. Próximos pasos

1. **Etapa 5 — Producción:**
   - SSO con **Microsoft Entra ID** (login corporativo + MFA) para backoffice y front.
   - Publicar en **IIS** con HTTPS y hostname interno; retirar la contraseña de desarrollo.
2. **Contenido:** dar de alta usuarios editores reales por área y curar el contenido vivo (no migrar los ~15k HTML / ~80k imágenes legacy; solo lo que cada área confirme).
3. **Migración por fases (Strangler Fig):** empezar a reescribir las apps ColdFusion 9 y ASP.NET, y **retirar ColdFusion 9** (prioridad de seguridad).
4. **Endurecimiento:** revisar advertencias de dependencias (`NU1903`), respaldos de la BD, política de contraseñas y logs.

---

## 8. Notas técnicas útiles (Umbraco 18 / .NET 10)

- Las APIs de servicios son **async** (`CreateAsync`, `GetAllAsync`, `UpdateAsync`) — los métodos síncronos `Save()`/`GetAll()` ya no existen.
- La **navegación en vistas** (`Children`, `Root`, `DescendantsOfType`) requiere resolver `IDocumentNavigationQueryService` e `IPublishedContentStatusFilteringService` desde `Context.RequestServices`.
- El template de un documento se guarda por versión en `umbracoDocumentVersion.templateId`.
- Base de datos definitiva ya en SQL Server (no SQLite).
