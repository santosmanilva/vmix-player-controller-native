# vMix Player Controller

Aplicación Windows nativa y portable para operar cuatro players independientes sobre dos equipos vMix y automatizar listas de vídeo desde una ATEM 2 M/E Constellation HD.

No utiliza navegador, WebView, Python ni un servidor local. El ejecutable está construido con WPF y .NET 8, se ejecuta con permisos normales de usuario y no modifica el registro de Windows.

## Funciones

- Cuatro players horizontales con selector de inputs de **vMix A** o **vMix B**.
- Cada vMix se controla de forma independiente; no existe sincronización ni redundancia A/B. vMix B es opcional y, al desactivarlo en Configuración, no aporta entradas a ningún selector.
- Rundown de `VideoList` con AHORA, SIGUIENTE, tiempo restante, avisos de 30/10 segundos, autodesplazamiento y listas virtualizadas. Las filas muestran solo el nombre de archivo, conservando la ruta completa como ayuda emergente, y las cuatro columnas quedan limitadas para caber en 1920×1080.
- Botón único Play/Pausa, restart, primero, anterior, siguiente, GO y búsqueda de posición. El transporte, la posición, el clip seleccionado y Loop reciben feedback continuo del XML de vMix.
- Destinos de **GO** en Mix 1–4: los botones seleccionan a qué salidas se enviará el input al pulsar GO. El punto `●` indica que el input ya está en esa salida según vMix. Desmarcar un destino no retira el input de la salida actual.
- Activadores de buses de audio M/A/B/C/D en cada `VideoList`, con control directo, feedback continuo de las rutas del input e indicador independiente `AUDIO ON/MUTE`.
- Loop, Auto Next, selección directa de clips y atajos F1–F4 para GO. vMix no publica el estado de Auto Next en su API: el botón muestra `?` hasta que el controlador establece expresamente ON u OFF y conserva ese estado durante la sesión.
- Títulos con edición de campos, aplicación conjunta sin parpadeos, presets y cuatro botones Overlay con feedback real: se encienden y apagan siguiendo el estado de vMix.
- Navegación de fuentes de datos Excel, CSV, XML o Google Sheets ya configuradas en vMix.
- Centro de control para grabación, streaming, External, MultiCorder, audio, Outputs 2–4 y presets.
- Perfiles JSON importables/exportables, registro local y paquete de diagnóstico.

## Automatización ATEM

En **Configuración** se introducen las direcciones de vMix A, vMix B y ATEM. En cada player que use una `VideoList`, el selector ATEM aparece al pie de la columna, debajo de la lista de clips, y permite:

1. Seleccionar una entrada física de la ATEM por su nombre.
2. Elegir `M/E 1`, `M/E 2` o cualquiera de los dos.
3. Activar el botón **AUTO**.

Cuando esa entrada pasa a PGM, el player envía `Play` a su lista de vMix. Cuando deja de estar en PGM, envía `Pause`. Las órdenes confirmadas no se repiten mientras el estado no cambie. Si falla una orden, AUTO muestra que queda pendiente y reintenta cada dos segundos mientras recibe estado. Al reconectar vMix, vuelve a comprobar PGM y recupera Play o un Pause pendiente. Al activar AUTO fuera de PGM no se envía una pausa inicial. Sin conexión a alguno de los equipos aparece `AUTO ?` y se suspenden las órdenes.

La conexión ATEM es de solo lectura: el programa vigila nombres de entradas y PGM por Ethernet/UDP, pero no modifica la mesa. No es necesario instalar el SDK de Blackmagic ni ejecutar como administrador.

## Uso

1. Ejecuta `vMix-Player-Controller.exe` desde cualquier carpeta escribible.
2. Abre **CONFIGURACIÓN** e introduce IP y puerto HTTP de vMix A y/o vMix B. El puerto habitual es `8088`.
3. Introduce allí también la IP de la ATEM Constellation y guarda.
4. La aplicación conectará los equipos; después asigna un input a cada player.
5. Activa **AUTO** solo en los players que deban seguir el PGM de la ATEM.

La configuración se guarda junto al ejecutable cuando la carpeta lo permite. Si no es escribible, se guarda en el perfil local del usuario; en ninguno de los casos requiere permisos de administrador.

## Requisitos de red

- Windows 10/11 de 64 bits.
- vMix accesible por HTTP desde el equipo del controlador.
- ATEM accesible en la misma red; su protocolo de control utiliza UDP 9910.
- El firewall debe permitir la comunicación saliente y de respuesta con ambos equipos.

## Compilar las versiones portables

Con .NET SDK 8 instalado:

```text
build_light.bat
```

La versión ligera se crea en `dist-light\vMix-Player-Controller.exe`, ocupa muy poco y requiere **Microsoft .NET 8 Desktop Runtime x64** en el equipo de destino. No requiere instalación del propio controlador ni permisos de administrador.

También se puede generar la edición autocontenida, que incluye .NET completo y funciona aunque el equipo no tenga el runtime:

```text
build_portable.bat
```

Esta edición se crea en `dist\vMix-Player-Controller.exe` y es considerablemente más grande. El repositorio del proyecto es `vmix-player-controller-native`; GitHub Actions genera los artefactos `vMix-Player-Controller-light` y `vMix-Player-Controller-native` en cada actualización de `main` y en las pull requests.

## Comprobaciones internas

La compilación de Windows ejecuta las pruebas en ambas ediciones: XML de vMix, parser y secuencias ATEM, comandos demo, perfiles parciales y recuperación del backup, parámetros de texto vacíos, reintentos de AUTO, cancelación al cambiar de equipo, lotes de órdenes y ResumeRender tras fallo. La prueba de interfaz comprueba Herramientas con un solo vMix, detección de campos editables, sustitución del perfil y selección de clips.

Las pruebas usan demo y transporte HTTP simulado; no certifican una emisión con equipos físicos. La recepción ATEM descarta emisores ajenos, duplicados y paquetes antiguos. Ante un salto de secuencia vuelve a sincronizar la sesión completa: durante esa recuperación AUTO espera un estado válido.

## Cambios de estabilidad · 0.3.13

- Importar un perfil reasigna los cuatro players y valida direcciones, puertos y valores ausentes.
- Guardado mediante archivo temporal y sustitución atómica, con copia `.bak` de la versión anterior.
- Reconexiones serializadas; se cancelan órdenes de la conexión anterior. GO mantiene juntas sus órdenes, aunque un fallo de red puede dejar una ejecución parcial: no existe rollback en vMix.
- Aplicar todos los campos intenta ResumeRender incluso cuando falla SetText. Si la conexión se cancela o ResumeRender también falla, el error requiere comprobar el título en vMix.
- Se permite vaciar un campo; los valores externos se refrescan sin pisar campos editados o con foco. Los atajos no actúan mientras se escribe y se ignora la repetición de tecla.
- Los catálogos de inputs se actualizan al añadir, renombrar o retirar entradas. La lista de clips conserva la selección del operador hasta que cambia el clip en reproducción.
- Las desconexiones muestran SIN DATOS y deshabilitan el transporte. El modo demo protege su estado frente a accesos simultáneos.

Pendiente de validar con hardware: reconexión de ATEM y vMix durante PGM, pérdida de paquetes y recuperación del render de títulos. El diseño de cuatro columnas y el registro en memoria se mantienen; la adaptación a escalado DPI elevado y un registro persistente quedan para otra revisión.
