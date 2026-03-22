using Microsoft.CodeAnalysis.Diagnostics;
using ModelContextProtocol;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RefactorMCP.Tests.Tools;

public class FindUsagesToolTests : RefactorMCP.Tests.TestBase
{
    [Fact]
    public async Task FindUsages_FieldAcrossFiles_ReturnsDeclarationsAndReferences()
    {
        const string symbolFileSource = """
public class Counter
{
    public int Value;

    public int Read() => Value;
}
""";

        const string consumerFileSource = """
public class Consumer
{
    public int Use(Counter counter) => counter.Value;
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var symbolFilePath = Path.Combine(TestOutputPath, "Counter.cs");
        var consumerFilePath = Path.Combine(TestOutputPath, "Consumer.cs");
        await TestUtilities.CreateTestFile(symbolFilePath, symbolFileSource);
        await TestUtilities.CreateTestFile(consumerFilePath, consumerFileSource);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, symbolFilePath);
        RefactoringHelpers.AddDocumentToProject(project, consumerFilePath);

        var result = await FindUsagesTool.FindUsages(
            SolutionPath,
            symbolFilePath,
            "Value",
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        Assert.Equal("Value", result.SymbolName);
        Assert.Equal("Field", result.SymbolKind);
        Assert.Single(result.Declarations);
        Assert.Equal(2, result.TotalReferenceCount);
        Assert.Equal(2, result.ReturnedReferenceCount);
        Assert.False(result.IsTruncated);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, symbolFilePath));
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, consumerFilePath));
        Assert.All(result.References, location => Assert.False(string.IsNullOrWhiteSpace(location.LineText)));
    }

    [Fact]
    public async Task FindUsages_MaxResults_TruncatesReferences()
    {
        const string source = """
public class Counter
{
    public int Value;

    public int Read() => Value + Value + Value;
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var symbolFilePath = Path.Combine(TestOutputPath, "Counter.cs");
        await TestUtilities.CreateTestFile(symbolFilePath, source);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, symbolFilePath);

        var result = await FindUsagesTool.FindUsages(
            SolutionPath,
            symbolFilePath,
            "Value",
            maxResults: 1,
            cancellationToken: CancellationToken.None);

        Assert.Equal(3, result.TotalReferenceCount);
        Assert.Equal(1, result.ReturnedReferenceCount);
        Assert.True(result.IsTruncated);
        Assert.Single(result.References);
    }

