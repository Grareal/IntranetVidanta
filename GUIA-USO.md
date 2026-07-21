# Guia de Uso - Intranet Vidanta

## Acceso

| Item | Valor |
|---|---|
| URL Intranet | `https://localhost:5011/` |
| URL Backoffice (admin) | `https://localhost:5011/umbraco` |
| Base de datos | SQL Server `.\SQLEXPRESS` / `IntranetVidanta` |

---

## Roles de usuario

### Administrador (Super User)

**Credenciales iniciales:**
- Usuario: `enriquemeza@vidanta.com`
- Contrasena: `Vidanta2026!`

**Permisos:** Acceso total. Puede:
- Crear, editar y eliminar cualquier contenido
- Gestionar tipos de documento y plantillas
- Crear y administrar usuarios y grupos
- Configurar el sistema
- Publicar y despublicar en cualquier area

### Editor de area

Cada area tiene su propio grupo de editores:

| Grupo | Area | Solo puede editar |
|---|---|---|
| Editor Operacion | Operacion | Nodo Operacion y sus hijos |
| Editor Recursos Humanos | Recursos Humanos | Nodo RH y sus hijos |
| Editor Administracion | Administracion | Nodo Administracion y sus hijos |
| Editor Mantenimiento | Mantenimiento | Nodo Mantenimiento y sus hijos |
| Editor Seguridad | Seguridad | Nodo Seguridad y sus hijos |

**Permisos de editor:**
- Crear avisos, documentos y collages dentro de su area
- Editar y eliminar sus propios contenidos
- Publicar y despublicar contenido de su area
- Subir archivos y imagenes a la galeria de medios
- **NO** puede ver ni editar contenido de otras areas
- **NO** puede modificar la estructura del sitio ni la navegacion

### Usuario de visualizacion (colaborador)

- Solo puede **ver** la intranet publicada
- No tiene acceso al backoffice (`/umbraco`)
- Navega por las areas, lee avisos, descarga documentos y accede a aplicaciones

---

## Arbol de contenido

```
Inicio (Home)
 |
 +-- Operacion (Area)
 |    +-- [sub-areas anidadas] (ej: Seguridad Industrial)
 |    +-- Avisos
 |    +-- Documentos
 |    +-- Collages
 |
 +-- Recursos Humanos (Area)
 |    +-- Avisos
 |    +-- Documentos
 |    +-- Collages
 |
 +-- Administracion (Area)
 |    +-- Avisos
 |    +-- Documentos
 |    +-- Collages
 |
 +-- Mantenimiento (Area)
 |    +-- Avisos
 |    +-- Documentos
 |    +-- Collages
 |
 +-- Seguridad (Area)
 |    +-- Avisos
 |    +-- Documentos
 |    +-- Collages
 |
 +-- Aplicaciones (nodo contenedor)
 |    +-- [cada aplicacion]
 |
 +-- Accesos Rapidos
      +-- Outlook
      +-- Dynamics 365
      +-- Moper
      +-- Optii
      +-- WIFI
      +-- Extensiones
```

---

## Tipos de contenido

### Aviso

Noticias, comunicados, circulares.

| Campo | Tipo | Obligatorio | Descripcion |
|---|---|---|---|
| Titulo | Texto | Si | Titulo del aviso |
| Area | Texto | Si | Area que publica (ej: Operacion) |
| Etiqueta | Texto | No | Etiqueta categorica (ej: Urgente, Informativo) |
| Contenido | Texto enriquecido | No | Cuerpo del aviso con formato |
| Imagen de portada | Selector de medios | No | Foto que acompana el aviso |

**Donde aparece:** Seccion "Avisos y comunicados" en la pagina de Inicio y en la pagina de cada area.

### Documento

Archivos descargables (PDF, Word, Excel, etc.).

| Campo | Tipo | Obligatorio | Descripcion |
|---|---|---|---|
| Titulo | Texto | Si | Nombre del documento |
| Area | Texto | Si | Area que publica |
| Archivo | Selector de medios | Si | Archivo a descargar (PDF, DOCX, XLSX, etc.) |

