# HyperMemory: auditoría del sistema actual

Fecha de referencia: 2026-08-13
Versión observada: 1.7.0
Commit de referencia: `c220e70e2b77f9e895e01070482953eeb1fcd83a`

Este documento fija la línea base previa a incorporar memoria operativa, de proyecto,
validación, errores, decisiones, contratos, tareas, checkpoints y activación asistida
de capacidades. En esta fase no se cambia el comportamiento ni el esquema persistente.

## CURRENT_ARCHITECTURE

HyperMemory está dividido en cinco piezas principales:

1. **Core**: contratos y modelos independientes de SQLite, HTTP, Hermes y Ollama.
2. **Infrastructure**: persistencia SQLite, archivo inmutable de eventos, búsqueda,
   proyección de conocimiento, importación de grafos, mantenimiento y proveedores de IA.
3. **API**: servicio HTTP local, autenticado para las rutas de memoria, y workers de fondo.
4. **Hermes Bridge**: proveedor Python que captura turnos y recupera contexto sin añadir
   herramientas manuales al usuario.
5. **Installer**: instalación/desinstalación transaccional de la API, skill y proveedor,
   con protección contra sobreescrituras inseguras y restauración ante fallo.

Los contratos existentes son `IEmbeddingGenerator`, `ITextSummarizer`, `IMemoryStore`,
`IMemoryService`, `IKnowledgeProjectionStore`, `IScaleMaintenanceStore`,
`IOperationalDiagnosticsStore` e `IExternalGraphImportService`. La implementación SQLite
actual concentra varias de esas responsabilidades en `SqliteMemoryStore`.

## CURRENT_MEMORY_FLOW

1. El proveedor de Hermes recibe el turno completado mediante `sync_turn`.
2. Respeta exclusiones explícitas del usuario y redacta secretos antes del envío.
3. Construye metadatos de sesión, workspace, agente, recuerdos consultados y evidencia
   verificable observada en archivos o terminal.
4. Guarda primero el evento en una outbox local durable y lo envía a `/memory/upsert`.
5. `MemoryService` vuelve a validar y redactar contenido y metadatos de forma central.
6. Se genera el embedding y `SqliteMemoryStore` comprueba idempotencia por `event_id`.
7. El contenido completo se conserva como sobre JSON inmutable bajo `events/`, dirigido
   por hash, antes de confirmar la transacción SQLite.
8. La transacción inserta átomo, índices léxicos, vector, evidencia, relaciones y auditoría.
9. Los resúmenes y las proyecciones derivadas se agregan; no sustituyen la fuente original.

La memoria histórica existente es append-only en su intención funcional. La reutilización
de un `event_id` con contenido diferente se rechaza.

## CURRENT_RETRIEVAL_FLOW

1. `prefetch` se ejecuta automáticamente antes de responder en Hermes.
2. El proveedor consulta `/memory/query` con workspace, proyecto y sesión disponibles.
3. SQLite recupera candidatos léxicos mediante FTS5 sobre el historial completo.
4. La búsqueda semántica se limita a una ventana acotada de vectores compatibles para
   mantener coste predecible.
5. Se expande contexto relacionado por artefacto, archivo, decisión, fuente, hash,
   comando, verificación y nodos del grafo de conocimiento.
6. El ranking combina señales léxicas, semánticas, estructurales, temporales y afinidad
   con el workspace; el modo de embedding determinista ajusta sus pesos.
7. El proveedor reordena por pertinencia y empaqueta sólo el contexto que cabe en el
   presupuesto configurado, separando solicitudes originales de afirmaciones no verificadas.
8. La respuesta recibe citas, procedencia y evidencia disponibles.

Esto reduce degradación por volumen, pero no elimina el límite de contexto del modelo:
selecciona qué memoria entra en cada turno. Tampoco garantiza por sí solo que una respuesta
sea correcta; la precisión depende de captura, selección, evidencia y conducta del modelo.

## CURRENT_STORAGE

