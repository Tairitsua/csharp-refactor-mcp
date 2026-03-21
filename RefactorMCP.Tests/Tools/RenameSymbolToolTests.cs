using Microsoft.CodeAnalysis.Diagnostics;
using System.IO;
using System.Threading;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ModelContextProtocol;
using Xunit;

namespace RefactorMCP.Tests.Tools;

public class RenameSymbolToolTests : RefactorMCP.Tests.TestBase
{
    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");

    [Fact]
    public async Task RenameSymbol_Field_RenamesReferences()
    {
        const string initialCode = """
using System.Collections.Generic;
using System.Linq;

public class Sample
{
    private List<int> numbers = new();
    public int Sum() => numbers.Sum();
}
""";

        const string expectedCode = """
using System.Collections.Generic;
using System.Linq;

public class Sample
{
    private List<int> values = new();
    public int Sum() => values.Sum();
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "Rename.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "numbers",
            "values");

        Assert.Contains("Successfully renamed", result);
        var fileContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_UnresolvedAnalyzerReference_IgnoresBrokenAnalyzer()
    {
        const string initialCode = """
using System.Collections.Generic;
using System.Linq;

public class Sample
{
    private List<int> numbers = new();
    public int Sum() => numbers.Sum();
}
""";

        const string expectedCode = """
using System.Collections.Generic;
using System.Linq;

public class Sample
{
    private List<int> values = new();
    public int Sum() => values.Sum();
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "RenameWithBrokenAnalyzer.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        project = solution.Projects.First();
        var solutionWithBrokenAnalyzer = solution.WithProjectAnalyzerReferences(
            project.Id,
            project.AnalyzerReferences.Append(new UnresolvedAnalyzerReference("missing-analyzer.dll")));
        RefactoringHelpers.UpdateSolutionCache(solutionWithBrokenAnalyzer);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "numbers",
            "values");

        Assert.Contains("Successfully renamed", result);
        var fileContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_InvalidName_ThrowsMcpException()
    {
        const string initialCode = """
using System.Collections.Generic;
using System.Linq;

public class Sample
{
    private List<int> numbers = new();
    public int Sum() => numbers.Sum();
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "RenameInvalid.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        await Assert.ThrowsAsync<McpException>(() =>
            RenameSymbolTool.RenameSymbol(
                SolutionPath,
                testFile,
                "missing",
                "newName"));
    }

    [Fact]
    public async Task RenameSymbol_Class_RenamesClassAndConstructor()
    {
        const string initialCode = """
public class OldName
{
    public OldName() { }
    public void DoWork() { }
}

public class Consumer
{
    public void Use()
    {
        var instance = new OldName();
    }
}
""";

        const string expectedCode = """
public class NewName
{
    public NewName() { }
    public void DoWork() { }
}

public class Consumer
{
    public void Use()
    {
        var instance = new NewName();
    }
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "RenameClass.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "OldName",
            "NewName");

        Assert.Contains("Successfully renamed", result);
        var fileContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_SingleTypeClassFile_RenamesFileToMatchClass()
    {
        const string initialCode = """
public class OldName
{
    public void Run() { }
}
""";

        const string expectedCode = """
public class NewName
{
    public void Run() { }
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var originalFile = Path.Combine(TestOutputPath, "OldName.cs");
        var renamedFile = Path.Combine(TestOutputPath, "NewName.cs");
        await TestUtilities.CreateTestFile(originalFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, originalFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            originalFile,
            "OldName",
            "NewName");

        Assert.Contains("Successfully renamed", result);
        Assert.False(File.Exists(originalFile));
        Assert.True(File.Exists(renamedFile));
        var fileContent = await File.ReadAllTextAsync(renamedFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_MultipleTopLevelTypes_DoesNotRenameFile()
    {
        const string initialCode = """
public class OldName
{
}

public class Helper
{
}
""";

        const string expectedCode = """
public class NewName
{
}

public class Helper
{
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var originalFile = Path.Combine(TestOutputPath, "OldName.cs");
        var renamedFile = Path.Combine(TestOutputPath, "NewName.cs");
        await TestUtilities.CreateTestFile(originalFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, originalFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            originalFile,
            "OldName",
            "NewName");

        Assert.Contains("Successfully renamed", result);
        Assert.True(File.Exists(originalFile));
        Assert.False(File.Exists(renamedFile));
        var fileContent = await File.ReadAllTextAsync(originalFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_SingleTypeClassFile_FailsWhenTargetFileExists()
    {
        const string sourceCode = """
public class OldName
{
}
""";

        const string conflictingCode = """
public class Existing
{
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var originalFile = Path.Combine(TestOutputPath, "OldName.cs");
        var conflictingFile = Path.Combine(TestOutputPath, "NewName.cs");
        await TestUtilities.CreateTestFile(originalFile, sourceCode);
        await TestUtilities.CreateTestFile(conflictingFile, conflictingCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, originalFile);
        solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, conflictingFile);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            RenameSymbolTool.RenameSymbol(
                SolutionPath,
                originalFile,
                "OldName",
                "NewName"));

        Assert.Contains("already exists", exception.Message);
        Assert.True(File.Exists(originalFile));
        Assert.True(File.Exists(conflictingFile));
        Assert.Contains("public class OldName", await File.ReadAllTextAsync(originalFile));
        Assert.Contains("public class Existing", await File.ReadAllTextAsync(conflictingFile));
    }

    [Fact]
    public async Task RenameSymbol_Method_RenamesMethodAndCalls()
    {
        const string initialCode = """
public class Sample
{
    public void OldMethod() { }

    public void Caller()
    {
        OldMethod();
        this.OldMethod();
    }
}
""";

        const string expectedCode = """
public class Sample
{
    public void NewMethod() { }

    public void Caller()
    {
        NewMethod();
        this.NewMethod();
    }
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "RenameMethod.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "OldMethod",
            "NewMethod");

        Assert.Contains("Successfully renamed", result);
        var fileContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_AbstractBaseMethod_RenamesOverridesAndCallSites()
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

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            baseFile,
            "Process",
            "Execute",
            line: 3,
            column: 26);

        Assert.Contains("Successfully renamed", result);
        Assert.DoesNotContain("Process()", await File.ReadAllTextAsync(baseFile));
        Assert.Contains("Execute()", await File.ReadAllTextAsync(baseFile));
        Assert.Contains("override void Execute()", await File.ReadAllTextAsync(firstDerivedFile));
        Assert.Contains("override void Execute()", await File.ReadAllTextAsync(secondDerivedFile));
        Assert.Contains("worker.Execute()", await File.ReadAllTextAsync(consumerFile));
    }

    [Fact]
    public async Task RenameSymbol_InterfaceMethod_RenamesImplementationsAndCallSites()
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

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            interfaceFile,
            "Process",
            "Execute",
            line: 3,
            column: 10);

        Assert.Contains("Successfully renamed", result);
        Assert.DoesNotContain("Process()", await File.ReadAllTextAsync(interfaceFile));
        Assert.Contains("Execute()", await File.ReadAllTextAsync(interfaceFile));
        Assert.Contains("void Execute()", await File.ReadAllTextAsync(firstImplementationFile));
        Assert.Contains("void Execute()", await File.ReadAllTextAsync(secondImplementationFile));
        Assert.Contains("worker.Execute()", await File.ReadAllTextAsync(consumerFile));
    }

    [Fact]
    public async Task RenameSymbol_GenericAbstractMethod_RenamesOverridesAndCallSites()
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

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            handlerBaseFile,
            "HandleEventAsync",
            "HandleAsync",
            line: 5,
            column: 26);

        Assert.Contains("Successfully renamed", result);
        Assert.Contains("Task HandleAsync", await File.ReadAllTextAsync(handlerBaseFile));
        Assert.Contains("override Task HandleAsync", await File.ReadAllTextAsync(orderHandlerFile));
        Assert.Contains("override Task HandleAsync", await File.ReadAllTextAsync(userHandlerFile));
        Assert.Contains("handler.HandleAsync(@event)", await File.ReadAllTextAsync(consumerFile));
    }

