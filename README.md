# MS-Forms JSON-to-Binary CLI (FrxEdit)

**FrxEdit** es una herramienta de interfaz de línea de comandos (CLI) diseñada para inspeccionar, extraer, parchar y reconstruir formularios de Microsoft Forms (`.frm` y `.frx`) utilizados nativamente en VBA (Microsoft Office, CorelDraw, etc.).

El objetivo principal de esta herramienta es permitir la manipulación, el control de versiones y la generación dinámica de formularios VBA desde un entorno moderno, utilizando JSON como formato intermedio de intercambio, logrando una **paridad binaria y estructural perfecta** frente al formato propietario `[MS-OFORMS]`.

---

## 🚀 Características Principales

*   **Extracción Perfecta**: Extrae el diseño completo de un archivo `.frm`/`.frx` y lo convierte a un JSON legible y editable.
*   **Reconstrucción Binaria**: Genera de vuelta los binarios `.frx` compatibles y los bloques de diseño `.frm` a partir de un JSON, listos para ser importados en VBA.
*   **Paridad Nativa**: Manejo a nivel de byte de offsets, streams OLE (`f`, `o`, `x`), strings ANSI/Unicode, alineaciones tipográficas, y control de dimensiones dinámicas.
*   **Independiente del Editor VBA**: Permite diseñar o modificar formularios desde cero sin necesidad de abrir MS Excel, Word o CorelDraw.

---

## 🛠️ Instalación y Uso Rápido

Esta herramienta se distribuye como un ejecutable independiente para tu plataforma (Windows, macOS, Linux). Una vez descargado, puedes ejecutarlo desde tu terminal sin necesidad de tener instalado el SDK de .NET.

> *Ejemplo de uso asumiendo que el ejecutable se llama `frxedit`.*

### 1. Inspeccionar / Extraer a JSON
Para leer un formulario existente y extraer sus propiedades a un archivo JSON:
```bash
frxedit inspect mi_formulario.frm --out diseño_extraido.json
```

### 2. Modificar (Parchar)
Puedes modificar el archivo JSON extraído (ej. cambiar textos, colores, tamaños). O crear un "parche" (JSON parcial) con los cambios específicos que quieres aplicar.

### 3. Aplicar Cambios "In-Place"
Si solo deseas sobreescribir el formulario original con los cambios definidos en tu archivo de parche:
```bash
frxedit apply mi_formulario.frm --patch cambios.json
```

### 4. Reconstruir (Rebuild)
Para reconstruir de cero un formulario a partir del original y un parche, guardando el resultado en un archivo nuevo:
```bash
frxedit rebuild mi_formulario.frm --out out/formulario_nuevo.frm --patch cambios.json --stream-mode full-patch
```

### 5. Crear Formulario desde Cero
Crea un archivo nuevo especificando su título y dimensiones, sin requerir un `.frm` previo:
```bash
frxedit create "Nuevo Formulario" --width 400 --height 300 --out out/mi_nuevo_formulario.frm
```

---

## 🗺️ Hoja de Ruta (Roadmap)

### Completado ✅
- [x] Extracción y desensamblado de OLE Streams (`f`, `o`, `x`).
- [x] Parsing de propiedades estructurales y metadatos (`SiteData`, `TextProps`).
- [x] Reconstrucción de `UserForm` y metadatos con paridad estricta.
- [x] Sincronización bidireccional entre propiedades binarias (`.frx`) y de texto (`.frm`).
- [x] Soporte completo de lectura/escritura para controles estándar (`CommandButton`, `TextBox`, `Frame`, `MultiPage`, `ComboBox`, `ListBox`, `CheckBox`, etc.).
- [x] Mapeo de alineación y tipografía completa (Bold, Italic, Tamaños).

### Pendiente (TODO) ⏳
- [ ] **Multimedia**: Deserialización y serialización de flujos multimedia para imágenes (`Picture`) e iconos de ratón (`MouseIcon`).
- [ ] **Alineación de Imágenes**: Manejo avanzado de propiedades visuales ligadas a multimedia (`PictureAlignment`, `PictureSizeMode`).
- [ ] **Tests Automatizados**: Implementar suite de pruebas automatizadas E2E.
- [ ] **Distribución (Release)**: Compilar binarios nativos listos para usar sin dependencias (`--self-contained`) para Windows, Mac y Linux.

---

## 📂 Estructura del Repositorio de Desarrollo

Si eres desarrollador y descargas el código fuente:
*   `src/`: Código fuente principal de la aplicación CLI (C# / .NET 8).
*   `test_data/`: Módulos `.bas` y formularios `.frm`/`.frx` originales y personalizados utilizados para pruebas.
*   `docs/`: Documentación adicional y especificaciones técnicas (ej. `[MS-OFORMS].pdf`).
*   `scripts/`: Scripts auxiliares de desarrollo y utilidades.
