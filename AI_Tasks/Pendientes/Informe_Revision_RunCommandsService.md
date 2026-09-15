# Informe de revisión técnica: RunCommandsService

**Repositorio:** https://github.com/peopleworks/RunCommandsService  
**Fecha de revisión:** 12 de septiembre de 2026  
**Alcance:** revisión estática del contenido público visible en GitHub. No se ejecutó el proyecto ni sus pruebas, porque el entorno de análisis no permitió clonar el repositorio. Las recomendaciones que dependen de comportamiento de ejecución se marcan como hipótesis que deben validarse.

## 1. Resumen ejecutivo

RunCommandsService tiene una propuesta útil y bien enfocada: ejecutar comandos programados mediante cron en un servicio de Windows, con recarga de configuración, límites de concurrencia y tiempo, alertas, API de salud y panel web. El repositorio también incluye documentación de instalación, guía de contribución, licencia MIT, validación de configuración y un flujo de compilación en GitHub Actions.

Las prioridades principales son:

1. **Resolver la inconsistencia de plataforma entre el README y el proyecto.** El repositorio se presenta como .NET 9, mientras que la página de `RunCommandsService.csproj` muestra `net8.0`. Esto puede confundir a usuarios, al flujo de CI y al proceso de despliegue.
2. **Crear una suite de pruebas automatizadas real.** Existe `TimezoneTest.cs` en la raíz, pero no se aprecia un proyecto de pruebas independiente ni ejecución de tests en la automatización visible.
3. **Endurecer la superficie HTTP y la administración de comandos.** El Job Builder puede modificar comandos que luego ejecutará un servicio de Windows. La autenticación mediante una sola clave es un punto de partida, pero requiere controles adicionales.
4. **Mejorar el logger.** `FileLoggerOptions.FileSizeLimit` aparece configurado, pero no se observa aplicado en `FileLogger`; además, la escritura síncrona por cada mensaje y la limpieza en cada evento pueden afectar el rendimiento.
5. **Separar responsabilidades.** El scheduler, la ejecución de procesos, la persistencia JSON, la API y el dashboard deberían dividirse en componentes más pequeños y comprobables.

## 2. Aspectos positivos

- Alcance funcional claro y documentación extensa.
- Uso de expresiones cron mediante Cronos y soporte de zonas horarias.
- Límites globales de paralelismo y exclusión por `ConcurrencyKey`.
- Tiempo máximo por trabajo y terminación de procesos bloqueados.
- Recarga de configuración con conservación de la última configuración válida.
- Modo `--validate` para detectar errores antes del despliegue.
- Endpoint de salud, historial de ejecuciones y heartbeat del scheduler.
- Alertas por correo y webhook con aislamiento entre notificadores.
- Plantillas de issues y pull requests, guía de contribución y licencia.

## 3. Correcciones recomendadas

### C-01. Alinear la versión de .NET

**Prioridad:** crítica  
**Evidencia:** el README presenta el servicio como .NET 9 y solicita SDK/runtime 9; la vista pública de `RunCommandsService.csproj` muestra `TargetFramework` igual a `net8.0`.

**Acciones:**

- Elegir oficialmente .NET 8 LTS o .NET 9 STS.
- Alinear `TargetFramework`, README, workflow, artefactos y documentación de instalación.
- Añadir `global.json` para fijar una versión o política de SDK.
- Añadir una comprobación de CI que compare el framework del proyecto con el declarado en la documentación.

### C-02. Convertir las comprobaciones de zona horaria en pruebas automatizadas

**Prioridad:** crítica  
**Evidencia:** `TimezoneTest.cs` aparece en la raíz, pero no se aprecia un proyecto `*.Tests.csproj` en el listado principal.

**Acciones:**

