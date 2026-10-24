# Craft
Recetas contra el inventario en vivo de tu personaje (bolsa y almacenes), y una calculadora de beneficio contra el mercado.
## Qué ves
- El desplegable elige el alcance: "Only what I can craft now" (por defecto), "Also short by 1–2 materials" (con lo que falta y de dónde sale: recolección, crafteo, drop con su porcentaje, NPC, logro) o "All recipes".
- Cada fila: el resultado, profesión y nivel, grado, materiales; "×N" en verde es cuántas veces te da tu stock. La estrella fija la receta arriba en todas las listas; púlsala otra vez para soltarla.
- Escribir en el buscador busca entre todas las recetas por nombre en inglés o español sin importar el alcance; la X o Escape lo limpian.
- El buscador también entiende una palabra de grado en inglés o español: "legendary stone", "piedra legendaria", "unique", "épico" (4 letras o más, plurales valen).
- Varios objetos comparten nombre y solo cambian de grado (en inglés "Artisan's Refining Stone" es tanto la rara como la legendaria). Una etiqueta de color tras el nombre dice el grado: común gris, raro verde, legendario azul, único dorado, épico naranja, especial turquesa. Dentro de una lista de materiales el grado sale entre paréntesis solo cuando el nombre por sí solo es ambiguo.
## Beneficio
- "Load market file…" elige el JSON que el script de mercado guardó en Descargas (Prices → Copy market script). Marca "Profit" para listar todas las recetas cuyo resultado se vende, de mayor a menor margen: anuncio más barato del resultado × rendimiento tras la comisión del broker, menos los anuncios más baratos de los materiales (los materiales sin precio cuentan 0 y se indican).
- Avisos: los precios son el anuncio más barato en la última muestra horaria de la web; el grado del resultado es aleatorio en el juego y la calculadora asume el de la receta; un margen alto con uno o dos anuncios puede no ser vendible.