    [Fact]
    public async Task FindUsages_UnknownSymbol_ThrowsMcpException()
    {
        const string source = """
public class Counter
{
    public int Value;
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var symbolFilePath = Path.Combine(TestOutputPath, "Counter.cs");
        await TestUtilities.CreateTestFile(symbolFilePath, source);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, symbolFilePath);

        await Assert.ThrowsAsync<McpException>(() => FindUsagesTool.FindUsages(
            SolutionPath,
            symbolFilePath,
            "MissingValue",
            cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task FindUsages_UnresolvedAnalyzerReference_IgnoresBrokenAnalyzer()
    {
        const string symbolFileSource = """
public class Counter
{
    public int Value;
}
""";

        const string consumerFileSource = """
public class Consumer
{
    public int Use(Counter counter) => counter.Value;
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var symbolFilePath = Path.Combine(TestOutputPath, "Counter.cs");
        var consumerFilePath = Path.Combine(TestOutputPath, "Consumer.cs");
        await TestUtilities.CreateTestFile(symbolFilePath, symbolFileSource);
        await TestUtilities.CreateTestFile(consumerFilePath, consumerFileSource);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, symbolFilePath);
        RefactoringHelpers.AddDocumentToProject(project, consumerFilePath);

        solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        project = solution.Projects.First();
        var solutionWithBrokenAnalyzer = solution.WithProjectAnalyzerReferences(
            project.Id,
            project.AnalyzerReferences.Append(new UnresolvedAnalyzerReference("missing-analyzer.dll")));
        RefactoringHelpers.UpdateSolutionCache(solutionWithBrokenAnalyzer);

        var result = await FindUsagesTool.FindUsages(
            SolutionPath,
            symbolFilePath,
            "Value",
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        Assert.Equal("Value", result.SymbolName);
        Assert.Equal(1, result.TotalReferenceCount);
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, consumerFilePath));
    }

    [Fact]
    public async Task FindUsages_RazorMethodReference_MapsToRazorSourceFile()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "BlazorRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BlazorRenameFixture");
        var consumerFile = Path.Combine(projectRoot, "Support", "CSharpConsumer.cs");
        var razorFile = Path.Combine(projectRoot, "Components", "Pages", "RenameDemo.razor");

        var result = await FindUsagesTool.FindUsages(
            solutionPath,
            consumerFile,
            "Read",
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        Assert.Equal("Read", result.SymbolName);
        Assert.Single(result.Declarations);
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, razorFile));
        Assert.DoesNotContain(
            result.References,
            location => location.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("@CSharpConsumer.Read()", result.References[0].LineText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_AbstractBaseMethod_ReturnsOverridesAndCallSites()
    {
        const string baseCode = """
public abstract class WorkerBase
{
    public abstract void Process();

    public void Run() => Process();
}
""";

        const string firstDerivedCode = """
public sealed class FirstWorker : WorkerBase
{
    public override void Process()
    {
    }
}
""";

        const string secondDerivedCode = """
public sealed class SecondWorker : WorkerBase
{
    public override void Process()
    {
    }
}
""";

        const string consumerCode = """
public static class WorkerConsumer
{
    public static void Use(WorkerBase worker) => worker.Process();
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var baseFile = Path.Combine(TestOutputPath, "WorkerBase.cs");
        var firstDerivedFile = Path.Combine(TestOutputPath, "FirstWorker.cs");
        var secondDerivedFile = Path.Combine(TestOutputPath, "SecondWorker.cs");
        var consumerFile = Path.Combine(TestOutputPath, "WorkerConsumer.cs");

        await TestUtilities.CreateTestFile(baseFile, baseCode);
        await TestUtilities.CreateTestFile(firstDerivedFile, firstDerivedCode);
        await TestUtilities.CreateTestFile(secondDerivedFile, secondDerivedCode);
        await TestUtilities.CreateTestFile(consumerFile, consumerCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, baseFile);
        RefactoringHelpers.AddDocumentToProject(project, firstDerivedFile);
        RefactoringHelpers.AddDocumentToProject(project, secondDerivedFile);
        RefactoringHelpers.AddDocumentToProject(project, consumerFile);

        var result = await FindUsagesTool.FindUsages(
            SolutionPath,
            baseFile,
            "Process",
            line: 3,
            column: 26,
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        Assert.Equal("Process", result.SymbolName);
        Assert.Equal(3, result.Declarations.Count);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, baseFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, firstDerivedFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, secondDerivedFile));
        Assert.Equal(2, result.TotalReferenceCount);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, baseFile));
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, consumerFile));
    }

    [Fact]
    public async Task FindUsages_InterfaceMethod_ReturnsImplementationsAndCallSites()
    {
        const string interfaceCode = """
public interface IWorker
{
    void Process();
}
""";

        const string firstImplementationCode = """
public sealed class WorkerA : IWorker
{
    public void Process()
    {
    }
}
""";

        const string secondImplementationCode = """
public sealed class WorkerB : IWorker
{
    public void Process()
    {
    }
}
""";

        const string consumerCode = """
public static class WorkerRunner
{
    public static void Run(IWorker worker) => worker.Process();
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var interfaceFile = Path.Combine(TestOutputPath, "IWorker.cs");
        var firstImplementationFile = Path.Combine(TestOutputPath, "WorkerA.cs");
        var secondImplementationFile = Path.Combine(TestOutputPath, "WorkerB.cs");
        var consumerFile = Path.Combine(TestOutputPath, "WorkerRunner.cs");

        await TestUtilities.CreateTestFile(interfaceFile, interfaceCode);
        await TestUtilities.CreateTestFile(firstImplementationFile, firstImplementationCode);
        await TestUtilities.CreateTestFile(secondImplementationFile, secondImplementationCode);
        await TestUtilities.CreateTestFile(consumerFile, consumerCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, interfaceFile);
        RefactoringHelpers.AddDocumentToProject(project, firstImplementationFile);
        RefactoringHelpers.AddDocumentToProject(project, secondImplementationFile);
        RefactoringHelpers.AddDocumentToProject(project, consumerFile);

        var result = await FindUsagesTool.FindUsages(
            SolutionPath,
            interfaceFile,
            "Process",
            line: 3,
            column: 10,
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        Assert.Equal("Process", result.SymbolName);
        Assert.Equal(3, result.Declarations.Count);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, interfaceFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, firstImplementationFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, secondImplementationFile));
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, consumerFile));
    }

    [Fact]
    public async Task FindUsages_GenericAbstractMethod_ReturnsOverridesAndCallSites()
    {
        const string eventsCode = """
public sealed record OrderCreated;
public sealed record UserCreated;
""";

        const string handlerBaseCode = """
using System.Threading.Tasks;

public abstract class HandlerBase<TEvent>
{
    public abstract Task HandleEventAsync(TEvent @event);
}
""";

        const string orderHandlerCode = """
using System.Threading.Tasks;

public sealed class OrderHandler : HandlerBase<OrderCreated>
{
    public override Task HandleEventAsync(OrderCreated @event) => Task.CompletedTask;
}
""";

        const string userHandlerCode = """
using System.Threading.Tasks;

public sealed class UserHandler : HandlerBase<UserCreated>
{
    public override Task HandleEventAsync(UserCreated @event) => Task.CompletedTask;
}
""";

        const string consumerCode = """
using System.Threading.Tasks;

public static class Dispatcher
{
    public static Task Dispatch(HandlerBase<OrderCreated> handler, OrderCreated @event)
        => handler.HandleEventAsync(@event);
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var eventsFile = Path.Combine(TestOutputPath, "Events.cs");
        var handlerBaseFile = Path.Combine(TestOutputPath, "HandlerBase.cs");
        var orderHandlerFile = Path.Combine(TestOutputPath, "OrderHandler.cs");
        var userHandlerFile = Path.Combine(TestOutputPath, "UserHandler.cs");
        var consumerFile = Path.Combine(TestOutputPath, "Dispatcher.cs");

        await TestUtilities.CreateTestFile(eventsFile, eventsCode);
        await TestUtilities.CreateTestFile(handlerBaseFile, handlerBaseCode);
        await TestUtilities.CreateTestFile(orderHandlerFile, orderHandlerCode);
        await TestUtilities.CreateTestFile(userHandlerFile, userHandlerCode);
        await TestUtilities.CreateTestFile(consumerFile, consumerCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, eventsFile);
        RefactoringHelpers.AddDocumentToProject(project, handlerBaseFile);
        RefactoringHelpers.AddDocumentToProject(project, orderHandlerFile);
        RefactoringHelpers.AddDocumentToProject(project, userHandlerFile);
        RefactoringHelpers.AddDocumentToProject(project, consumerFile);

        var result = await FindUsagesTool.FindUsages(
            SolutionPath,
            handlerBaseFile,
            "HandleEventAsync",
            line: 5,
            column: 26,
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        Assert.Equal("HandleEventAsync", result.SymbolName);
        Assert.Equal(3, result.Declarations.Count);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, handlerBaseFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, orderHandlerFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, userHandlerFile));
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, consumerFile));
        Assert.DoesNotContain(
            result.References,
            location => location.LineText.Contains("see cref", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FindUsages_GenericHandlerWithoutPosition_PrefersDeclarationOverCref()
    {
        const string contractCode = """
using System.Threading.Tasks;

public interface IDistributedEventHandler<in TEvent>
{
    Task HandleEventAsync(TEvent eventData);
}

/// <summary>
/// Inherit and implement <see cref="HandleEventAsync"/>.
/// </summary>
public abstract class DomainEventHandler<THandler, TEvent> : IDistributedEventHandler<TEvent>
    where THandler : DomainEventHandler<THandler, TEvent>
{
    public abstract Task HandleEventAsync(TEvent eventData);
}
""";

        const string eventCode = """
public sealed record OrderCreated;
public sealed record UserCreated;
""";

        const string orderHandlerCode = """
using System.Threading.Tasks;

public sealed class OrderHandler : DomainEventHandler<OrderHandler, OrderCreated>
{
    public override Task HandleEventAsync(OrderCreated eventData) => Task.CompletedTask;
}
""";

        const string userHandlerCode = """
using System.Threading.Tasks;

public sealed class UserHandler : DomainEventHandler<UserHandler, UserCreated>
{
    public override Task HandleEventAsync(UserCreated eventData) => Task.CompletedTask;
}
""";

        const string consumerCode = """
using System.Threading.Tasks;

public static class HandlerDispatcher
{
    public static Task Dispatch(IDistributedEventHandler<OrderCreated> handler, OrderCreated eventData) =>
        handler.HandleEventAsync(eventData);
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var contractFile = Path.Combine(TestOutputPath, "DomainEventHandler.cs");
        var eventFile = Path.Combine(TestOutputPath, "Events.cs");
        var orderHandlerFile = Path.Combine(TestOutputPath, "OrderHandler.cs");
        var userHandlerFile = Path.Combine(TestOutputPath, "UserHandler.cs");
        var consumerFile = Path.Combine(TestOutputPath, "HandlerDispatcher.cs");

        await TestUtilities.CreateTestFile(contractFile, contractCode);
        await TestUtilities.CreateTestFile(eventFile, eventCode);
        await TestUtilities.CreateTestFile(orderHandlerFile, orderHandlerCode);
        await TestUtilities.CreateTestFile(userHandlerFile, userHandlerCode);
        await TestUtilities.CreateTestFile(consumerFile, consumerCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, contractFile);
        RefactoringHelpers.AddDocumentToProject(project, eventFile);
        RefactoringHelpers.AddDocumentToProject(project, orderHandlerFile);
        RefactoringHelpers.AddDocumentToProject(project, userHandlerFile);
        RefactoringHelpers.AddDocumentToProject(project, consumerFile);

        var result = await FindUsagesTool.FindUsages(
            SolutionPath,
            contractFile,
            "HandleEventAsync",
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        Assert.Equal("HandleEventAsync", result.SymbolName);
        Assert.Equal(4, result.Declarations.Count);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, contractFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, orderHandlerFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, userHandlerFile));
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, consumerFile));
    }

    [Fact]
    public async Task FindUsages_OpenGenericInterfaceMethod_ReturnsClosedGenericImplementations()
    {
        const string contractCode = """
using System.Threading.Tasks;

public interface IEventHandler<in TEvent>
{
    Task HandleEventAsync(TEvent eventData);
}

public interface IDistributedEventHandler<in TEvent> : IEventHandler<TEvent>
{
    Task HandleEventAsync(TEvent eventData);
}

public sealed record EntityUpdated<T>(T Value);
public sealed record EntityCreated<T>(T Value);
public sealed record EntityDeleted<T>(T Value);
public sealed record SampleDto(string Value);
""";

        const string implementationCode = """
using System.Threading.Tasks;

public sealed class SyncHandler<TDto> :
    IDistributedEventHandler<EntityUpdated<TDto>>,
    IDistributedEventHandler<EntityCreated<TDto>>,
    IDistributedEventHandler<EntityDeleted<TDto>>
{
    public Task HandleEventAsync(EntityUpdated<TDto> eventData) => Task.CompletedTask;

    public Task HandleEventAsync(EntityCreated<TDto> eventData) => Task.CompletedTask;

    public Task HandleEventAsync(EntityDeleted<TDto> eventData) => Task.CompletedTask;
}
""";

        const string consumerCode = """
using System.Threading.Tasks;

public static class SyncRunner
{
    public static Task Run(
        IDistributedEventHandler<EntityUpdated<SampleDto>> handler,
        EntityUpdated<SampleDto> eventData)
        => handler.HandleEventAsync(eventData);
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var contractFile = Path.Combine(TestOutputPath, "DistributedHandlers.cs");
        var implementationFile = Path.Combine(TestOutputPath, "SyncHandler.cs");
        var consumerFile = Path.Combine(TestOutputPath, "SyncRunner.cs");

        await TestUtilities.CreateTestFile(contractFile, contractCode);
        await TestUtilities.CreateTestFile(implementationFile, implementationCode);
        await TestUtilities.CreateTestFile(consumerFile, consumerCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, contractFile);
        RefactoringHelpers.AddDocumentToProject(project, implementationFile);
        RefactoringHelpers.AddDocumentToProject(project, consumerFile);

        var result = await FindUsagesTool.FindUsages(
            SolutionPath,
            contractFile,
            "HandleEventAsync",
            line: 9,
            column: 10,
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        Assert.Equal("HandleEventAsync", result.SymbolName);
        Assert.Equal(5, result.Declarations.Count);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, contractFile));
        Assert.Equal(
            3,
            result.Declarations.Count(location => RefactoringHelpers.PathEquals(location.FilePath, implementationFile)));
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, consumerFile));
    }

    [Fact]
    public async Task FindUsages_OpenGenericInterfaceMethod_WithGenericTypeParameterCallSite_ReturnsReferences()
    {
        const string contractCode = """
using System.Threading.Tasks;

public interface IHandler<in TEvent>
{
    Task HandleAsync(TEvent eventData);
}
""";

        const string implementationCode = """
using System.Threading.Tasks;

public abstract class BaseHandler<THandler, TEvent> : IHandler<TEvent>
    where THandler : BaseHandler<THandler, TEvent>
{
    public abstract Task HandleAsync(TEvent eventData);
}

public sealed class ConcreteHandler<TEvent> : BaseHandler<ConcreteHandler<TEvent>, TEvent>
{
    public override Task HandleAsync(TEvent eventData) => Task.CompletedTask;
}
""";

        const string consumerCode = """
using System.Threading.Tasks;

public static class GenericInvoker<TEvent>
{
    public static Task RunAsync(IHandler<TEvent> handler, TEvent eventData) =>
        handler.HandleAsync(eventData);
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var contractFile = Path.Combine(TestOutputPath, "IHandler.cs");
        var implementationFile = Path.Combine(TestOutputPath, "BaseHandler.cs");
        var consumerFile = Path.Combine(TestOutputPath, "GenericInvoker.cs");

        await TestUtilities.CreateTestFile(contractFile, contractCode);
        await TestUtilities.CreateTestFile(implementationFile, implementationCode);
        await TestUtilities.CreateTestFile(consumerFile, consumerCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, contractFile);
        RefactoringHelpers.AddDocumentToProject(project, implementationFile);
        RefactoringHelpers.AddDocumentToProject(project, consumerFile);

        var result = await FindUsagesTool.FindUsages(
            SolutionPath,
            implementationFile,
            "HandleAsync",
            line: 6,
            column: 26,
            maxResults: 20,
            cancellationToken: CancellationToken.None);

        Assert.Equal("HandleAsync", result.SymbolName);
        Assert.Equal(3, result.Declarations.Count);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, contractFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, implementationFile) && location.Line == 6);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, implementationFile) && location.Line == 11);
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, consumerFile));
    }

    [Fact]
    public async Task FindUsages_AmbiguousMethodWithoutPosition_ThrowsMcpException()
    {
        const string interfacesCode = """
using System.Threading.Tasks;

public interface IDistributedHandler<TEvent>
{
    Task HandleAsync(TEvent eventData);
}

public interface ILocalHandler<TEvent>
{
    Task HandleAsync(TEvent eventData);
}
""";

        const string handlerBasesCode = """
using System.Threading.Tasks;

public abstract class DistributedHandler<THandler, TEvent> : IDistributedHandler<TEvent>
    where THandler : DistributedHandler<THandler, TEvent>
{
    /// <summary>
    /// Implement <see cref="HandleAsync"/>.
    /// </summary>
    public abstract Task HandleAsync(TEvent eventData);
}

public abstract class LocalHandler<THandler, TEvent> : ILocalHandler<TEvent>
    where THandler : LocalHandler<THandler, TEvent>
{
    /// <summary>
    /// Implement <see cref="HandleAsync"/>.
    /// </summary>
    public abstract Task HandleAsync(TEvent eventData);
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var interfacesFile = Path.Combine(TestOutputPath, "IHandlers.cs");
        var handlerBasesFile = Path.Combine(TestOutputPath, "HandlerBases.cs");

        await TestUtilities.CreateTestFile(interfacesFile, interfacesCode);
        await TestUtilities.CreateTestFile(handlerBasesFile, handlerBasesCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, interfacesFile);
        RefactoringHelpers.AddDocumentToProject(project, handlerBasesFile);

        var exception = await Assert.ThrowsAsync<McpException>(() => FindUsagesTool.FindUsages(
            SolutionPath,
            handlerBasesFile,
            "HandleAsync",
            maxResults: 20,
            cancellationToken: CancellationToken.None));

        Assert.Contains("Supply line and column to disambiguate", exception.Message);
    }

    [Fact]
    public async Task FindUsages_AmbiguousMethodWithPosition_ReturnsOnlySelectedHierarchy()
    {
        const string interfacesCode = """
using System.Threading.Tasks;

public interface IDistributedHandler<TEvent>
{
    Task HandleAsync(TEvent eventData);
}

public interface ILocalHandler<TEvent>
{
    Task HandleAsync(TEvent eventData);
}
""";

        const string handlerBasesCode = """
using System.Threading.Tasks;

public abstract class DistributedHandler<THandler, TEvent> : IDistributedHandler<TEvent>
    where THandler : DistributedHandler<THandler, TEvent>
{
    /// <summary>
    /// Implement <see cref="HandleAsync"/>.
    /// </summary>
    public abstract Task HandleAsync(TEvent eventData);
}

public abstract class LocalHandler<THandler, TEvent> : ILocalHandler<TEvent>
    where THandler : LocalHandler<THandler, TEvent>
{
    /// <summary>
    /// Implement <see cref="HandleAsync"/>.
    /// </summary>
    public abstract Task HandleAsync(TEvent eventData);
}
""";

        const string implementationsCode = """
using System.Threading.Tasks;

public sealed class DistributedOrderHandler : DistributedHandler<DistributedOrderHandler, string>
{
    public override Task HandleAsync(string eventData) => Task.CompletedTask;
}

public sealed class LocalOrderHandler : LocalHandler<LocalOrderHandler, string>
{
    public override Task HandleAsync(string eventData) => Task.CompletedTask;
}
""";

        const string consumersCode = """
using System.Threading.Tasks;

public static class DistributedRunner
{
    public static Task RunAsync(IDistributedHandler<string> handler, string eventData) =>
        handler.HandleAsync(eventData);
}

public static class LocalRunner
{
    public static Task RunAsync(ILocalHandler<string> handler, string eventData) =>
        handler.HandleAsync(eventData);
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var interfacesFile = Path.Combine(TestOutputPath, "IHandlers.cs");
        var handlerBasesFile = Path.Combine(TestOutputPath, "HandlerBases.cs");
        var implementationsFile = Path.Combine(TestOutputPath, "Implementations.cs");
        var consumersFile = Path.Combine(TestOutputPath, "Consumers.cs");

        await TestUtilities.CreateTestFile(interfacesFile, interfacesCode);
        await TestUtilities.CreateTestFile(handlerBasesFile, handlerBasesCode);
        await TestUtilities.CreateTestFile(implementationsFile, implementationsCode);
        await TestUtilities.CreateTestFile(consumersFile, consumersCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, interfacesFile);
        RefactoringHelpers.AddDocumentToProject(project, handlerBasesFile);
        RefactoringHelpers.AddDocumentToProject(project, implementationsFile);
        RefactoringHelpers.AddDocumentToProject(project, consumersFile);

        var result = await FindUsagesTool.FindUsages(
            SolutionPath,
            handlerBasesFile,
            "HandleAsync",
            line: 9,
            column: 26,
            maxResults: 20,
            cancellationToken: CancellationToken.None);

        Assert.Equal("HandleAsync", result.SymbolName);
        Assert.Equal(3, result.Declarations.Count);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, interfacesFile) && location.Line == 5);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, handlerBasesFile) && location.Line == 9);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, implementationsFile) && location.Line == 5);
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, consumersFile) && location.Line == 6);
    }

    [Fact]
    public async Task FindUsages_CrossProjectGenericHandlerFamily_ReturnsInterfaceDeclarationsAndCallSites()
    {
        var fixture = await TestUtilities.PrepareCrossProjectGenericHandlerFixtureAsync(TestOutputPath);
        await LoadSolutionTool.LoadSolution(fixture.SolutionPath, null, CancellationToken.None);

        var result = await FindUsagesTool.FindUsages(
            fixture.SolutionPath,
            fixture.BaseFile,
            "HandleEventAsync",
            line: 12,
            column: 26,
            maxResults: 20,
            cancellationToken: CancellationToken.None);

        Assert.Equal("HandleEventAsync", result.SymbolName);
        Assert.Equal(4, result.Declarations.Count);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.InterfaceFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.BaseFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.OrderHandlerFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.UserHandlerFile));
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.ConsumerFile));
        Assert.DoesNotContain(
            result.References,
            location => location.LineText.Contains("see cref", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FindUsages_CrossProjectGenericExecutorCall_ExcludesUnrelatedOverloadAndFindsMemberAccessReference()
    {
        var fixture = await TestUtilities.PrepareCrossProjectGenericExecutorFixtureAsync(TestOutputPath);
        await LoadSolutionTool.LoadSolution(fixture.SolutionPath, null, CancellationToken.None);

        var result = await FindUsagesTool.FindUsages(
            fixture.SolutionPath,
            fixture.BaseFile,
            "HandleEventAsync",
            line: 12,
            column: 26,
            maxResults: 20,
            cancellationToken: CancellationToken.None);

        var declarationSummary = string.Join(
            " | ",
            result.Declarations.Select(location =>
                $"{Path.GetFileName(location.FilePath)}:{location.Line}:{location.Column}:{location.LineText}"));

        Assert.Equal("HandleEventAsync", result.SymbolName);
        Assert.True(result.Declarations.Count == 3, declarationSummary);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.InterfaceFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.BaseFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.HandlerFile) && location.Line == 7);
        Assert.DoesNotContain(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.HandlerFile) && location.Line == 9);
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.ExecutorFile) && location.Line == 15);
    }

    [Fact]
    public async Task FindUsages_CrossProjectGenericExecutorCall_FromInterfaceRoot_ExcludesUnrelatedOverloadAndFindsMemberAccessReference()
    {
        var fixture = await TestUtilities.PrepareCrossProjectGenericExecutorFixtureAsync(TestOutputPath);
        await LoadSolutionTool.LoadSolution(fixture.SolutionPath, null, CancellationToken.None);

        var result = await FindUsagesTool.FindUsages(
            fixture.SolutionPath,
            fixture.InterfaceFile,
            "HandleEventAsync",
            line: 7,
            column: 10,
            maxResults: 20,
            cancellationToken: CancellationToken.None);

        var declarationSummary = string.Join(
            " | ",
            result.Declarations.Select(location =>
                $"{Path.GetFileName(location.FilePath)}:{location.Line}:{location.Column}:{location.LineText}"));

        Assert.Equal("HandleEventAsync", result.SymbolName);
        Assert.True(result.Declarations.Count == 3, declarationSummary);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.InterfaceFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.BaseFile));
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.HandlerFile) && location.Line == 7);
        Assert.DoesNotContain(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.HandlerFile) && location.Line == 9);
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.ExecutorFile) && location.Line == 15);
    }

    [Fact]
    public async Task FindUsages_CrossProjectGenericExecutorPropertyCall_FindsMemberAccessReference()
    {
        var fixture = await TestUtilities.PrepareCrossProjectGenericExecutorPropertyFixtureAsync(TestOutputPath);
        await LoadSolutionTool.LoadSolution(fixture.SolutionPath, null, CancellationToken.None);

        var result = await FindUsagesTool.FindUsages(
            fixture.SolutionPath,
            fixture.BaseFile,
            "HandleEventAsync",
            line: 12,
            column: 26,
            maxResults: 20,
            cancellationToken: CancellationToken.None);

        var declarationSummary = string.Join(
            " | ",
            result.Declarations.Select(location =>
                $"{Path.GetFileName(location.FilePath)}:{location.Line}:{location.Column}:{location.LineText}"));

        Assert.Equal("HandleEventAsync", result.SymbolName);
        Assert.True(result.Declarations.Count == 3, declarationSummary);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.InterfaceFile) && location.Line == 11);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.BaseFile) && location.Line == 12);
        Assert.Contains(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.HandlerFile) && location.Line == 7);
        Assert.DoesNotContain(result.Declarations, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.HandlerFile) && location.Line == 9);
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.ExecutorFile) && location.Line == 23);
    }

    [Fact]
    public async Task FindUsages_CrossProjectGenericExecutorPropertyCall_WithUnresolvedAsReceiver_FindsMemberAccessReference()
    {
        var fixture = await TestUtilities.PrepareCrossProjectGenericExecutorPropertyFixtureAsync(TestOutputPath);
        await TestUtilities.CreateTestFile(fixture.ExecutorFile, """
using System;
using System.Threading.Tasks;
using Contracts;

namespace Handlers;

public interface IEventHandlerMethodExecutor
{
    Func<IMoEventHandler, object, Task> ExecutorAsync { get; }
}

public sealed class DistributedEventHandlerMethodExecutor<TEvent> : IEventHandlerMethodExecutor
{
    public Func<IMoEventHandler, object, Task> ExecutorAsync => (target, parameter) =>
    {
        if (parameter is TEvent eventData)
        {
            return target.As<IMoDistributedEventHandler<TEvent>>().HandleEventAsync(eventData);
        }

        return Task.CompletedTask;
    };
}
""");

        await LoadSolutionTool.LoadSolution(fixture.SolutionPath, null, CancellationToken.None);

        var result = await FindUsagesTool.FindUsages(
            fixture.SolutionPath,
            fixture.BaseFile,
            "HandleEventAsync",
            line: 12,
            column: 26,
            maxResults: 20,
            cancellationToken: CancellationToken.None);

        Assert.Equal("HandleEventAsync", result.SymbolName);
        Assert.Equal(3, result.Declarations.Count);
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.ExecutorFile) && location.Line == 18);
    }

    [Fact]
    public async Task FindUsages_CrossProjectGenericExecutorPropertyCallSite_WithUnresolvedAsReceiver_ResolvesByLineAndColumn()
    {
        var fixture = await TestUtilities.PrepareCrossProjectGenericExecutorPropertyFixtureAsync(TestOutputPath);
        await TestUtilities.CreateTestFile(fixture.ExecutorFile, """
using System;
using System.Threading.Tasks;
using Contracts;

namespace Handlers;

public interface IEventHandlerMethodExecutor
{
    Func<IMoEventHandler, object, Task> ExecutorAsync { get; }
}

public sealed class DistributedEventHandlerMethodExecutor<TEvent> : IEventHandlerMethodExecutor
{
    public Func<IMoEventHandler, object, Task> ExecutorAsync => (target, parameter) =>
    {
        if (parameter is TEvent eventData)
        {
            return target.As<IMoDistributedEventHandler<TEvent>>().HandleEventAsync(eventData);
        }

        return Task.CompletedTask;
    };
}
""");

        await LoadSolutionTool.LoadSolution(fixture.SolutionPath, null, CancellationToken.None);

        var result = await FindUsagesTool.FindUsages(
            fixture.SolutionPath,
            fixture.ExecutorFile,
            "HandleEventAsync",
            line: 18,
            column: 68,
            maxResults: 20,
            cancellationToken: CancellationToken.None);

        Assert.Equal("HandleEventAsync", result.SymbolName);
        Assert.Equal(3, result.Declarations.Count);
        Assert.Single(result.References);
        Assert.Contains(result.References, location => RefactoringHelpers.PathEquals(location.FilePath, fixture.ExecutorFile) && location.Line == 18);
    }
}