- Crear `tests/RunCommandsService.Tests` con xUnit, NUnit o MSTest.
- Mover allí las pruebas de `TimeZoneHelper` y DST.
- Probar instantes inexistentes y ambiguos durante cambios de horario.
- Ejecutar `dotnet test --configuration Release` en CI sobre Windows.
- Generar cobertura y establecer inicialmente un umbral razonable, por ejemplo 70 %, elevándolo gradualmente.

### C-03. Aplicar realmente `FileSizeLimit`

**Prioridad:** alta  
**Evidencia:** `Program.cs` configura un límite de 10 MB y `FileLoggerOptions` declara `FileSizeLimit`, pero el logger visible escribe con `File.AppendAllText` y rota por fecha/retención; no se observa una comprobación del tamaño.

**Acciones:**

- Implementar rotación por tamaño con nombres deterministas.
- Evitar una limpieza completa del directorio en cada mensaje.
- Ejecutar la limpieza una vez al inicio y después mediante temporizador.
- Usar `LastWriteTimeUtc` en lugar de `CreationTime` para retención más predecible.
- Respetar niveles configurados en lugar de que `IsEnabled` devuelva siempre `true`.
- Considerar `Microsoft.Extensions.Logging` con un proveedor mantenido, como Serilog o NLog, si se acepta otra dependencia.

### C-04. Determinar el resultado por código de salida

**Prioridad:** alta  
**Evidencia:** el README indica que contenido en `stderr` marca la ejecución como fallida.

Muchos programas escriben advertencias o progreso en `stderr` y terminan con código 0. El criterio principal debe ser `Process.ExitCode`; la presencia de `stderr` debe registrarse, pero no convertir automáticamente un éxito en fallo salvo que exista una opción explícita.

**Cambio propuesto:**

- Éxito por defecto: `ExitCode == 0`.
- Fallo: código distinto de cero, timeout, cancelación o excepción de arranque.
- Opción por trabajo: `TreatStdErrAsFailure`, por defecto `false`.
- Registrar stdout y stderr como flujos separados y limitar su tamaño.

### C-05. Evitar secretos de ejemplo reutilizables

**Prioridad:** alta  
**Evidencia:** la configuración documentada contiene `AdminKey`, credenciales SMTP y una URL de webhook dentro de `appsettings.json`.

**Acciones:**

- Admitir variables de entorno y el almacén de credenciales de Windows.
- Rechazar al arrancar valores conocidos como `CHANGE_ME` o `put-a-strong-random-key-here` cuando la administración esté habilitada.
- Enmascarar claves, contraseñas, tokens y URLs de webhook en logs, API y errores.
- Entregar `appsettings.example.json` sin secretos y excluir la configuración efectiva del control de versiones cuando proceda.

### C-06. Corregir y automatizar los comandos de documentación

**Prioridad:** media  
**Evidencia:** las vistas renderizadas del README y CONTRIBUTING muestran comandos como `dotnet build - c Debug` y `dotnet run -- project`, que pueden deberse al formato Markdown o a espacios incorrectos.

**Acciones:**

- Usar bloques cercados `powershell` o `shell`.
- Verificar que los comandos reales sean `dotnet build -c Debug` y `dotnet run --project ...`.
- Añadir markdownlint y una prueba de enlaces.
- Validar automáticamente ejemplos de JSON.

## 4. Mejoras recomendadas

### M-01. Arquitectura comprobable

Extraer interfaces y servicios pequeños:

- `IJobScheduler`: cálculo de próximas ejecuciones.
- `IProcessRunner`: arranque, captura, cancelación y finalización del árbol de procesos.
- `IJobRepository`: lectura, validación y escritura atómica de trabajos.
- `IExecutionStore`: historial y métricas.
- `IClock`: tiempo inyectable para pruebas de cron y DST.
- `INotifier`: canales de alerta.

Esto permite pruebas unitarias sin lanzar procesos ni esperar al reloj real.

### M-02. Seguridad del panel y API

La API de escritura permite definir comandos que ejecutará el servicio, por lo que debe considerarse una interfaz administrativa privilegiada.

