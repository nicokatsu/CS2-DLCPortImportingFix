# B&P Harbor Resource Importing Fix

Fixes the bug where businesses in the city do not purchase or import goods from Bridges & Ports DLC cargo harbors.

## What it fixes

In the base game, B&P DLC cargo harbors can end up being ignored as suppliers even when they have stock and are otherwise operating normally. Businesses may send their trucks to other freight facilities or external road connections instead.

This mod patches the resource seller target setup so DLC harbors can participate correctly in supplier selection based on the resources managed by their own child buildings.

## Notes

- Works per harbor instance instead of rewriting a shared prefab resource list.
- Supports different harbors of the same type managing different goods.
- Requires the `Bridges & Ports` DLC.

## Build

- Harmony version is pinned to `2.2.2`.
- `dotnet build .\\DLCPortImportingFix\\DLCPortImportingFix.csproj -c Debug`

## Repository

GitHub: https://github.com/nicokatsu/CS2-DLCPortImportingFix
