# HyperMemory para Hermes — guía rápida

## Instalar

1. Abre `HyperMemorySetup.exe`.
2. Elige dónde quieres guardar la memoria.
3. Pulsa **Instalar HyperMemory**.
4. Cierra y vuelve a abrir Hermes.

HyperMemory se iniciará automáticamente con Windows. No necesitas abrirlo ni configurarlo.
Si el servicio se cierra inesperadamente, su supervisor local lo vuelve a iniciar de forma automática.

## Qué ocurre al usar Hermes

- Tú escribes normalmente; no tienes que decir “guarda esto” ni llamar a HyperMemory.
- Antes de responder, Hermes busca automáticamente si existe un recuerdo relacionado.
- Después de responder, HyperMemory guarda el intercambio completo.
- Para que la integración empiece después de instalar, basta con cerrar y volver a abrir Hermes una vez.

## Desinstalar

1. Abre **Configuración de Windows**.
2. Entra en **Aplicaciones > Aplicaciones instaladas**.
3. Busca **HyperMemory para Hermes** y pulsa **Desinstalar**.
4. Elige **Sí** para conservar los recuerdos (recomendado) o **No** para borrarlos permanentemente.
5. Si elegiste borrar, confirma por segunda vez.
6. Cierra y vuelve a abrir Hermes.

La desinstalación retira el proveedor automático y el Skill instalados por HyperMemory, restaura el proveedor de memoria anterior si existía y detiene el servicio. No altera Hermes ni sus demás Skills. La opción recomendada conserva la memoria dentro de `Hyper_Memory`; el borrado permanente sólo ocurre tras elegirlo y confirmarlo expresamente.

## Actualizar

Abre el instalador nuevo y elige la misma ubicación. HyperMemory crea y verifica un respaldo antes de cambiar Hermes. Si la versión nueva no puede instalarse o arrancar, restaura automáticamente la integración y la base de datos anteriores.

## Comprobación rápida

Después de reiniciar Hermes, úsalo normalmente. HyperMemory guarda cada turno principal terminado y recupera recuerdos relacionados incluso en una sesión nueva. No necesitas escribir una orden especial para guardar.