**Donde aparece:** Seccion "Documentos recientes" en Inicio y dentro de cada area.

### Collage de fotos

Galeria de imagenes.

| Campo | Tipo | Obligatorio | Descripcion |
|---|---|---|---|
| Titulo | Texto | Si | Titulo de la galeria |
| Area | Texto | Si | Area que publica |
| Diseno | Texto | No | Tipo de layout: `grid4`, `strip`, `feature` |
| Fotos | Multiples medios | Si | Varias imagenes para la galeria |

**Donde aparece:** Como pagina individual dentro de cada area.

### Aplicacion

Enlaces a apps internas o externas.

| Campo | Tipo | Obligatorio | Descripcion |
|---|---|---|---|
| Nombre | Texto | Si | Nombre de la aplicacion |
| Descripcion | Texto | No | Breve descripcion |
| URL | Texto | Si | Direccion de la app |
| Categoria | Texto | No | Ej: Externa, Interna, Reportes |
| Icono | Texto | No | Emoji o clase de icono |

**Donde aparece:** Seccion "Aplicaciones" en la pagina de Inicio.

### Acceso Rapido

Enlaces cortos en la parte superior del Inicio (Outlook, Dynamics, etc.).

| Campo | Tipo | Obligatorio | Descripcion |
|---|---|---|---|
| Nombre | Texto | Si | Nombre del acceso (ej: Outlook) |
| URL | Texto | Si | Direccion del enlace |
| Icono | Texto | No | Emoji (ej: 📧) |

**Donde aparece:** Seccion "Accesos rapidos" en la pagina de Inicio.

### Area (AreaPage)

Nodo de estructura que funciona como contenedor y pagina de area.

| Campo | Tipo | Obligatorio | Descripcion |
|---|---|---|---|
| Titulo | Texto | Si | Nombre del area |
| Introduccion | Texto enriquecido | No | Texto de bienvenida del area |

**Permite como hijos:** Avisos, Documentos, Collages y sub-Areas (para submenus desplegables).

---

## Como crear contenido

### 1. Crear un aviso

1. Ir a `https://localhost:5011/umbraco`
2. Iniciar sesion (solo editores o admin)
3. En el arbol de contenido, expandir **Inicio** y seleccionar tu area (ej: Operacion)
4. Click derecho sobre el area > **Crear** > **Aviso**
5. Llenar los campos:
   - Titulo: "Reunion de seguridad"
   - Area: "Operacion"
   - Etiqueta: "Urgente" (opcional)
   - Contenido: escribir el texto del aviso
   - Imagen de portada: arrastrar o seleccionar una imagen (opcional)
6. Click en **Salvar y publicar** (icono verde check)

El aviso aparecera automaticamente en la pagina de Inicio (seccion "Avisos y comunicados") y en la pagina del area.

### 2. Crear un documento

1. Click derecho sobre tu area > **Crear** > **Documento**
2. Llenar los campos:
   - Titulo: "Politica de seguridad 2026"
   - Area: "Operacion"
   - Archivo: arrastrar el PDF o documento
3. **Salvar y publicar**

### 3. Crear un collage de fotos

1. Click derecho sobre tu area > **Crear** > **Collage**
2. Llenar los campos:
   - Titulo: "Evento team building"
   - Area: "Operacion"
   - Diseno: "grid4" (opcional)
   - Fotos: seleccionar multiples imagenes
3. **Salvar y publicar**

### 4. Crear una aplicacion

1. Expandir **Inicio** > **Aplicaciones**
2. Click derecho sobre **Aplicaciones** > **Crear** > **Aplicacion**
3. Llenar los campos:
   - Nombre: "SharePoint 365"
   - URL: "https://sharepoint.vidanta.com"
   - Descripcion: "Documentos y sitios"
   - Categoria: "Externa"
   - Icono: "📁"
