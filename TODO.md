# TODO

## Base Method Symbol Correctness

### Status

Open, but narrower than before.

### What Is Fixed

1. `FindUsages` now resolves the generic event-handler family that previously failed.
   Examples:
   - `BuildingBlocksPlatform.SeedWork.OurDomainEventHandler<THandler, TEvent>.HandleEventAsync(TEvent)`
   - `Monica.EventBus.Abstractions.Handlers.IMoDistributedEventHandler<TEvent>.HandleEventAsync(TEvent)`
   - member-access references such as `target.As<IMoDistributedEventHandler<TEvent>>().HandleEventAsync(...)`

2. `RenameSymbol` now follows the same symbol family and no longer double-renames when the new name starts with the old name.

### Remaining Risk

1. Very large base/interface method families are not yet broadly proven.
   Example:
   - `Monica.Core.Modularity.MoModule.ConfigureServices(IServiceCollection)`

2. Base/interface rename coverage still needs wider validation outside the event-handler shape already fixed.

3. Razor-backed base/interface rename paths still rely on the same symbol-family correctness and need explicit coverage for broader hierarchies.

### Next Validation

1. Add focused tests for high-fanout abstract/virtual base methods.
2. Add focused tests for high-fanout interface methods.
3. Add rename tests for the same families, including Razor-backed entry points where relevant.
4. If any family still under-renames, either harden canonical-symbol selection further or block that entry point explicitly.

### Done When

1. `FindUsages` is correct for the remaining high-fanout base/interface families.
2. `RenameSymbol` is correct for the same families, or rejects unsupported cases clearly.
3. Razor-backed rename behavior matches the C# result for those families.