- Mantener escucha en loopback por defecto.
- Si se expone remotamente, exigir HTTPS mediante un proxy inverso.
- Almacenar la clave como hash o fuera del JSON.
- Comparar secretos en tiempo constante.
- Añadir rotación de claves, rate limiting y auditoría de cambios.
- Incorporar protección CSRF si el navegador envía credenciales automáticamente.
- Aplicar encabezados `Content-Security-Policy`, `X-Content-Type-Options` y `Referrer-Policy`.
- Limitar tamaño del cuerpo y validar estrictamente todos los campos.
- Separar permisos de solo lectura y administración.
- Documentar el modelo de amenazas y el nivel de privilegio recomendado para la cuenta del servicio.

### M-03. Ejecución robusta de procesos

- Evitar construir una cadena de shell cuando sea posible; separar ejecutable y argumentos.
- Añadir `WorkingDirectory`, variables de entorno permitidas y codificación.
- Finalizar el árbol completo de procesos al superar el timeout.
- Limitar stdout/stderr para evitar consumo ilimitado de memoria.
- Registrar código de salida, duración, PID y motivo de terminación.
- Permitir una lista opcional de ejecutables o directorios autorizados.
- Añadir modo de simulación que muestre qué se ejecutaría.

### M-04. Semántica del scheduler

Definir y probar explícitamente:

- Qué ocurre si el servicio estuvo apagado durante una ejecución.
- Si se recuperan ejecuciones perdidas y hasta qué límite.
- Cómo se evita disparar dos veces el mismo instante cron.
- Qué ocurre al recargar configuración mientras un trabajo se ejecuta.
- Conducta ante retrocesos o adelantos del reloj.
- Diferencia entre deshabilitar un trabajo y cancelar una instancia activa.

Se recomienda persistir un identificador de ocurrencia formado por `JobId + ScheduledInstantUtc` y usarlo para deduplicación.

### M-05. Observabilidad estándar

- Logs estructurados en JSON con `JobId`, `OccurrenceId`, `ExitCode` y duración.
- OpenTelemetry para métricas y trazas.
- Métricas: ejecuciones, fallos, timeouts, retraso de inicio, trabajos omitidos y utilización del paralelismo.
- Endpoints separados de liveness y readiness.
- Estado degradado cuando el heartbeat esté atrasado, la configuración sea inválida o la cola esté saturada.
- Correlation ID entre ejecución, alerta y API.

### M-06. CI/CD y calidad

El repositorio contiene un único workflow de compilación visible. Conviene ampliarlo para:

- Restaurar, compilar y probar en `windows-latest`.
- Ejecutar `dotnet format --verify-no-changes`.
- Analizadores .NET con advertencias relevantes tratadas como errores.
- Dependabot o Renovate.
- CodeQL y revisión de dependencias.
- SBOM, hashes SHA-256 y artefactos firmados.
- Publicación reproducible y releases versionadas.
- Matriz con el framework oficialmente soportado.

### M-07. Configuración y compatibilidad

- Definir un esquema JSON para autocompletado y validación.
- Versionar el formato de configuración.
- Detectar IDs duplicados.
- Validar rangos positivos para `PollSeconds`, `MaxParallelism` y `MaxRuntimeMinutes`.
- Validar prefijos HTTP, destinatarios, URLs y combinaciones incoherentes.
- Añadir migraciones o mensajes claros al cambiar propiedades.
- Escribir la configuración mediante archivo temporal, flush y reemplazo atómico con copia de seguridad.

## 5. Nuevas ideas

### N-01. Historial persistente opcional

SQLite permitiría conservar ejecuciones tras reinicios, consultar tendencias y evitar que una cola en memoria pierda contexto. Debe incluir política de retención y compactación.

### N-02. Reintentos configurables

Añadir reintentos por trabajo con backoff exponencial, jitter y códigos de salida reintentables. Nunca se debe reintentar un comando no idempotente sin una decisión explícita del administrador.