    [Fact]
    public async Task RenameSymbol_GenericHandlerWithoutPosition_PrefersDeclarationOverCref()
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

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var contractFile = Path.Combine(TestOutputPath, "DomainEventHandler.cs");
        var eventFile = Path.Combine(TestOutputPath, "Events.cs");
        var orderHandlerFile = Path.Combine(TestOutputPath, "OrderHandler.cs");
        var userHandlerFile = Path.Combine(TestOutputPath, "UserHandler.cs");

        await TestUtilities.CreateTestFile(contractFile, contractCode);
        await TestUtilities.CreateTestFile(eventFile, eventCode);
        await TestUtilities.CreateTestFile(orderHandlerFile, orderHandlerCode);
        await TestUtilities.CreateTestFile(userHandlerFile, userHandlerCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, contractFile);
        RefactoringHelpers.AddDocumentToProject(project, eventFile);
        RefactoringHelpers.AddDocumentToProject(project, orderHandlerFile);
        RefactoringHelpers.AddDocumentToProject(project, userHandlerFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            contractFile,
            "HandleEventAsync",
            "ExecuteAsync");

        Assert.Contains("Successfully renamed", result);
        Assert.Contains("Task ExecuteAsync", await File.ReadAllTextAsync(contractFile));
        Assert.Contains("override Task ExecuteAsync", await File.ReadAllTextAsync(orderHandlerFile));
        Assert.Contains("override Task ExecuteAsync", await File.ReadAllTextAsync(userHandlerFile));
    }

    [Fact]
    public async Task RenameSymbol_OpenGenericInterfaceMethod_RenamesClosedGenericImplementations()
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

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            contractFile,
            "HandleEventAsync",
            "ExecuteAsync",
            line: 9,
            column: 10);

        Assert.Contains("Successfully renamed", result);
        Assert.Contains("Task ExecuteAsync(TEvent eventData);", await File.ReadAllTextAsync(contractFile));
        var implementationContent = await File.ReadAllTextAsync(implementationFile);
        Assert.Equal(3, implementationContent.Split("ExecuteAsync(").Length - 1);
        Assert.Contains("handler.ExecuteAsync(eventData)", await File.ReadAllTextAsync(consumerFile));
    }

    [Fact]
    public async Task RenameSymbol_Property_RenamesPropertyAndReferences()
    {
        const string initialCode = """
public class Sample
{
    public string OldProperty { get; set; }

    public void Use()
    {
        OldProperty = "test";
        var x = OldProperty;
    }
}
""";

        const string expectedCode = """
public class Sample
{
    public string NewProperty { get; set; }

    public void Use()
    {
        NewProperty = "test";
        var x = NewProperty;
    }
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "RenameProperty.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "OldProperty",
            "NewProperty");

        Assert.Contains("Successfully renamed", result);
        var fileContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_Parameter_RenamesParameterAndUsages()
    {
        const string initialCode = """
public class Sample
{
    public int Calculate(int oldParam)
    {
        return oldParam * 2;
    }
}
""";

        const string expectedCode = """
public class Sample
{
    public int Calculate(int newParam)
    {
        return newParam * 2;
    }
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "RenameParameter.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "oldParam",
            "newParam");

        Assert.Contains("Successfully renamed", result);
        var fileContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_LocalVariable_RenamesVariableAndUsages()
    {
        const string initialCode = """
public class Sample
{
    public void Method()
    {
        var oldVar = 10;
        var result = oldVar + 5;
    }
}
""";

        const string expectedCode = """
public class Sample
{
    public void Method()
    {
        var newVar = 10;
        var result = newVar + 5;
    }
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "RenameLocalVar.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "oldVar",
            "newVar");

        Assert.Contains("Successfully renamed", result);
        var fileContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_Interface_RenamesInterfaceAndImplementations()
    {
        const string initialCode = """
public interface IOldInterface
{
    void DoWork();
}

public class Implementation : IOldInterface
{
    public void DoWork() { }
}
""";

        const string expectedCode = """
public interface INewInterface
{
    void DoWork();
}

public class Implementation : INewInterface
{
    public void DoWork() { }
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "RenameInterface.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "IOldInterface",
            "INewInterface");

        Assert.Contains("Successfully renamed", result);
        var fileContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_GenericHandlerBaseMethod_RenamesInterfaceAndOverrides()
    {
        const string interfaceCode = """
using System.Threading.Tasks;

public interface IHandler<TEvent>
{
    Task HandleAsync(TEvent eventData);
}
""";

        const string baseHandlerCode = """
using System.Threading.Tasks;

public abstract class BaseHandler<THandler, TEvent> : IHandler<TEvent>
    where THandler : BaseHandler<THandler, TEvent>
{
    /// <summary>
    /// Implement <see cref="HandleAsync"/>.
    /// </summary>
    public abstract Task HandleAsync(TEvent eventData);
}
""";

        const string derivedHandlersCode = """
using System.Threading.Tasks;

public sealed class FirstHandler : BaseHandler<FirstHandler, string>
{
    public override Task HandleAsync(string eventData) => Task.CompletedTask;
}

public sealed class SecondHandler : BaseHandler<SecondHandler, string>
{
    public override Task HandleAsync(string eventData) => Task.CompletedTask;
}
""";

        const string consumerCode = """
using System.Threading.Tasks;

public static class HandlerRunner
{
    public static Task RunAsync(IHandler<string> handler, string eventData) =>
        handler.HandleAsync(eventData);
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var interfaceFile = Path.Combine(TestOutputPath, "IHandler.cs");
        var baseHandlerFile = Path.Combine(TestOutputPath, "BaseHandler.cs");
        var derivedHandlersFile = Path.Combine(TestOutputPath, "DerivedHandlers.cs");
        var consumerFile = Path.Combine(TestOutputPath, "HandlerRunner.cs");

        await TestUtilities.CreateTestFile(interfaceFile, interfaceCode);
        await TestUtilities.CreateTestFile(baseHandlerFile, baseHandlerCode);
        await TestUtilities.CreateTestFile(derivedHandlersFile, derivedHandlersCode);
        await TestUtilities.CreateTestFile(consumerFile, consumerCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, interfaceFile);
        RefactoringHelpers.AddDocumentToProject(project, baseHandlerFile);
        RefactoringHelpers.AddDocumentToProject(project, derivedHandlersFile);
        RefactoringHelpers.AddDocumentToProject(project, consumerFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            baseHandlerFile,
            "HandleAsync",
            "ProcessAsync");

        Assert.Contains("Successfully renamed", result);

        var interfaceContent = await File.ReadAllTextAsync(interfaceFile);
        var baseHandlerContent = await File.ReadAllTextAsync(baseHandlerFile);
        var derivedHandlersContent = await File.ReadAllTextAsync(derivedHandlersFile);
        var consumerContent = await File.ReadAllTextAsync(consumerFile);

        Assert.Contains("Task ProcessAsync(TEvent eventData);", interfaceContent);
        Assert.DoesNotContain("HandleAsync", interfaceContent);

        Assert.Contains("Implement <see cref=\"ProcessAsync\"/>.", baseHandlerContent);
        Assert.Contains("public abstract Task ProcessAsync(TEvent eventData);", baseHandlerContent);
        Assert.DoesNotContain("HandleAsync", baseHandlerContent);

        Assert.Equal(2, CountOccurrences(derivedHandlersContent, "override Task ProcessAsync"));
        Assert.DoesNotContain("override Task HandleAsync", derivedHandlersContent);

        Assert.Contains("handler.ProcessAsync(eventData)", consumerContent);
        Assert.DoesNotContain("HandleAsync", consumerContent);
    }

    [Fact]
    public async Task RenameSymbol_AmbiguousMethodWithoutPosition_ThrowsMcpException()
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

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);

        var interfacesFile = Path.Combine(TestOutputPath, "IHandlers.cs");
        var handlerBasesFile = Path.Combine(TestOutputPath, "HandlerBases.cs");
        var implementationsFile = Path.Combine(TestOutputPath, "Implementations.cs");

        await TestUtilities.CreateTestFile(interfacesFile, interfacesCode);
        await TestUtilities.CreateTestFile(handlerBasesFile, handlerBasesCode);
        await TestUtilities.CreateTestFile(implementationsFile, implementationsCode);

        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, interfacesFile);
        RefactoringHelpers.AddDocumentToProject(project, handlerBasesFile);
        RefactoringHelpers.AddDocumentToProject(project, implementationsFile);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            RenameSymbolTool.RenameSymbol(
                SolutionPath,
                handlerBasesFile,
                "HandleAsync",
                "ProcessAsync"));

        Assert.Contains("Supply line and column to disambiguate", exception.Message);
    }

    [Fact]
    public async Task RenameSymbol_AmbiguousMethodWithPosition_RenamesOnlySelectedHierarchy()
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

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            handlerBasesFile,
            "HandleAsync",
            "ProcessAsync",
            line: 9,
            column: 26);

        Assert.Contains("Successfully renamed", result);

        var interfacesContent = await File.ReadAllTextAsync(interfacesFile);
        var handlerBasesContent = await File.ReadAllTextAsync(handlerBasesFile);
        var implementationsContent = await File.ReadAllTextAsync(implementationsFile);
        var consumersContent = await File.ReadAllTextAsync(consumersFile);

        Assert.Contains("Task ProcessAsync(TEvent eventData);", interfacesContent);
        Assert.Contains("Task HandleAsync(TEvent eventData);", interfacesContent);

        Assert.Contains("public abstract Task ProcessAsync(TEvent eventData);", handlerBasesContent);
        Assert.Contains("public abstract Task HandleAsync(TEvent eventData);", handlerBasesContent);

        Assert.Contains("override Task ProcessAsync", implementationsContent);
        Assert.Contains("override Task HandleAsync", implementationsContent);

        Assert.Contains("handler.ProcessAsync(eventData)", consumersContent);
        Assert.Contains("handler.HandleAsync(eventData)", consumersContent);
    }

    [Fact]
    public async Task RenameSymbol_WithLineAndColumn_RenamesSpecificSymbol()
    {
        const string initialCode = """
public class Sample
{
    private int value;
    public int Value => value;
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "RenameWithPosition.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        // Line 3, column 17 should be the field 'value'
        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "value",
            "internalValue",
            line: 3,
            column: 17);

        Assert.Contains("Successfully renamed", result);
        var fileContent = await File.ReadAllTextAsync(testFile);
        Assert.Contains("internalValue", fileContent);
    }

    [Fact]
    public async Task RenameSymbol_Utf8WithoutBom_DoesNotAddBom()
    {
        const string initialCode = """
public class Sample
{
    private int value;
    public int Read() => value;
}
""";

        var testFile = Path.Combine(TestOutputPath, "RenameNoBom.cs");
        await File.WriteAllTextAsync(testFile, initialCode, new UTF8Encoding(false));

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "value",
            "currentValue");

        Assert.Contains("Successfully renamed", result);

        var bytes = await File.ReadAllBytesAsync(testFile);
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public async Task RenameSymbol_Enum_RenamesEnumAndUsages()
    {
        const string initialCode = """
public enum OldStatus
{
    Active,
    Inactive
}

public class Sample
{
    public OldStatus Status { get; set; }
}
""";

        const string expectedCode = """
public enum NewStatus
{
    Active,
    Inactive
}

public class Sample
{
    public NewStatus Status { get; set; }
}
""";

        await LoadSolutionTool.LoadSolution(SolutionPath, null, CancellationToken.None);
        var testFile = Path.Combine(TestOutputPath, "RenameEnum.cs");
        await TestUtilities.CreateTestFile(testFile, initialCode);
        var solution = await RefactoringHelpers.GetOrLoadSolution(SolutionPath);
        var project = solution.Projects.First();
        RefactoringHelpers.AddDocumentToProject(project, testFile);

        var result = await RenameSymbolTool.RenameSymbol(
            SolutionPath,
            testFile,
            "OldStatus",
            "NewStatus");

        Assert.Contains("Successfully renamed", result);
        var fileContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(NormalizeLineEndings(expectedCode), NormalizeLineEndings(fileContent));
    }

    [Fact]
    public async Task RenameSymbol_CrossProjectGenericHandlerFamily_RenamesInterfaceImplementationsAndCallSites()
    {
        var fixture = await TestUtilities.PrepareCrossProjectGenericHandlerFixtureAsync(TestOutputPath);
        await LoadSolutionTool.LoadSolution(fixture.SolutionPath, null, CancellationToken.None);

        var result = await RenameSymbolTool.RenameSymbol(
            fixture.SolutionPath,
            fixture.BaseFile,
            "HandleEventAsync",
            "ExecuteAsync",
            line: 12,
            column: 26);

        Assert.Contains("Successfully renamed", result);
        Assert.Contains("Task ExecuteAsync(TEvent eventData);", await File.ReadAllTextAsync(fixture.InterfaceFile));
        Assert.Contains("Task ExecuteAsync(TEvent eventData);", await File.ReadAllTextAsync(fixture.BaseFile));
        Assert.Contains("override Task ExecuteAsync(OrderCreated eventData)", await File.ReadAllTextAsync(fixture.OrderHandlerFile));
        Assert.Contains("override Task ExecuteAsync(UserCreated eventData)", await File.ReadAllTextAsync(fixture.UserHandlerFile));
        Assert.Contains("handler.ExecuteAsync(eventData)", await File.ReadAllTextAsync(fixture.ConsumerFile));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
