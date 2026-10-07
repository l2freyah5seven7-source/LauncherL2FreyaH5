# Ascension Lineage II Launcher

El launcher conserva el cliente dentro de la subcarpeta `Client\`. Las versiones
listas para probar se publican como ZIP en **Releases** de este repositorio.

Launcher WPF para el cliente Freya/High Five. El cliente base se descarga SOLO
cuando el usuario pulsa `ACTUALIZAR` y acepta la confirmación. El botón cambia
a `JUGAR` después de una instalación correcta. Conserva el layout Canvas/Viewbox,
la ventana personalizada y los controles circulares del proyecto
`PlayerExplains/l2-launcher-custom` (MIT), adaptados al arte de Ascension. El
arte permanece estatico con particulas animadas sutiles de polvo y brasas,
sin video ni movimiento en la imagen de fondo. El panel de estado queda arriba,
a la derecha de «Ascension Realm», fuera del titulo del arte.
Referencia: [`PlayerExplains/l2-launcher-custom` en el commit `9dd1add9`](https://github.com/PlayerExplains/l2-launcher-custom/tree/9dd1add9e569cd12dd802984b17c1677a950902f).

## Requisitos de uso y publicación

- Windows y .NET 8 SDK para compilar.
- Conexión a Internet y espacio libre para descargar y extraer el cliente base.
- Un repositorio **público** separado de GitHub para alojar los Releases de
  parches del cliente (opcional hasta que el repositorio del servidor esté creado).
- GitHub CLI (`gh`) autenticado para publicar un parche.
- Un cliente base que se instala en `Client\` tras la confirmación del usuario.

El launcher consulta el Release más reciente y busca `client-manifest.json`
junto con los ZIP de parches. Comprueba cada archivo con SHA-256 y conserva los
bytes publicados, incluidos los archivos cifrados. Para iniciar el juego usa
`system\l2.exe` con `system` como directorio de trabajo, de forma que el cliente
lea su `L2.ini`.

## Instalación del cliente

El launcher no inicia descargas al abrirse. El botón `ACTUALIZAR` solicita
confirmación antes de descargar el ZIP oficial de High Five desde la dirección
HTTPS de `launcher.settings.json`, valida las rutas del archivo, lo extrae a
una carpeta temporal y lo instala en `Client\` solo cuando encuentra
`Client\system\l2.exe`. Después de instalar, el botón cambia a `JUGAR`. Nunca
sobrescribe una carpeta de cliente ya existente con archivos. La carpeta raíz
conserva `LineageII.exe`, el arte estatico y la configuración.

## Configurar GitHub

Cuando tengas el repositorio **público de parches**, actualiza
`GitHubOwner`/`GitHubRepository` en `launcher.settings.json` y vuelve a firmar
la configuración antes de compilar y publicar una nueva version del launcher.
El actualizador aplicará los Releases verificados mediante SHA-256 a `Client\`.
El launcher no incluye ni almacena tokens.
La configuración y el manifiesto de cada Release también requieren una
firma ECDSA P-256/SHA-256. La clave privada se protege con DPAPI para el
usuario Windows en `%LOCALAPPDATA%\AscensionLauncher` y no se incluye en el
launcher ni en GitHub. Genera la clave una sola vez en el PC del publicador:

```powershell
.\Sign-LauncherAssets.ps1 -InitializeKey
.\Sign-LauncherAssets.ps1 -SignSettings
```

La clave pública se incorpora al build. Después de cambiar ajustes, hay que
firmarlos nuevamente desde el mismo usuario Windows; los parches se firman
automáticamente al publicarlos. Conserva un respaldo seguro de la clave; sin
ella no se podrán publicar nuevas actualizaciones verificables.

Escribe en `patch-files.txt` las rutas exactas que quieras publicar. Revisa el
contenido antes de ejecutar:

```powershell
.\Publish-ClientPatch.ps1 -Repository "usuario/repositorio" -ClientRoot "C:\ruta\al\cliente"
```

El publicador crea un Release con el manifiesto y los ZIP de los archivos
permitidos actuales. Así, un jugador puede actualizar desde cualquier versión
directamente al último Release. Nunca sube el cliente completo y rechaza rutas
como el launcher, el respaldo del cliente, logs y capturas. No elimina archivos
locales cuando un archivo deja de aparecer en un parche.

## Compilar

```powershell
dotnet publish .\L2Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Distribuye el contenido publicado junto con `launcher.settings.json`,
`launcher.settings.json.sig`, `Iniciar L2.bat`, `GUIA-Launcher-Ascension.txt`,
`Publish-ClientPatch.ps1`, `Sign-LauncherAssets.ps1`, `patch-files.txt`,
`Resources\launcher-signing-public.pem` y la carpeta `tools\` del firmador.
Al abrirlo no se descarga
el cliente: `ACTUALIZAR` solicita permiso y el botón cambia a `JUGAR` después
de instalar.

## Visibilidad y firmas

El código fuente de este repositorio es público. Las firmas ECDSA protegen la
integridad de la configuración y de los manifiestos de parches, pero no ocultan
ni impiden copiar, modificar o recompilar el código. La clave privada de firma
debe permanecer fuera de GitHub y del paquete distribuido.
