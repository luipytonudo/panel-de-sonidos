# Panel de sonidos para Linux

Misma idea que la versión de Windows: asignas una tecla a cada sonido y suena en tus audífonos y en la llamada.

## Compilar

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o salida
```

Sale un solo archivo, `salida/PanelSonidos`, que no necesita .NET instalado.

```bash
chmod +x PanelSonidos
./PanelSonidos
```

## Uso en Discord

El panel crea un micrófono virtual llamado **PanelSonidos-Microfono**.
En Discord: Ajustes → Voz y video → Dispositivo de entrada → PanelSonidos-Microfono.
Tu voz se mezcla con los sonidos (opción «Mezclar mi voz»).

## Requisitos

- PulseAudio o PipeWire (con `pipewire-pulse`) y el comando `pactl`.
- Teclas globales: funcionan en sesiones X11. En Wayland solo se detectan mientras la ventana enfocada es X11 (Discord lo es).
- Formatos: mp3, wav, ogg (Vorbis y Opus) y opus.
- Configuración y sonidos en `~/.config/panel-sonidos/`.
