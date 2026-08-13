# Línea base 1.7.0 previa a memoria operativa

Fecha: 2026-08-13
Commit: `c220e70e2b77f9e895e01070482953eeb1fcd83a`

## Verificación de código

| Comprobación | Resultado |
|---|---|
| `dotnet test HyperMemory.sln -c Release --no-restore` | 26/26 aprobadas |
| `python -m unittest tests.hermes_plugin_test -v` | 17/17 aprobadas |
| `git diff --check` | Sin errores de formato |

## Verificación de instalación

| Comprobación | Resultado |
|---|---|
| Instalación activa | HyperMemory 1.7.0 |
| API loopback | Viva y saludable en el endpoint instalado |
| Skill Hermes | Presente |
| Proveedor Hermes | Presente |
| Integridad | Válida, sin problemas informados |
| Átomos / vectores / auditoría | 90 / 90 / 90 |
| Esquema SQLite | 4 |

## Respaldo previo

Se creó una copia no destructiva mediante la API de backup online de SQLite, acompañada
por eventos inmutables, integración instalada y manifiesto SHA-256:

`artifacts/development-backups/baseline-1.7.0-20260813T122007958Z`

El manifiesto verificó 102 archivos. La base respaldada mide 2.719.744 bytes y su SHA-256
es `d16512f1df9da5a05cddc5b180bd44bdc201b1ef34b42baf84d420c4a765305c`.

## Ensayo de restauración

La base se restauró en un directorio aislado, nunca sobre la instalación activa. El ensayo
obtuvo `PRAGMA integrity_check = ok`, esquema 4 y recuentos 90/90/90. Los 102 archivos del
manifiesto coincidieron con sus hashes. La memoria activa quedó intacta.

Esta copia es una protección de desarrollo local, no una función de backup para el usuario
final. La futura migración deberá conservar además el rollback transaccional del instalador.
