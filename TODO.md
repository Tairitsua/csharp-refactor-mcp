# TODO

## Base Method Symbol Correctness

### Status

Open.

### Background

`FindUsages` now tolerates `UnresolvedAnalyzerReference` and correctly maps Razor references back to source files.

However, base/interface method symbol handling is still not fully reliable for some inheritance-heavy patterns.

### Confirmed Findings

1. `FindUsages` works for ordinary abstract/virtual base methods in at least some hierarchies.
   Example:
   - `Monica.RegisterCentre.Core.CoordinatedLeaderService.OnBecameLeaderAsync(CancellationToken)`
   - The tool returns the base declaration, the direct call site, and the derived override declarations.

2. `FindUsages` partially works for framework-wide base methods with many overrides.
   Example:
   - `Monica.Core.Modularity.MoModule.ConfigureServices(IServiceCollection)`
   - The tool returns several override declarations and some base call sites, but the result likely under-reports the full override set.

3. `FindUsages` is incorrect for some generic event-handler base methods.
   Example:
   - `BuildingBlocksPlatform.SeedWork.OurDomainEventHandler<THandler, TEvent>.HandleEventAsync(TEvent)`
   - The tool returns only the XML documentation `<see cref="HandleEventAsync"/>` reference instead of the override chain.

4. Interface-level event-handler method lookup is also incorrect in this area.
   Example:
   - `Monica.EventBus.Abstractions.Handlers.IMoDistributedEventHandler<TEvent>.HandleEventAsync(TEvent)`
   - The tool returned zero references even though many implementations exist in the solution.

### Current Risk

1. `FindUsages` can under-report references for base/interface methods, especially generic event-handler patterns.

2. `RenameSymbol` is likely at risk for the same symbol families.
   Notes:
   - The main rename flow uses `Renamer.RenameSymbolAsync(...)`.
   - Razor projected rename edits still depend on `SymbolFinder.FindReferencesAsync(...)`.
   - If the symbol graph is incomplete, Razor/source edits may be incomplete as well.

3. Renaming base/interface methods should not be considered safe yet for:
   - generic event-handler hierarchies
   - interface-driven handler patterns
   - any inheritance graph where `FindUsages` does not already show the expected override/implementation set

### Repair Plan

1. Reproduce with focused tests.
   - Add dedicated test fixtures for:
     - abstract base method -> multiple overrides
     - interface method -> multiple implementations
     - generic abstract handler base -> multiple concrete handlers
     - the same patterns with Razor consumers where relevant

2. Add a shared symbol-expansion helper for inheritance and implementation closure.
   - Start from the selected symbol.
   - Expand to:
     - source definition
     - overridden base member
     - all overriding members
     - implemented interface members
     - all interface implementations
   - Dedupe by `SymbolEqualityComparer.Default`.

3. Update `FindUsages` to search the expanded symbol set.
   - Aggregate declarations and references across the full symbol closure.
   - Keep existing path/line deduplication.
   - Preserve Razor source mapping behavior.

4. Verify rename semantics before changing production rename behavior.
   - Add tests that rename:
     - a base abstract method
     - an interface method
     - a generic handler base method
   - Confirm whether `Renamer.RenameSymbolAsync(...)` alone already updates the full hierarchy when given the right canonical symbol.

5. If rename is still incomplete, harden `RenameSymbol`.
   - Either choose a better canonical symbol before calling Roslyn rename,
   - or add grouped rename handling for the expanded symbol closure with conflict checks.
   - Re-run Razor projected rename collection using the expanded symbol set as needed.

6. Add a temporary safety guard if needed.
   - If tests confirm known-incorrect rename scenarios remain, reject those rename entry points with a clear error instead of silently under-renaming.

### Done When

1. `FindUsages` returns the expected override/implementation set for the scenarios above.
2. `RenameSymbol` renames the full hierarchy correctly or explicitly blocks unsupported cases.
3. Razor-backed rename paths stay consistent with the C# rename result.