El almacenamiento autorizado vive bajo una raíz llamada exactamente `Hyper_Memory`; se
rechazan rutas que no cumplan esa convención y enlaces o junctions inseguros.

La base SQLite declara actualmente esquema `4` y crea de forma idempotente:

- `memory_atoms`, `memory_vectors`, `memory_evidence`, `memory_relations` y `audit_log`;
- índices FTS5 `memory_fts` y `memory_turn_fts`, más estado de indexación;
- `knowledge_entities`, `knowledge_mentions`, `knowledge_edges` y estado de proyección;
- índices por identidad lógica, proyecto, espacio vectorial, evidencia y grafo.

Los sobres JSON inmutables de `events/` son la evidencia primaria para integridad y
reconstrucción. SQLite contiene índices y proyecciones consultables. La proyección de
conocimiento es derivada, acotada, reconstruible y no reemplaza el archivo original.

Limitación crítica: no existe aún un historial explícito y ordenado de migraciones. El
inicializador usa `CREATE ... IF NOT EXISTS` y actualiza directamente el número de esquema.
Cualquier ampliación debe introducir migraciones aditivas, transaccionales e idempotentes,
sin reinterpretar ni borrar datos anteriores.

## CURRENT_HERMES_INTEGRATION

El proveedor registra tres capacidades observadas:

| Punto | Función actual | Límite relevante |
|---|---|---|
| `prefetch` | Recupera e inyecta memoria automáticamente | Sólo puede aportar contexto; no controla la decisión final del modelo |
| `sync_turn` | Captura el turno principal completado y evidencia observada | Ve herramientas después del turno, no necesariamente inmediatamente después de cada operación |
| `on_session_switch` | Mantiene identidad al cambiar sesión | No equivale a un checkpoint transaccional de tarea |

`get_tool_schemas()` devuelve una lista vacía: HyperMemory no exige comandos manuales al
usuario. Esto debe preservarse.

No se ha comprobado en la integración actual un hook oficial equivalente a
`before_tool`, `after_tool` o `before_completion`. Por tanto:

- la invalidación inmediata tras cada herramienta sólo será estricta si Hermes expone
  ese evento; en caso contrario debe hacerse al cierre de turno y declararse la demora;
- un evaluador de completitud puede emitir evidencia y recomendaciones, pero no debe
  presentarse como bloqueo duro sin un hook previo a la respuesta final;
- nunca se debe simular que una herramienta, prueba o validación se ejecutó.

La selección de skills pertenece a Hermes. HyperMemory puede mantener un registro de
capacidades, detectar necesidades, recordar disponibilidad y sugerir una activación al
router de Hermes. No debe conceder permisos ni ejecutar una skill fuera de los mecanismos
autorizados del agente. Para el usuario, la activación debe seguir siendo automática.

## CURRENT_CONFIGURATION

`HyperMemoryOptions` ofrece actualmente ruta de almacenamiento, endpoint/modelo de Ollama,
fallback determinista de embeddings, resúmenes de fondo, límite de candidatos semánticos,
proyección de conocimiento, tamaños de lote, intervalos de mantenimiento y límites de
importación de grafos.

La API escucha sólo en loopback y las rutas `/memory/*` requieren token. Los flags nuevos
deberán agruparse, iniciar desactivados durante el desarrollo y cumplir la regla:

> todos los módulos nuevos desactivados = comportamiento observable de HyperMemory 1.7.0.

No basta con omitir sus respuestas: desactivarlos también debe evitar registrar workers,
crear efectos secundarios, alterar rankings o cambiar la captura existente.

## CURRENT_TESTS

La línea base existente incluye:

- pruebas .NET de servicio, SQLite, API, privacidad, integridad, escala, proyección de
  conocimiento, importación de grafos y escenarios de recuperación;
- pruebas Python del proveedor Hermes, outbox, captura, prefetch y tratamiento de evidencia;
- evaluaciones reproducibles para LoCoMo, LongMemEval y escala sintética;
- aceptación documentada de la versión 1.7.0 e instalador con flujo de rollback.

