<h1 align="center">AION2 DPS Meter — el fork de akunito</h1>

<p align="center">
  Un medidor de DPS de código abierto para <b>AION 2 Global (EU)</b>, en Windows y Linux.<br>
  DPS del grupo, vida del jefe, desglose de habilidades y temporizadores de jefes encima del juego — y además un mapa de nodos, rankings de DPS, avisos de precios del mercado, crafteo, builds y tus objetos en todos tus personajes.
</p>

<p align="center">
  <a href="https://github.com/akunito/aion2-meter-releases/releases/latest"><img alt="Descargar la última versión" src="https://img.shields.io/github/v/release/akunito/aion2-meter-releases?label=descargar&color=c9a45c&style=for-the-badge"></a>
  <img alt="Windows 10 / 11, 64 bits" src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-2b3a67?style=for-the-badge">
  <img alt="Linux x64, Wayland" src="https://img.shields.io/badge/Linux-x64%20%C2%B7%20Wayland-2b3a67?style=for-the-badge">
  <a href="LICENSE"><img alt="Licencia: GPL-3.0" src="https://img.shields.io/badge/licencia-GPL--3.0-3dd68c?style=for-the-badge"></a>
</p>

<p align="center"><a href="README.md">English</a> · <b>🇪🇸 Español</b></p>

