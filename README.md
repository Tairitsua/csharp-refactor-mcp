# RefactorMCP

RefactorMCP is a Model Context Protocol server that exposes Roslyn-based refactoring tools for C#.

The repo targets `.NET 10`, uses [`RefactorMCP.slnx`](./RefactorMCP.slnx) as its primary solution file, and still accepts legacy `.sln` paths by resolving them to the matching `.slnx` file at runtime.

## Usage

Run the console application directly or host it as an MCP server:

```bash
dotnet run --project RefactorMCP.ConsoleApp
```

Build and run the Docker image as a stdio MCP server:

```bash
docker build -t refactor-mcp:local .
docker run --rm -i \
  -v /mnt/d:/mnt/d \
  -v /mnt/c/Users/<user>/.nuget/packages:/root/.nuget/packages \
  refactor-mcp:local
```

If your projects use private feeds or a custom `NuGet.Config`, also mount the host NuGet config directory:

```bash
docker run --rm -i \
  -v /mnt/d:/mnt/d \
  -v /mnt/c/Users/<user>/.nuget/packages:/root/.nuget/packages \
  -v /mnt/c/Users/<user>/AppData/Roaming/NuGet:/root/.nuget/NuGet:ro \
  refactor-mcp:local
```

Run a simple JSON-mode smoke test inside the container:

```bash
docker run --rm -i refactor-mcp:local --json Version '{}'
docker run --rm -i \
  -v /mnt/d:/mnt/d \
  -v /mnt/c/Users/<user>/.nuget/packages:/root/.nuget/packages \
  refactor-mcp:local --json LoadSolution '{"solutionPath":"/mnt/d/Code/refactor-mcp/RefactorMCP.slnx"}'
```

Register the container with Codex as a local stdio MCP server:

```bash
codex mcp add refactor-mcp -- \
  docker run --rm -i \
  -v /mnt/d:/mnt/d \
  -v /mnt/c/Users/<user>/.nuget/packages:/root/.nuget/packages \
  refactor-mcp:local
```

Build or test the repo from the root:

```bash
dotnet build
dotnet test
```

For usage examples see [EXAMPLES.md](./EXAMPLES.md).

## Available Refactorings

- **Rename Symbol** – rename a symbol across the solution using Roslyn. Phase 1 includes source-mapped Razor rename support for `RenameSymbol` across `.cs`, `.razor`, `.razor.cs`, `.cshtml`, and `.cshtml.cs` when the edit can be mapped safely back to the original Razor source. When a top-level type rename comes from a single-type `.cs` file whose filename matches the original type name, the file is renamed to match the new type name as well. `_Imports.razor` and `_ViewImports.cshtml` are explicit unsupported entry points, and unsupported Razor spans fail safely instead of applying partial edits.
- **Extract Method** – create a new method from selected code and replace the original with a call (expression-bodied methods are not supported).
- **Introduce Field/Parameter/Variable** – turn expressions into new members; fails if a field already exists.
- **Convert to Static** – make instance methods static using parameters or an instance argument.
- **Move Static Method** – relocate a static method and keep a wrapper in the original class.
- **Move Instance Method** – move one or more instance methods to another class and delegate from the source. If a moved method no longer accesses instance members, it is made static automatically. Provide a `methodNames` list along with optional `constructor-injections` and `parameter-injections` to control dependencies.
- **Move Multiple Methods (instance)** – move several methods and keep them as instance members of the target class. The source instance is injected via the constructor when required.
- **Move Multiple Methods (static)** – move multiple methods and convert them to static, adding a `this` parameter.
- **Make Static Then Move** – convert an instance method to static and relocate it to another class in one step.
- **Move Type to Separate File** – move a top-level type into its own file named after the type.
- **Make Field Readonly** – move initialization into constructors and mark the field readonly.
- **Transform Setter to Init** – convert property setters to init-only and initialize in constructors.
- **Constructor Injection** – convert method parameters to constructor-injected fields or properties.
- **Safe Delete** – remove fields or variables only after dependency checks.
- **Extract Class** – create a new class from selected members and compose it with the original.
- **Inline Method** – replace calls with the method body and delete the original.
- **Extract Decorator** – create a decorator class that delegates to an existing method.
- **Create Adapter** – generate an adapter class wrapping an existing method.
- **Add Observer** – introduce an event and raise it from a method.
- **Use Interface** – change a method parameter type to one of its implemented interfaces.
- **List Tools** – display all available refactoring tools as kebab-case names.

Metrics and summaries are also available via the `metrics://` and `summary://` resource schemes.

## Contributing

* Run `dotnet test` to ensure all tests pass.
* Format the code with `dotnet format` before opening a pull request.

## License

Licensed under the [Mozilla Public License 2.0](https://www.mozilla.org/MPL/2.0/).
