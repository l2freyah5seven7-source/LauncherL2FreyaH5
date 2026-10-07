# Ascension: Lineage II High Five

Copia la carpeta completa `L2Launcher` al otro PC. La carpeta incluye el
launcher, el icono y el arte estatico; **el cliente base se descarga aparte**.

Ejecuta `Iniciar L2.bat` o `LineageII.exe`. Abrir la ventana **no** inicia la
descarga. Al principio, el boton dice **ACTUALIZAR**. Pulsa el boton y confirma
que quieres descargar el ZIP oficial de High Five (unos 6 GB) e instalarlo en
la subcarpeta `Client`. Al terminar, el launcher descarga y valida el parche
firmado del servidor antes de dejar el botón listo para jugar.

Al terminar, el boton cambia a **JUGAR** e inicia `Client\system\l2.exe` con
`Client\system` como directorio de trabajo. No modifica otros clientes del PC.
El launcher no sobrescribe una carpeta `Client` que ya contenga archivos:
revisala antes de reintentar una instalacion incompleta.

El arte de Ascension permanece estatico. Particulas de polvo y brasas se
mueven suavemente sobre el fondo, sin tapar los controles. El icono de
Ascension esta incorporado en `LineageII.exe`.

El actualizador consulta los parches firmados de
`l2freyah5seven7-source/ParcheL2Freya`. No edites `launcher.settings.json`:
el launcher rechazara cambios sin una firma valida. El cliente base completo
nunca se publica en GitHub.
Si falla el parche tras instalar el cliente, reintenta con
**ACTUALIZAR PARCHES** o al pulsar **JUGAR**.

Basado en [`PlayerExplains/l2-launcher-custom`](https://github.com/PlayerExplains/l2-launcher-custom/tree/9dd1add9e569cd12dd802984b17c1677a950902f), con licencia MIT incluida en `LICENSE`.