> **Hecho sobre [cyberbadger6969/aion2-dps-meter](https://github.com/cyberbadger6969/aion2-dps-meter).** Esto es un fork y el núcleo es suyo: la captura, la decodificación del protocolo, el motor de combate, el overlay, el desglose, el historial de combates y los temporizadores de jefes son obra de cyberbadger6969, y este fork no los reescribe — sigue las versiones del original y añade encima sus pestañas y la versión de Linux. Las tablas de datos del juego del original vienen de [taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter) y [Kuroukihime/AIon2-Dps-Meter](https://github.com/Kuroukihime/AIon2-Dps-Meter). Si el meter te resulta útil, dale una estrella y apoya también [al proyecto original](https://github.com/cyberbadger6969/aion2-dps-meter).

<p align="center">
  <img src="docs/img/overlay.png" width="460" alt="El overlay durante un combate contra un jefe: una fila por jugador con GS, CP, DPS, daño y porcentaje">
</p>

> Los grupos, combates, precios vigilados y rankings de estas imágenes son inventados por la herramienta de render del propio meter. Las imágenes de Build e Items usan un build público real de questlog.gg y los personajes del propio mantenedor.

La interfaz del meter se puede poner en español (chip EN/ES del overlay o *Settings → Language*), pero esta página cita los botones y menús en inglés, tal como aparecen con la interfaz en inglés. Las páginas de ayuda de cada pestaña también están en español.

## Qué es

Este repositorio guarda el **código fuente** del fork y sus **versiones publicadas**. El mantenedor (akunito) lo empezó como una build auditada para su clan — el código del original leído commit a commit, con pestañas extra — y lo abrió cuando la versión de Linux estuvo lista. Cada versión se compila desde este código en un contenedor limpio (`build/build.sh`), todos los tests tienen que pasar antes de crear el archivo, y cada archivo lleva `BUILD-INFO.txt` (el commit exacto del código) y `SHA256SUMS.txt`.

La captura es **pasiva**, igual que en el original: el meter lee el tráfico de red que el servidor del juego manda a tu PC (con Npcap en Windows, con libpcap en Linux). Nunca envía nada al juego, nunca lee ni escribe la memoria del juego y no inyecta nada. Aun así, cualquier herramienta externa puede ir contra las normas del juego: úsalo bajo tu responsabilidad.

Licencia: **GPL-3.0**, la misma que el original (`LICENSE` está en el repositorio y en cada archivo).

## Descarga

**[⬇ Última versión](https://github.com/akunito/aion2-meter-releases/releases/latest)** — dos archivos por versión:

- **Windows**: `AION2DpsMeter-v<versión>-win-x64.zip` (unos 75 MB). No hay instalador: descomprimir y ejecutar. .NET va incluido. Dentro, en una carpeta `AION2DpsMeter`: `AION2DpsMeter.exe`, la carpeta `data` que necesita a su lado, `INSTALL.txt`, `LICENSE`, `BUILD-INFO.txt` y `SHA256SUMS.txt`.
- **Linux**: `AION2DpsMeter-v<versión>-linux-x64.tar.gz`. El daemon de captura, la tarjeta GTK 4 y un `install.sh`; ver [Linux](#linux).

Las **pre-releases** son el canal de desarrollo. No las uses salvo que quieras probar una.

## Funciones

Del original:

- **Overlay en vivo** — DPS, daño y porcentaje de cada jugador y la vida del jefe, cinco veces por segundo. Colores y emblemas de clase, tu fila resaltada, puntuación de equipo (GS) y poder de combate (CP) tuyos y de tu grupo.
- **Aparece cuando peleas** y se esconde 60 s después; se puede fijar, hacer que los clics lo atraviesen y cambiar la opacidad. Nunca le quita el foco al juego.
- **Desglose completo** — clic en un jugador: línea temporal de DPS frente al grupo, cada habilidad con golpes, daño, DPS, media, golpe máximo, críticos y porcentaje; pestañas de precisión (crítico, espalda, frente, perfecto, smite) y de rotación (cada cast en orden); daño recibido.
- **Historial de combates** — los combates con jefe y los largos se guardan solos y se pueden recorrer en el propio overlay.
- **Temporizadores de jefes de campo sacados del juego** — abre una vez la lista de jefes de campo en el mapa del juego y el meter lleva la cuenta atrás de cada uno, por servidor, con aviso antes de que vuelva un jefe que vigilas.
- **Límite de tiempo de instancia y de jefe** en la cabecera (⏱ 46:52), en rojo el último minuto.
- **Compartir en el chat** — una línea para el chat del juego o una tabla para Discord.
- **Jefes de mundo con multitud** — cuando el servidor deja de mandar el daño de casi todos, el meter lo dice ("133 attacking · damage of 3 sent") en vez de enseñar números falsos.

Añadido en este fork:

- **Pestañas dentro del overlay** — Meter · Atlas · Top DPS · Prices · Craft · Build · Items · Boss Timers · Settings, y un botón para abrir cualquier pestaña en una segunda tarjeta (el mapa en un monitor y el medidor en otro).
- **Interfaz en inglés o en español** — el chip EN/ES del overlay o *Settings → Language*; los nombres de objetos, habilidades y monstruos pueden seguir a la interfaz o ir en su propio idioma.
- **Ficha de jugador** — el desglose del combate y Top DPS abren una ficha por jugador: el combate y además su perfil oficial (equipo, habilidades, Daevanion).
- **Atlas** — mapas calibrados de Verteron, Altgard y el Abismo inferior contigo, los nodos de recolección vivos y recordados (unidades que quedan, reaparición aprendida), jefes de campo, las cinco mejores zonas de cada material y los puntos de las mascotas que aún no tienes al máximo.
- **Top DPS** — los diez mejores DPS por clase de tus propios combates con reparto por habilidad, apertura, secuencias repetidas y casts por minuto; los rankings de poder de combate del servidor y de Europa.
- **Prices** — vigila objetos en aion2.exchange: suena un aviso cuando el anuncio más barato baja a tu precio (o sube a tu precio de venta), con gráficos de 24 h / 7 d / 30 d.
- **Craft** — lo que puedes fabricar ahora con tu bolsa y almacenes, lo que casi puedes (te faltan 1–2 materiales), favoritas, y una calculadora de beneficio contra el mercado.
- **Build** — tu build de habilidades con la maqueta del planificador de questlog.gg y un planificador de Daevanion, comparados con la ficha oficial de tu personaje; importar por enlace o búsqueda, tus propios builds de questlog, notas por habilidad y relaciones automáticas entre habilidades.
- **Items** — bolsa, almacén del personaje, almacén compartido de la cuenta y equipo puesto, con un buscador entre todos tus personajes.
- **Pastillas de eventos** — cuenta atrás de Shugo, Invasion, Rift, Kaira, Siege y Reset con aviso sonoro.
- **Selector de personaje** — mira los objetos, builds y crafteos de otro personaje, incluso mientras juegas.
- **Región y servidor** — *Settings → Game* (*Auto* desde el login); precios, rankings, fichas y temporizadores siguen al servidor elegido.
- **Ayuda en cada pestaña** — el botón **?** abre una página de esa pestaña, en inglés o en español.
- **Se actualiza solo** desde este repositorio en Windows (canal estable o de desarrollo) y **arranca con el juego** si quieres.
- **Funciona sin Npcap** — todo menos el DPS en vivo sigue funcionando, y la captura arranca sola en cuanto instalas Npcap.
- **Recibir de otro meter** — un meter que ve el juego puede reenviar sus paquetes a un meter en otro PC, en una máquina virtual o bajo Wine (*Settings → Capture*).
- **Versión de Linux** — un daemon de captura y una tarjeta GTK 4 nativa con todas las pestañas; ver [Linux](#linux).

## Requisitos

- Windows 10 u 11 de 64 bits — o Linux x86_64 con Wayland (ver [Linux](#linux)).
- AION 2 en servidores Global, EU (Steam o el launcher de NC). Este fork se hace y se prueba en EU.
- Windows: [Npcap](https://npcap.com/#download), el driver de captura gratuito que usa Wireshark (se instala una vez).
- Unos 100 MB de disco.

## Instalación (Windows)

1. **Instala [Npcap](https://npcap.com/#download).** Descarga "Npcap 1.xx installer for Windows", ábrelo, acepta la licencia y deja todas las opciones como vienen (así también captura el tráfico que pasa por ExitLag y otros aceleradores de ping). Es el único paso que pide permiso de administrador. Sáltalo si ya tienes Npcap (por ejemplo, por Wireshark).
2. **Descomprime el meter en una carpeta tuya**, por ejemplo `Documentos\AION2DpsMeter`: clic derecho en el zip → *Extraer todo…*. `AION2DpsMeter.exe` y la carpeta `data` tienen que quedar juntos. No lo abras desde dentro del zip.
3. **Abre `AION2DpsMeter.exe`.** La primera vez Windows puede decir *"Windows protegió su PC"*: pulsa *Más información* → *Ejecutar de todas formas*. Sale porque la build no está firmada con un certificado de pago; solo pasa la primera vez.

### Primer arranque

- Aparece una **A** dorada en la zona de notificaciones, junto al reloj (pulsa la flechita **^** si está oculta). Pon el ratón encima: *"Waiting for the game to connect…"* es que el meter está listo y espera al juego; *"Capturing · …"* es que ya está leyendo el juego.
- El overlay sale unos segundos y luego se esconde hasta que pelees. <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> lo vuelve a mostrar.
- **Abre el meter antes de entrar con tu personaje**, y siempre antes de entrar en una mazmorra: los nombres de los jugadores llegan en las pantallas de carga, y tus mascotas, inventario y títulos al entrar. Los jugadores que ya estaban alrededor salen como `Templar #12345` hasta la siguiente pantalla de carga.
- Para cerrar el meter: clic derecho en la **A** dorada → *Exit*. La X y <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> solo esconden la ventana.

Ajustes, combates, builds y todo lo demás se guardan en `%AppData%\AionMeter` y sobreviven a las actualizaciones.

## Cómo se usa

| | |
|---|---|
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> | mostrar / ocultar el overlay |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>R</kbd> o ↻ | reiniciar el medidor si los números parecen raros: guarda el combate hasta ese momento y empieza de cero |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>L</kbd> | clic a través: los clics van al juego (otra vez para quitarlo) |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>T</kbd> | temporizadores de jefes de campo |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>A</kbd> | pestaña Atlas / volver al medidor |
| clic izquierdo en un jugador | abrir el desglose |
| clic derecho en un jugador, o 💬 | copiar el resultado para el chat del juego o Discord; pegar con <kbd>Ctrl</kbd>+<kbd>V</kbd> |
| rueda del ratón sobre el título del combate | pasar de un combate a otro (abajo = más antiguo, arriba del todo = en vivo) |
| <kbd>Ctrl</kbd> + rueda sobre las filas | tamaño de fila, 50–100 % |
| pulsar otra vez la pestaña activa | volver al medidor |
| clic derecho en una pastilla de evento o de jefe | activar o quitar sonido y notificación de Windows |
| **A** dorada de la zona de notificaciones | historial, temporizadores, ajustes, informar de un problema, actualizaciones, salir |

Las teclas se cambian en *Settings → Hotkeys*. La barra de botones (candado, opacidad, ajustes, chat, reinicio, ocultar) aparece al poner el ratón sobre el overlay. Mientras hay abierta una pestaña que no es el medidor, el clic a través se desactiva y el overlay no se esconde solo. En Linux las teclas son los atajos de tu compositor: ver [Comandos](#comandos).

## Las pestañas

Los iconos de arriba a la izquierda del overlay cambian de pestaña (pon el ratón encima para ver su nombre). La flecha de al lado abre la pestaña actual en una segunda tarjeta con su propia X. El botón **?** abre la página de ayuda de la pestaña en la que estás.

### Meter

El medidor de daño: una fila por jugador con puesto, nombre, clase, GS y CP, DPS, daño total y porcentaje; la barra de vida del jefe arriba; *TOP HIT*; abajo el estado de la captura, *Open the fight* y *Fights*; y debajo la pastilla del jefe y las de eventos.

<table>
  <tr>
    <td width="50%"><img src="docs/img/breakdown-dps.png" alt="Desglose: línea temporal de DPS y tabla por habilidad"><br><sub><b>Desglose</b> — línea temporal de DPS y cada habilidad del jugador</sub></td>
    <td width="50%"><img src="docs/img/breakdown-accuracy.png" alt="Precisión: crítico, espalda, frente, perfecto y smite"><br><sub><b>Precisión</b> — crítico, espalda, frente, perfecto y smite por habilidad</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/img/breakdown-rotation.png" alt="Rotación: cada cast en orden"><br><sub><b>Rotación</b> — cada cast en orden, los críticos con anillo dorado</sub></td>
    <td width="50%"><img src="docs/img/boss-timers.png" alt="Temporizadores de jefes de campo"><br><sub><b>Temporizadores</b> — leídos del mapa del juego, al segundo</sub></td>
  </tr>
</table>

- **Temporizadores de jefes de campo**: en el juego abre una vez *Mapa → Exploración → Monstruos de campo*; el meter copia la lista y lleva la cuenta atrás de cada jefe, por servidor. La campana de un jefe suena 2 minutos antes de que vuelva. Se abren con <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>T</kbd>, con la pastilla dorada o con la pestaña Boss Timers.
- **Pastillas de eventos**: Shugo (cada hora a las :03, unos 3 minutos), Invasion (:30), Rift (cada 3 h desde las 02:00), Kaira (cada 3 h desde la 01:00), Siege (lunes, jueves y sábado a las 21:00) y el Reset diario, en hora del servidor. Se ponen naranjas y rojas al acercarse y suenan 10 y 5 minutos antes. Las notificaciones de Windows vienen apagadas porque un aviso encima del juego baja los FPS; clic derecho en una pastilla para cambiar sonido o notificación.
- El juego no avisa de cuándo empieza el Shugo, así que su hora es aproximada: si ves que empieza en otro minuto, clic derecho en su pastilla → *Starts now* y el meter recoloca el ciclo.

### Atlas

<p align="center"><img src="docs/img/atlas.png" width="620" alt="Atlas: Verteron con el jugador, nodos de recolección, la mejor zona de oricalco y los puntos de mascotas"></p>

- Mapas calibrados de **Verteron, Altgard y el Abismo inferior**. Tú eres el punto dorado (el servidor no manda tu movimiento al caminar, así que se mueve cuando usas habilidades o recolectas). En otras zonas no hay mapa, pero los nodos se siguen apuntando.
- **Nodos** con su icono: verde con número = a la vista y las unidades que quedan; *N?* = vivo la última vez que lo viste; gris con un tiempo = agotado y vuelve en ese tiempo, cuando el meter ya ha aprendido la reaparición de ese punto; tenue = conocido por los datos del mapa pero aún no visto por ti. Las calaveras son jefes de campo (rojo vivo, gris con la cuenta atrás).
- Los botones de materiales de la cabecera marcan las **cinco mejores zonas** de ese material (hasta dos a la vez). La capa **Pets** marca los monstruos que sueltan almas de las mascotas que tienes por debajo de nivel 3: rojo = no la tienes, naranja = nivel 1, amarillo = nivel 2. Clic derecho en un punto de mascota para ver una tarjeta con el monstruo, tu nivel de esa mascota, las almas por nivel y los otros monstruos que la sueltan.
- Arrastra para moverte, rueda para el zoom, *Follow me* para que el mapa te siga, *Fit* para ver la zona entera. Clic derecho en un nodo desconocido para decir qué es, o márcalo como *Other (not a node)* para que desaparezca del mapa.

### Top DPS

<p align="center"><img src="docs/img/top-dps.png" width="560" alt="Top DPS: diez arqueros de tus combates, el primero abierto con reparto por habilidad, apertura, secuencias repetidas y casts por minuto"></p>

- **My fights**: los diez mejores DPS por clase que este meter ha visto en tus combates (un combate por jugador, de 30 s o más). Clic en una fila para ver el reparto por habilidad, la apertura (primeros 16 casts), las secuencias de 3 casts más repetidas y los casts por minuto; *Open* abre el combate guardado.
- **Server** y **Europe**: los diez personajes más fuertes de cada clase por poder de combate, del ranking público de aion2index.com (el juego no publica ranking de DPS).
- *Load profile* enseña la ficha oficial de ese personaje (equipo, habilidades, Daevanion) para compararla con la tuya.

### Prices

<p align="center"><img src="docs/img/prices.png" width="760" alt="Prices: seis objetos vigilados con su límite, último precio, tendencia de 24 h y anuncios"></p>

- Busca un objeto por su nombre en **inglés o español**, escribe un precio máximo (`850k`, `1.2m`, `1,200,000`) y pulsa *Add*. Marca *sell above* (o pulsa **≤ / ≥** en una fila) para que te avise cuando algo que tienes suba por encima de un precio.
- Una vez por hora el meter mira el anuncio más barato en **aion2.exchange**; cuando llega a tu precio suena un aviso y la fila se pone verde. Si no vigilas nada, no consulta nada.
- Cada fila enseña el último precio, su tendencia en 24 h, el número de anuncios y — si rellenas *units* y *paid each* — lo que vale lo que tienes y cuánto ganas. Despliega una fila para ver su gráfico de precio.
- *Copy market script* copia un script para la consola del navegador en aion2.exchange que exporta todo el mercado de tu servidor a un archivo: es lo que usa la calculadora de beneficio de Craft.

### Craft

<table>
  <tr>
    <td width="50%"><img src="docs/img/craft.png" alt="Craft: recetas que puedes fabricar ahora, con una favorita fijada arriba"><br><sub><b>Craft</b> — lo que puedes fabricar ahora (×N), favoritas primero</sub></td>
    <td width="50%"><img src="docs/img/craft-profit.png" alt="Beneficio: recetas ordenadas por margen contra el mercado"><br><sub><b>Profit</b> — margen de cada receta contra la exportación del mercado</sub></td>
  </tr>
</table>

- Las recetas que puedes fabricar **ahora** con tu bolsa y los dos almacenes, y con *×N* cuántas veces. *Also short by 1–2 materials* añade las que casi puedes hacer, con lo que falta y de dónde sale (recolección, crafteo, drop de mazmorra con su probabilidad, NPC, logro).
- El buscador mira en todas las recetas, en inglés o español; la **estrella** fija una receta arriba de todas las listas.
- **Beneficio**: *Load market file…* con la exportación de Prices, marca *Profit* y salen todas las recetas cuyo resultado se vende, por margen (anuncio más barato × cantidad tras la comisión, menos los materiales más baratos). Un margen alto con uno o dos anuncios puede no venderse.

### Build

<table>
  <tr>
    <td width="50%"><img src="docs/img/build-skills.png" alt="Build: habilidades y estigmas con la maqueta de questlog, nivel del build frente al tuyo"><br><sub><b>Skills &amp; Stigmas</b> — nivel objetivo abajo a la derecha, el tuyo abajo a la izquierda</sub></td>
    <td width="50%"><img src="docs/img/build-daevanion.png" alt="Planificador de Daevanion: los cinco tableros con el plan frente a los nodos abiertos"><br><sub><b>Daevanion</b> — el plan frente a los nodos que tienes abiertos, con las estadísticas sumadas</sub></td>
  </tr>
  <tr>
    <td colspan="2"><img src="docs/img/build-import.png" width="560" alt="Importar de questlog.gg: pegar un enlace o buscar builds públicos"><br><sub><b>Import</b> — pega un enlace de questlog.gg, busca builds públicos o lista los tuyos</sub></td>
  </tr>
</table>

- El build de habilidades de tu personaje con la **maqueta del planificador de questlog.gg**, comparado con su ficha oficial: en cada icono el número de abajo a la derecha es el nivel que pide el build y el de abajo a la izquierda el tuyo — verde ya llegas, ámbar te faltan 4 niveles o menos, rojo te falta más o no la has aprendido. Desmarca *Show skills already at the build level* para ver solo lo pendiente.
- Si aún no tienes build, la pestaña te ofrece los cinco builds públicos de tu clase con más "me gusta": *Add* para empezar con uno, *Search…* para buscar otro, *Import…* para el enlace de questlog que te pase un amigo, o *New from profile* para partir de lo que tienes. Sin iniciar sesión, *Import… → My builds* lista tus propios builds y planes de questlog, y *Check questlog* ofrece *Update* cuando tu copia en questlog es más nueva.
- **Daevanion**: los cinco tableros de tu clase; clic en los nodos para planear, ver cuáles ya tienes abiertos y comparar las estadísticas del plan con las de ahora. *Import plan…* carga un plan del daevanion-planner de questlog.
- Clic en una habilidad para ver su barra de nivel, sus especializaciones, tu nota (*When to use · combos*) y las **relaciones** que el meter saca solo (qué buff le da otra habilidad, qué estado necesita y quién lo pone, la cadena de su tecla, su puesto en tu prioridad). Todo se guarda en tu PC, un archivo por build; *Copy for questlog* lo publica desde tu propia sesión del navegador.

### Items

<p align="center"><img src="docs/img/items.png" width="760" alt="Items: bolsa, almacén del personaje, almacén de la cuenta y una búsqueda de Odyle en tres personajes"></p>

- La **bolsa** y el **almacén del personaje** con el que juegas, el **almacén compartido de la cuenta** (el "almacén del servidor" del juego, juntado hueco a hueco con lo que vio cada personaje; un punto azul marca los huecos que otro personaje cambió después) y el **equipo puesto**. Color del marco = grado; vista de casillas o lista, filtros y orden.
- **Buscador entre personajes** (inglés o español): las casillas que coinciden se iluminan y el panel lateral dice dónde está cada objeto en cada personaje, de dónde sale y qué recetas lo usan, con *Open in Craft →*.
- El pie suma por personaje Kina, Kina ligada, Puntos del Abismo, Piedras de encantamiento y el valor de lo que tiene. El juego solo manda un almacén al abrir su ventana: abre cada uno una vez con el meter encendido.

### Selector de personaje

<p align="center"><img src="docs/img/character-selector.png" width="560" alt="El selector de personaje junto a las pestañas, en ámbar mientras miras otro personaje"></p>

El desplegable junto a las pestañas elige de quién son la bolsa, los almacenes, los builds y las cuentas de Craft que enseñan las pestañas. Funciona mientras juegas: el personaje conectado sale como *(current)* y sigue recibiendo los datos en vivo, y la caja se pone ámbar mientras miras a otro. Si entras con otro personaje en el juego, la vista cambia a él.

## Ajustes que conviene conocer

Settings es una pestaña como las demás (con un botón para sacarla a su propia ventana).

- **Capture → Network adapter**: *Automatic* encuentra el adaptador que lleva la conexión del juego (incluido el loopback de ExitLag). Elige uno a mano solo si una VPN lo oculta. **Receive from another meter** recibe en cambio un `host:puerto`, para un meter que no ve el juego por sí mismo.
- **Hotkeys** (Windows): todas se pueden cambiar, se aplican al guardar.
- **Game**: región y servidor (*Auto* desde el login). **Start and stop with AION 2** (Windows) crea una tarea para tu usuario de Windows (sin administrador) que arranca el meter cuando el juego está abierto y lo cierra un minuto después de cerrar el juego. Desmárcala para quitar la tarea.
- **Updates** (Windows): el meter mira este repositorio poco después de arrancar y cada 2 horas. *Install updates by itself* descarga el zip nuevo en segundo plano, comprueba su tamaño y su SHA-256 con GitHub y lo aplica al siguiente arranque o cuando no estás jugando (tras 10 minutos sin combate); el meter se reinicia solo y tus ajustes se quedan. Desmárcalo para que te pregunte. **Channel**: *Stable* (por defecto) o *Development builds*.
- **Overlay**: barras relativas al primero o al total del grupo, barra de vida del jefe, GS / CP, mostrarse solo y ocultarse tras N segundos, filas, opacidad, tamaño de fila.
- **Language**: la interfaz en inglés o en español, y el idioma de los nombres de objetos, habilidades y monstruos (el de la interfaz, o inglés / español).

## Funciones retiradas: las del servidor

Desde esta versión **el meter no envía nada al mantenedor y ya no hay ningún servidor detrás**. El mantenedor no mantiene servicios funcionando para el meter, así que todo lo que necesitaba uno ha salido del código:

- **Estadísticas de uso anónimas** — retiradas.
- **Envío de informes de fallos** ("Send to Diego") — retirado. *Report a problem…* ahora solo guarda un zip en tu PC: adjúntalo a un [issue de GitHub](https://github.com/akunito/aion2-meter-releases/issues) (ver [Informar de un problema](#informar-de-un-problema)).
- **El código de copia de seguridad y *Restore from code*** — retirados. Los códigos antiguos ya no restauran nada: la copia del servidor está borrada.
- **Compartir los nombres de nodos del atlas** — retirado. Los nombres que pongas a los nodos se quedan en tu PC.

Qué hacer en su lugar: tus builds, tu lista de precios vigilados y tus recetas favoritas son archivos normales en `%AppData%\AionMeter` (Linux: `~/.local/share/AionMeter`). Para pasarte a un PC nuevo, copia esa carpeta — en `characters\<servidor>-<nombre>\builds\` están los builds. Las versiones hasta la v0.5.0 siguen intentando enviar; esos envíos simplemente fallan sin decir nada y no se rompe nada más. Al actualizar desaparecen.

## Informar de un problema

Clic derecho en la **A** dorada → *Report a problem…* (en Linux: *Settings* de la tarjeta), cuenta qué pasó y guarda el zip. El zip lleva los dos últimos registros, los ficheros de cierres inesperados, tus ajustes (sin direcciones privadas), el resumen de estado, tu lista de Top DPS y la información de la build; tu inventario, títulos y mascotas solo si marcas la casilla. Luego pulsa *Open an issue on GitHub* (o ve a los [issues](https://github.com/akunito/aion2-meter-releases/issues)) y escribe lo que hiciste, lo que esperabas y el zip adjunto. Si el meter da un error, un globo abre esa misma ventana.

Las grabaciones de paquetes (*Settings → Capture → Record game packets*, apagado por defecto) contienen tu tráfico del juego, incluidos nombres de personajes: no las adjuntes a un issue público.

## Privacidad

El meter lee el tráfico del juego en tu PC y lo guarda todo en `%AppData%\AionMeter` (Linux: `~/.local/share/AionMeter`). Nunca ve tu cuenta ni tu contraseña, y **no envía nada a ningún sitio**: ni estadísticas, ni combates, ni informes.

Lo que consulta (solo lectura):

- **aion2.plaync.com** — la ficha oficial pública de tu personaje (habilidades, Daevanion, equipo) al entrar y cada 6 horas; las fichas que abras en Top DPS y en la ficha de jugador.
- **questlog.gg** — builds públicos para la pestaña Build (sugerencias, búsqueda, importaciones, tus propios builds).
- **aion2.exchange** — una vez por hora, solo los objetos que vigilas en Prices.
- **aion2index.com** — los rankings Server / Europe, solo si los abres.
- **aion2hub.me** — iconos de objetos y nombres que no vienen incluidos, cuando hacen falta.
- **assets.playnccdn.com** y **metabot.gg** — iconos de habilidades y retratos de jefes, una vez cada uno (se puede apagar en Settings).
- **github.com** — la comprobación de versión y la descarga de la actualización desde este repositorio (Windows).

## Linux

La versión de Linux es el mismo meter en dos piezas:

- **El daemon de captura** (`aionmeter-daemon`) — el meter sin ventana: captura, motor de combate, historial, inventario, temporizadores, precios. Corre como **servicio de sistema de systemd con tu propio usuario**, solo con `CAP_NET_RAW` y `CAP_NET_ADMIN` (lo que libpcap necesita para capturar). Corre con el mismo usuario que el juego, así que encuentra la conexión del juego sin ningún otro privilegio. **No** arranca con el sistema: el lanzador `aionmeter` lo arranca al abrir la tarjeta y lo para al cerrarla.
- **La tarjeta** (`aionmeter`) — una ventana nativa **GTK 4 de Wayland** con todas las pestañas: medidor, atlas, top DPS, precios, crafteo, build, objetos, temporizadores de jefes, ajustes, ayuda, la ficha de jugador e *Informar de un problema*.

Los datos van en `~/.local/share/AionMeter`, con los mismos archivos y el mismo formato que `%AppData%\AionMeter` en Windows: puedes copiar encima una carpeta de datos de Windows y conservar tus combates, builds y lista de precios.

### Requisitos

- Linux x86_64 con **systemd** y **polkit**, en una sesión **Wayland** (la tarjeta es un cliente Wayland; X11 / Xwayland no está soportado).
- **Runtime de .NET 10**, **libpcap**, **GTK 4**. Opcionales: **gtk4-layer-shell** (solo para `--layer`) y **webp-pixbuf-loader** (los iconos de habilidades de questlog son WebP).
- AION 2 con Steam / Proton, ejecutándose con el mismo usuario que instala el meter.

Instala los paquetes con el gestor de tu distribución:

| Distribución | Comando |
|---|---|
| Arch / CachyOS / EndeavourOS | `sudo pacman -S dotnet-runtime libpcap gtk4 gtk4-layer-shell webp-pixbuf-loader` |
| Fedora 43 | `sudo dnf install dotnet-runtime-10.0 libpcap gtk4 gtk4-layer-shell webp-pixbuf-loader` |
| Ubuntu 25.10+ | `sudo apt install dotnet-runtime-10.0 libpcap0.8t64 libgtk-4-1 libgtk4-layer-shell0 webp-pixbuf-loader` |
| Debian 13 | `sudo apt install libpcap0.8t64 libgtk-4-1 libgtk4-layer-shell0 webp-pixbuf-loader`, y .NET como abajo |
| .NET si la distribución no lo trae | [las instrucciones de Microsoft](https://learn.microsoft.com/dotnet/core/install/linux), o `dotnet-install.sh --runtime dotnet --channel 10.0` (en `~/.dotnet`, que el instalador y el lanzador también encuentran) |

`install.sh` los comprueba y enseña esta lista si falta algo.

### Instalación

```sh
tar xzf AION2DpsMeter-v<versión>-linux-x64.tar.gz
cd AION2DpsMeter-linux
./install.sh
```

Ejecútalo con tu usuario, no como root: pide `sudo` una vez, para escribir la unidad de systemd (`/etc/systemd/system/aion2-meter-daemon.service`) y una regla de polkit (`/etc/polkit-1/rules.d/50-aion2-meter.rules`) que deja a tu usuario arrancar y parar esa unidad sin contraseña. Todo lo demás va a tu carpeta personal:

- el programa en `~/.local/opt/aionmeter` (la versión anterior se guarda en `~/.local/opt/aionmeter.prev`),
- los comandos `aionmeter`, `aionmeter-toggle` y `aionmeter-clicks` en `~/.local/bin`,
- una entrada *AION 2 DPS Meter* en el menú de aplicaciones,
- los ajustes del lanzador en `~/.config/aionmeter/launcher.conf`.

| Opción | |
|---|---|
| `--layer` | la tarjeta es un overlay layer-shell en vez de una ventana (ver [Compositores](#compositores-y-pantalla-completa)) |
| `--monitor NOMBRE` | salida para la tarjeta `--layer`, p. ej. `DP-1` (por defecto: la que elija el compositor) |
| `--prefix DIR` | instalar el programa en otro sitio que no sea `~/.local/opt/aionmeter` |
| `--no-system` | saltarse la parte de root (unidad + regla de polkit), p. ej. en NixOS, donde la pone el módulo |

### Primer arranque

Ejecuta `aionmeter` (o elige *AION 2 DPS Meter* en el menú de aplicaciones). Se abre la tarjeta y arranca el daemon; la línea de estado al pie de la tarjeta dice *"Waiting for the game to connect…"* hasta que el juego está abierto, y luego *"Capturing · …"*. Igual que en Windows, **abre el meter antes de entrar con tu personaje**: los nombres de los jugadores llegan en las pantallas de carga, y tus mascotas, inventario y títulos al entrar.

Al cerrar la tarjeta (o con `aionmeter --quit`) se para el daemon; antes se guarda el combate en curso. *Settings → Daemon* en la tarjeta reinicia el daemon o cierra la tarjeta.

### Comandos

| | |
|---|---|
| `aionmeter` / `aionmeter --show` | abrir la tarjeta, o mostrar y traer al frente la que ya está abierta |
| `aionmeter --tab CLAVE` | abrir una pestaña: `meter`, `atlas`, `top`, `prices`, `craft`, `build`, `items`, `timers`, `settings` |
| `aionmeter --hide` / `--toggle` / `--quit` | ocultar, ocultar/mostrar, cerrar la tarjeta (y parar el daemon) |
| `aionmeter-toggle` | ocultar/mostrar la tarjeta, o abrirla si no está abierta — asígnale una tecla |
| `aionmeter-clicks` | clic a través sí/no: los clics van al juego — asígnale una tecla |
| arrastrar la tarjeta | moverla (por su cabecera en cualquier pestaña, o por cualquier parte que no sea un control), también a otra pantalla |
| arrastrar con el botón derecho, o la esquina dorada de abajo a la derecha | cambiar el tamaño desde el borde o la esquina más cercana |
| rueda del ratón sobre la esquina de tamaño | zoom de la tarjeta |

Una segunda llamada a `aionmeter` le pasa su orden a la tarjeta abierta y termina, así que los atajos de teclado pueden llamarlo sin problema. Ejemplo de atajos para Sway (`~/.config/sway/config`):

```
bindsym $mod+g exec aionmeter-toggle
bindsym $mod+Shift+g exec aionmeter-clicks
```

### Compositores y pantalla completa

- El **modo ventana** (el de por defecto) funciona en cualquier compositor Wayland. La tarjeta es una ventana normal con app id `aionmeter`. En **Sway** además recuerda su posición y tamaño (por el IPC de Sway); en otros compositores la coloca el compositor.
- Una ventana normal **queda oculta detrás de un juego a pantalla completa**. O juegas en ventana sin bordes / modo ventana y dejas la tarjeta flotando encima, o instalas con **`--layer`**: la tarjeta pasa a ser una superficie layer-shell en la capa overlay, por encima de las ventanas a pantalla completa, y se coloca ella misma. `--layer` necesita gtk4-layer-shell y un compositor con el protocolo wlr-layer-shell — compositores wlroots como Sway, Hyprland o river, y KDE Plasma. GNOME no lo soporta.

Reglas de Sway para el modo ventana (`~/.config/sway/config`):

```
for_window [app_id="aionmeter"] floating enable, sticky enable, border none
no_focus [app_id="aionmeter"]
```

### NixOS

El tarball (y este repositorio) trae un módulo de NixOS, `linux/nixos/aion2-meter.nix`, que pone con rutas del Nix store lo que `install.sh` escribe como root: la unidad de captura, la regla de polkit y los tres lanzadores (con .NET, libpcap, GTK 4, gtk4-layer-shell y el cargador WebP en sus rutas).

```nix
imports = [ ./aion2-meter.nix ];
programs.aion2-meter = {
  enable = true;
  user = "alice";          # el usuario que juega
  # layer = true; monitor = "DP-1";      # tarjeta layer-shell
  # forward = "127.0.0.1:47013";         # reenviar además los paquetes a un meter de Windows bajo Wine / en una VM
  # prefix = null; dataDir = null;       # por defecto: ~/.local/opt/aionmeter, ~/.local/share/AionMeter
};
```

Recompila el sistema, luego descomprime el tarball y ejecuta `./install.sh --no-system` para dejar el programa en `~/.local/opt/aionmeter`. Las actualizaciones no necesitan tocar la configuración: descomprime el tarball nuevo y vuelve a ejecutar `./install.sh --no-system`.

### Actualizar, desinstalar, registros

- **Actualizar**: en Linux no hay actualización automática. Descarga el tarball nuevo y vuelve a ejecutar `./install.sh`: cambia el programa (el anterior se queda en `~/.local/opt/aionmeter.prev`), reinicia el daemon si estaba corriendo y conserva tus datos y los ajustes del lanzador.
- **Desinstalar**: `./uninstall.sh` quita el programa, los comandos, la entrada del menú, la unidad y la regla de polkit, y conserva `~/.local/share/AionMeter`; `./uninstall.sh --purge` borra también los datos.
- **Registros**: `journalctl -u aion2-meter-daemon -f` para el daemon; los registros propios del meter están en `~/.local/share/AionMeter/logs/`.

### Lo que solo existe en Windows

El icono de la bandeja, las notificaciones de Windows, *Start and stop with AION 2*, la actualización automática y las teclas globales. En Linux la tarjeta tiene lo que en Windows hace el menú de la bandeja, y las teclas son los atajos de tu compositor ([Comandos](#comandos)).

### Preguntas frecuentes de Linux

**El estado dice "No capture (libpcap missing …)".**
Instala libpcap (tabla de arriba). En NixOS usa el módulo: `${pkgs.libpcap}/lib` por sí solo no trae la biblioteca.

**Se queda en "Waiting for the game to connect…".**
El juego tiene que correr con el mismo usuario que el meter. Si el juego corre en su propio espacio de procesos, el daemon pasa a mirar las conexiones al puerto del juego a los 30 s; si hay dos adaptadores de red que la llevan, elige el tuyo en *Settings → Capture*.

**La tarjeta no sale encima del juego.**
El juego está a pantalla completa: juega en ventana sin bordes / modo ventana, o reinstala con `./install.sh --layer` (ver [Compositores](#compositores-y-pantalla-completa)).

**`aionmeter` dice que no pudo arrancar `aion2-meter-daemon.service`.**
Falta la regla de polkit (instalaste con `--no-system`, o polkit no está corriendo): vuelve a ejecutar `./install.sh` sin `--no-system`, o arráncalo a mano con `sudo systemctl start aion2-meter-daemon`.

## Preguntas frecuentes

**El overlay está vacío / no veo DPS.**
Comprueba que Npcap está instalado (paso 1) — sin él funciona todo menos el DPS en vivo, y la línea de estado lo dice; en cuanto lo instalas, la captura arranca sola. Pon el ratón sobre la **A** dorada: si dice *"No Npcap adapter carries the game connection"*, elige tu adaptador en *Settings → Capture*. El overlay se esconde fuera de combate: <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> lo enseña. La pantalla completa exclusiva lo tapa: juega en ventana sin bordes o pon el meter en otro monitor.

**Los jugadores salen como "Templar #12345".**
Los nombres llegan en las pantallas de carga. Entra en una mazmorra o pasa una pantalla de carga y aparecen; la próxima vez abre el meter antes de entrar con el personaje.

**¿Funciona con ExitLag o con VPN?**
Sí con ExitLag y otros aceleradores de ping (las opciones por defecto de Npcap capturan su loopback). Si una VPN oculta la conexión del juego, elige el adaptador en *Settings → Capture*.

**Faltan iconos de objetos.**
El archivo no trae imágenes de objetos (hay más de 12.000): cada icono se descarga la primera vez que una fila lo necesita y se guarda, así que van apareciendo en unos segundos. Algunos iconos son WebP y solo se ven si Windows tiene el códec WebP (en Linux: webp-pixbuf-loader). Los iconos de habilidades y los retratos de jefes funcionan igual, y *Settings → Download skill icons and boss portraits* lo apaga todo.

**¿Cómo traigo un build de questlog.gg?**
Pestaña Build → *Import…* → pega el enlace (`…/skill-builder/<usuario>?build-id=<n>`, o un enlace de `daevanion-planner` para un plan) → *Import*. O busca entre los builds públicos, o abre *My builds* para ver tus propios builds de questlog sin iniciar sesión. Importar otra vez el mismo build lo actualiza y conserva tus notas y tu plan.

**Tengo un PC nuevo. ¿Cómo recupero mis builds?**
Copia la carpeta de datos (`%AppData%\AionMeter`, en Linux `~/.local/share/AionMeter`) del PC viejo. El código de copia de seguridad ya no existe: ver [Funciones retiradas](#funciones-retiradas-las-del-servidor).

**Windows o el antivirus se quejan.**
La build no está firmada con un certificado de pago y lee el tráfico de red, cosa que a algunos antivirus no les gusta. Permítelo (Seguridad de Windows → Protección contra virus y amenazas → Historial de protección → Permitir) o excluye la carpeta del meter. Puedes comprobar el zip con `SHA256SUMS.txt`, o compilarlo tú mismo desde este código.

**Me sale un aviso de versión nueva.**
Es normal: el meter se actualiza solo desde este repositorio y se reinicia por su cuenta; tus ajustes e historial se quedan.

**Mi porcentaje de críticos en un jefe de campo lleno de gente es casi cero / en un jefe de mundo faltan casi todos.**
En jefes con mucha gente el juego deja de mandar a tu cliente casi todo el daño (y muchos críticos). El meter enseña exactamente lo que llega y dice *"N attacking · damage of M sent"* en vez de un DPS de grupo que no puede saber.

**¿Me pueden banear?**
El meter no toca el juego: no lee memoria, no inyecta nada, no le envía nada. Aun así, las herramientas externas pueden ir contra las normas — tú decides.

## Compilar desde el código

En el equipo solo hace falta Docker; la compilación corre en `mcr.microsoft.com/dotnet/sdk:10.0`.

```sh
build/build.sh                  # tests + zip de Windows + tarball de Linux
build/build.sh --linux-only     # tests + tarball de Linux (se salta la publicación lenta de win-x64)
build/build.sh --no-tests       # sin los tests (solo para iterar en local; las versiones siempre los pasan)
VERSION=0.6.0 build/build.sh    # una versión publicada: versión en los ensamblados, "release v0.6.0" en BUILD-INFO
tools/release.sh 0.6.0 NOTAS.md    # mantenedor: compila con tests y publica la versión en GitHub (--dev = pre-release)
```

Los archivos y `SHA256SUMS.txt` quedan en `dist/`. Las builds sin `VERSION` son de desarrollo y nunca se actualizan solas.

Estructura del repositorio:

| | |
|---|---|
| `src/AionMeter.Core` | captura, decodificación del protocolo, motor de combate, datos del juego, mercado, atlas (aquí vive el núcleo del original) |
| `src/AionMeter.Service` | el motor que comparten todas las interfaces: ajustes, historial, inventario, temporizadores, precios, builds |
| `src/AionMeter.App` | la aplicación de Windows (WPF) |
| `src/AionMeter.Daemon` | el daemon de captura de Linux |
| `src/AionMeter.Overlay` | la tarjeta de Linux (GTK 4) |
| `src/AionMeter.Cli` | herramienta de línea de comandos (captura en vivo, reproducción de grabaciones) |
| `tests/` | los tests |
| `data/` | datos del juego, mapas del atlas y páginas de ayuda que acompañan al meter |
| `linux/` | `install.sh`, lanzadores, unidad de systemd, regla de polkit, módulo de NixOS |
| `build/` | `build.sh` |

Las contribuciones son bienvenidas: issues y pull requests aquí. Los arreglos de la captura, el protocolo o el motor de combate son también del original — por favor, mándalos además a [cyberbadger6969/aion2-dps-meter](https://github.com/cyberbadger6969/aion2-dps-meter), cuyo núcleo sigue este fork.

## Créditos y licencia

- **[cyberbadger6969/aion2-dps-meter](https://github.com/cyberbadger6969/aion2-dps-meter)** — el meter en el que se basa este fork, y su núcleo: captura, protocolo, motor de combate, overlay, desglose, historial y temporizadores de jefes. GPL-3.0. Ver [su README](https://github.com/cyberbadger6969/aion2-dps-meter#readme). Sus tablas de datos del juego vienen de los proyectos GPL-3.0 [A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter) (taengu) y [AIon2-Dps-Meter](https://github.com/Kuroukihime/AIon2-Dps-Meter) (Kuroukihime).
- **[questlog.gg](https://questlog.gg)** — la base de datos de habilidades y los builds y planes públicos que la pestaña Build lee e importa.
- **[aion2hub.me](https://aion2hub.me)** — nombres de objetos, recetas, fuentes de objetos, mascotas y títulos, y los iconos de objetos.
- **[aion2.exchange](https://aion2.exchange)** — los precios del mercado para Prices y la calculadora de beneficio de Craft.
- **[shugo.gg](https://shugo.gg)** — los horarios de los eventos y las teselas del mapa y las posiciones de nodos del Atlas.
- **[aion2index.com](https://aion2index.com)** — los rankings de poder de combate de Top DPS.
- Iconos de habilidades del CDN oficial de AION 2, fichas de personaje de la web oficial de AION 2, retratos de jefes de [MetaBot.GG](https://metabot.gg).

Este fork se distribuye bajo la **GPL-3.0**, la licencia del original; el texto completo está en [`LICENSE`](LICENSE). AION 2 y sus datos y arte son © NCSOFT; este proyecto no está afiliado a NCSOFT ni al autor del original.
