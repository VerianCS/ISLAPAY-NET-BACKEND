# Ramas

Hasta ahora todo el trabajo iba en una sola rama larga. Desde aquí, cada cosa
va en su propia rama, se revisa sola y entra por pull request.

```
master ─────────────●──────────────────────●──────   lo publicado
                    ↑ release/x.y          ↑
develop ──●────●────●────●────●────●───────●──────   lo integrado, siempre verde
           \  /          \   /      \     /
feature/…   ●●            ●●●        ●●●●             una tarea cada una
```

| Rama | Para qué | Sale de | Entra en |
|---|---|---|---|
| `master` | Lo publicado. Sólo recibe merges de `release/*` y `hotfix/*`. | — | — |
| `develop` | Integración. Lo que está aquí compila y pasa CI. | `master` | `master`, vía `release/*` |
| `feature/<tema>` | Una funcionalidad o un cambio con nombre propio. | `develop` | `develop` |
| `fix/<tema>` | Un error encontrado en `develop`. | `develop` | `develop` |
| `chore/<tema>` | Herramientas, CI, dependencias, documentación. | `develop` | `develop` |
| `release/<x.y>` | Congelar una versión: sólo arreglos. | `develop` | `master` y `develop` |
| `hotfix/<tema>` | Un error en lo publicado que no puede esperar. | `master` | `master` y `develop` |

## Reglas

- **Una rama, una tarea.** Si el pull request necesita dos títulos, son dos
  ramas. Cada tarjeta del tablero de implementación nombra su rama.
- **Nombres en minúscula y con guiones**: `feature/swagger`,
  `fix/eisla-simbolo`, `chore/ci-cache`.
- **Nada se sube directo a `develop` ni a `master`.** Todo entra por pull
  request con CI en verde. Conviene activar la protección de ambas ramas en
  GitHub (Settings → Branches) para que la regla no dependa de la memoria.
- **Ramas cortas.** Se actualizan desde `develop` con merge, no con rebase,
  una vez que alguien más pudo haberlas descargado.
- **Merge con squash hacia `develop`** para las ramas de tarea: un commit por
  cambio en la historia de integración. `release/*` y `hotfix/*` entran con
  merge commit, para que se vea de dónde salió cada versión.
- **Cambios entre repositorios** (un endpoint nuevo en el backend y su uso en
  la app) usan el mismo nombre de rama en ambos, y el pull request de la app
  enlaza el del backend. El backend entra primero.

## Punto de partida

`develop` nace del estado integrado a la fecha, que hasta hoy vivía en la rama
de trabajo única. Esa rama se conserva tal cual, como historia; el trabajo nuevo
ya no se le suma.