### N-03. Dependencias entre trabajos

Permitir flujos simples: ejecutar B tras el éxito de A, límites de profundidad, detección de ciclos y visualización del DAG.

### N-04. Ventanas de mantenimiento y calendarios

Añadir exclusiones por fecha, festivos, ventanas permitidas y opción de pausar globalmente sin detener el servicio.

### N-05. Control manual seguro

Botones de ejecutar, cancelar y reintentar con autorización separada, confirmación visible, motivo obligatorio y registro de auditoría.

### N-06. Plantillas y parámetros

Permitir parámetros tipados y secretos referenciados, sin sustitución arbitraria de texto. Mostrar una vista previa exacta y enmascarada antes de guardar.

### N-07. Exportación e importación

Exportar configuración, validar antes de importar, mostrar diferencias y aceptar rollback. Firmar paquetes de configuración en entornos regulados.

### N-08. Compatibilidad con PowerShell sin shell intermedio

Agregar un tipo de trabajo PowerShell con archivo y argumentos separados, política documentada y soporte para PowerShell 7, manteniendo el tipo de comando genérico.

## 6. Plan propuesto

### Fase 1: seguridad y corrección

1. Resolver .NET 8 frente a .NET 9.
2. Crear el proyecto de pruebas y ejecutar tests en CI.
3. Corregir criterio de éxito, límites de salida y árbol de procesos.
4. Aplicar rotación por tamaño y niveles del logger.
5. Externalizar secretos y endurecer la API administrativa.

### Fase 2: mantenibilidad

1. Extraer `IClock`, `IProcessRunner`, `IJobRepository` e `IExecutionStore`.
2. Añadir esquema JSON y validaciones exhaustivas.
3. Incorporar logs estructurados, readiness y métricas.
4. Ampliar CI con formato, análisis, CodeQL y dependencias.

### Fase 3: producto

1. Historial SQLite opcional.
2. Reintentos y recuperación de ejecuciones perdidas.
3. Auditoría y control manual.
4. Dependencias entre trabajos y calendarios.

## 7. Criterios de aceptación sugeridos

- Documentación, proyecto y CI declaran la misma versión de .NET.
- `dotnet test -c Release` pasa en Windows y cubre cron, DST, timeout, cancelación y recarga.
- Ningún secreto aparece en repositorio, logs o respuestas HTTP.
- Un proceso con código 0 y texto en stderr puede finalizar correctamente.
- El timeout termina también procesos secundarios.
- El archivo de log rota al límite configurado.
- Dos actualizaciones concurrentes no corrompen `appsettings.json`.
- La API administrativa no queda accesible remotamente con valores predeterminados.
- Cada ocurrencia cron se ejecuta como máximo una vez.
- La CI produce artefactos versionados con hash y SBOM.

## 8. Fuentes revisadas

- Repositorio y README: https://github.com/peopleworks/RunCommandsService
- Proyecto: https://github.com/peopleworks/RunCommandsService/blob/master/RunCommandsService.csproj
- Validador: https://github.com/peopleworks/RunCommandsService/blob/master/ConfigValidator.cs
- Logger: https://github.com/peopleworks/RunCommandsService/blob/master/FileLogger.cs
- Workflow: https://github.com/peopleworks/RunCommandsService/blob/master/.github/workflows/build.yml
- Guía de contribución: https://github.com/peopleworks/RunCommandsService/blob/master/CONTRIBUTING.md

## 9. Limitaciones de la revisión

Este informe no afirma que cada riesgo sea un defecto explotable. La clonación fue bloqueada por el entorno, por lo que no se pudo compilar, ejecutar pruebas, inspeccionar cada línea mediante herramientas locales ni realizar análisis dinámico. Antes de abrir incidencias, conviene confirmar cada hallazgo contra el commit actual y añadir una reproducción mínima.