4. **Salvar y publicar**

### 5. Crear un acceso rapido

1. Expandir **Inicio**
2. Click derecho sobre **Inicio** > **Crear** > **Acceso Rapido**
3. Llenar los campos:
   - Nombre: "Outlook"
   - URL: "https://outlook.office.com"
   - Icono: "📧"
4. **Salvar y publicar**

### 6. Crear sub-areas (submenus desplegables)

Para crear un submenu dentro de un area (ej: Operacion > Seguridad Industrial):

1. Click derecho sobre **Operacion** > **Crear** > **Area**
2. Poner nombre: "Seguridad Industrial"
3. **Salvar y publicar**

Al pasar el mouse sobre "Operacion" en la navegacion, aparecera un dropdown con las sub-areas.

---

## Gestionar usuarios

### Crear un usuario nuevo

1. Ir a **Usuarios** en el menu lateral del backoffice
2. Click en **Crear usuario**
3. Llenar: nombre, email, contrasena
4. Asignar al **grupo** correspondiente (ej: Editor Operacion)
5. Guardar

### Grupos disponibles

| Grupo | Acceso | Que puede hacer |
|---|---|---|
| Administrators | Todo | Control total del sistema |
| Editor Operacion | Solo area Operacion | Crear/editar/publicar avisos, documentos, collages |
| Editor Recursos Humanos | Solo area RH | Crear/editar/publicar avisos, documentos, collages |
| Editor Administracion | Solo area Administracion | Crear/editar/publicar avisos, documentos, collages |
| Editor Mantenimiento | Solo area Mantenimiento | Crear/editar/publicar avisos, documentos, collages |
| Editor Seguridad | Solo area Seguridad | Crear/editar/publicar avisos, documentos, collages |

---

## Gestionar medios (archivos e imagenes)

1. Ir a **Medios** en el menu lateral del backoffice
2. Crear carpetas organizadas (ej: "Avisos 2026", "Documentos RH")
3. Subir archivos arrastrando o seleccionando
4. Los medios se pueden reutilizar en avisos, documentos y collages

---

## Publicar y despublicar

### Publicar contenido
1. Seleccionar el nodo en el arbol
2. Click en **Salvar y publicar** (icono verde check)
3. Seleccionar idiomas (default: todos)
4. Confirmar

### Despublicar (retirar)
1. Seleccionar el nodo publicado
2. Click en **Despublicar** (icono de ojo tachado)
3. El contenido deja de verse en el sitio pero se mantiene en el backoffice

### Editar contenido existente
1. Seleccionar el nodo
2. Modificar los campos necesarios
3. **Salvar y publicar** para que los cambios se reflejen en el sitio

---

## Estructura de la navegacion

```
[GRUPO VIDANTA]  [Buscar...]  [Avatar]

[Inicio] [Operacion ▾] [Recursos Humanos ▾] [Administracion ▾] [Mantenimiento ▾] [Seguridad ▾] [Aplicaciones]
```

- **Inicio**: Pagina principal con hero, accesos rapidos, avisos recientes, documentos y apps
- **Areas**: Pagina de cada area con avisos y documentos propios
- **Submenus**: Hover sobre un area muestra sus sub-areas como dropdown
- **Aplicaciones**: Scroll a la seccion de apps en Inicio

---

## Comportamiento automatico

La intranet esta disenada para que el contenido fluya automaticamente:

- **Avisos** publicados en cualquier area aparecen en la pagina de Inicio (los 6 mas recientes)
- **Documentos** publicados aparecen en "Documentos recientes" en Inicio
- **Aplicaciones** creadas bajo el nodo "Aplicaciones" aparecen en la grid de apps
- **Sub-areas** creadas dentro de un area aparecen como dropdown en la navegacion
- **Collages** son paginas independientes accesibles desde el area correspondiente

No es necesario configurar nada adicional. El contenido se renderiza dinamicamente segun lo que se publique en el backoffice.