Resultado de referencia previo a esta auditoría: **26 pruebas .NET aprobadas**. La suite
Python y los escenarios de larga duración deben volver a ejecutarse y registrarse en la
línea base antes de cualquier migración persistente.

## EXTENSION_POINTS

Las ampliaciones seguras son:

1. **Contratos Core nuevos y abstractos**, sin dependencias de lenguaje, framework o VCS.
2. **Journal operativo append-only** separado de la memoria histórica existente.
3. **Proyecciones de estado reconstruibles** para proyecto, tareas, contratos, validación,
   errores, decisiones y checkpoints.
4. **Tipos de relación extensibles** almacenados como texto, con validación por política.
5. **Memory Router** que componga cortes selectivos de memoria histórica y operativa dentro
   de un presupuesto, sin reemplazar el prefetch actual.
6. **Adaptadores de validación y herramientas** fuera del núcleo, con descubrimiento de
   capacidades y resultado uniforme `PASS`, `FAIL`, `UNKNOWN` o `STALE`.
7. **Capability Registry/Router** para que Hermes active automáticamente skills disponibles
   cuando una tarea las requiera, respetando permisos y degradando a `UNKNOWN` si faltan.
8. **Workers opcionales** para proyecciones, invalidación y compactación no destructiva.
9. **Endpoints nuevos versionados/aditivos**, conservando los contratos HTTP actuales.
10. **Hooks Hermes opcionales por capacidad detectada**, manteniendo `sync_turn` como
    fallback compatible.

## RISKS

| Riesgo | Impacto | Control requerido |
|---|---|---|
| Migración implícita del esquema | Corrupción o incompatibilidad silenciosa | Ledger versionado, transacción, copia previa y prueba de rollback |
| Monolito `SqliteMemoryStore` | Acoplamiento y regresiones difíciles de aislar | Nuevos repositorios/interfaces y una transacción coordinada donde corresponda |
| Hooks Hermes inexistentes | Promesas falsas de invalidación o bloqueo inmediato | Detección de capacidades y fallback explícito por fin de turno |
| Estado derivado tratado como verdad | Finalizaciones incorrectas | Eventos/evidencia como fuente; proyecciones reconstruibles |
| Validadores específicos en Core | Pérdida de universalidad | Contratos abstractos y adaptadores externos |
| Skills activadas sin control | Riesgo de permisos y seguridad | Hermes conserva autorización; HyperMemory sólo enruta necesidades/capacidades |
| Concurrencia de agentes | Sobrescritura o checkpoints incoherentes | IDs globales, revisiones, idempotencia y control optimista |
| Crecimiento histórico | Latencia y contexto degradados | Índices, partición lógica, retrieval jerárquico, presupuestos y métricas |
| Evidencia envejecida | Respuestas aparentemente verificadas pero obsoletas | Contratos, alcance e invalidación `STALE` por cambios reales |
| PII y secretos en nuevas estructuras | Exposición ampliada | Redacción central antes de toda persistencia, retención y borrado verificable |
| Workflow de firma/versionado desalineado | Releases inválidas o artefactos equivocados | Una única fuente de versión y gate de release antes de generar instalador |

## NON_NEGOTIABLE_COMPATIBILITY_GATES

Antes de habilitar cada fase se deberá demostrar:

1. Copia verificable de base, eventos, configuración e integración instalada.
2. Restauración ensayada en un directorio aislado, sin tocar la instalación activa.
3. Todas las pruebas heredadas aprobadas sin modificar sus expectativas para esconder fallos.
4. Flags nuevos desactivados con resultados equivalentes a 1.7.0.
5. Migración repetible e idempotente sobre base vacía y sobre una copia real anterior.
6. Ningún dato existente borrado, reescrito o reclasificado destructivamente.
7. Validaciones sin soporte devuelven `UNKNOWN`, nunca éxito inventado.
8. Desinstalación elimina sólo componentes propios y no daña Hermes ni los datos que el
   usuario haya elegido conservar.
