using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;

namespace RefactorMCP.Tests.Tools;

public class RazorRenameSymbolToolTests : RefactorMCP.Tests.TestBase
{
    [Fact]
    public async Task LoadSolution_RazorFixture_LoadsSuccessfully()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "BlazorRenameFixture"),
            TestOutputPath);

        var result = await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        Assert.Contains("BlazorRenameFixture", result);
    }

    [Fact]
    public async Task RenameSymbol_CSharpDeclaration_UpdatesRazorReference()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "BlazorRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BlazorRenameFixture");
        var targetFile = Path.Combine(projectRoot, "Support", "RenameTarget.cs");
        var razorFile = Path.Combine(projectRoot, "Components", "Pages", "RenameDemo.razor");
        var consumerFile = Path.Combine(projectRoot, "Support", "CSharpConsumer.cs");

        var result = await RenameSymbolTool.RenameSymbol(
            solutionPath,
            targetFile,
            "CurrentValue",
            "DisplayValue");

        Assert.Contains("Successfully renamed", result);
        Assert.Contains("DisplayValue", await File.ReadAllTextAsync(targetFile));
        Assert.Contains("DisplayValue", await File.ReadAllTextAsync(razorFile));
        Assert.Contains("DisplayValue", await File.ReadAllTextAsync(consumerFile));
    }

    [Fact]
    public async Task RenameSymbol_RazorUsage_UpdatesCSharpDeclarationAndOtherReferences()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "BlazorRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BlazorRenameFixture");
        var razorFile = Path.Combine(projectRoot, "Components", "Pages", "RenameDemo.razor");
        var targetFile = Path.Combine(projectRoot, "Support", "RenameTarget.cs");
        var consumerFile = Path.Combine(projectRoot, "Support", "CSharpConsumer.cs");

        var result = await RenameSymbolTool.RenameSymbol(
            solutionPath,
            razorFile,
            "CurrentValue",
            "RenamedValue");

        Assert.Contains("Successfully renamed", result);
        Assert.Contains("RenamedValue", await File.ReadAllTextAsync(razorFile));
        Assert.Contains("RenamedValue", await File.ReadAllTextAsync(targetFile));
        Assert.Contains("RenamedValue", await File.ReadAllTextAsync(consumerFile));
    }

    [Fact]
    public async Task RenameSymbol_RazorCodeBehind_UpdatesPairedRazor()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "BlazorRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BlazorRenameFixture");
        var codeBehindFile = Path.Combine(projectRoot, "Components", "Pages", "CodeBehindDemo.razor.cs");
        var razorFile = Path.Combine(projectRoot, "Components", "Pages", "CodeBehindDemo.razor");

        var result = await RenameSymbolTool.RenameSymbol(
            solutionPath,
            codeBehindFile,
            "CurrentCount",
            "VisibleCount");

        Assert.Contains("Successfully renamed", result);
        Assert.Contains("VisibleCount", await File.ReadAllTextAsync(codeBehindFile));
        Assert.Contains("VisibleCount", await File.ReadAllTextAsync(razorFile));
    }

    [Fact]
    public async Task RenameSymbol_ImportsEntryPoint_FailsAndLeavesFilesUnchanged()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "BlazorRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BlazorRenameFixture");
        var importsFile = Path.Combine(projectRoot, "Components", "_Imports.razor");
        var originalText = await File.ReadAllTextAsync(importsFile);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            RenameSymbolTool.RenameSymbol(
                solutionPath,
                importsFile,
                "BlazorRenameFixture",
                "RenamedFixture"));

        Assert.Contains("Phase 1", exception.Message);
        Assert.Equal(originalText, await File.ReadAllTextAsync(importsFile));
    }

    [Fact]
    public async Task RenameSymbol_CSharpViewCodeBehind_UpdatesCshtmlReference()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "RazorPagesRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorPagesRenameFixture");
        var codeBehindFile = Path.Combine(projectRoot, "Pages", "Index.cshtml.cs");
        var viewFile = Path.Combine(projectRoot, "Pages", "Index.cshtml");
        var consumerFile = Path.Combine(projectRoot, "Support", "PageConsumer.cs");

        var result = await RenameSymbolTool.RenameSymbol(
            solutionPath,
            codeBehindFile,
            "CurrentValue",
            "DisplayValue");

        Assert.Contains("Successfully renamed", result);
        Assert.Contains("DisplayValue", await File.ReadAllTextAsync(codeBehindFile));
        Assert.Contains("DisplayValue", await File.ReadAllTextAsync(viewFile));
        Assert.Contains("DisplayValue", await File.ReadAllTextAsync(consumerFile));
    }

    [Fact]
    public async Task RenameSymbol_CshtmlUsage_UpdatesCodeBehindAndCSharpReferences()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "RazorPagesRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorPagesRenameFixture");
        var viewFile = Path.Combine(projectRoot, "Pages", "Index.cshtml");
        var codeBehindFile = Path.Combine(projectRoot, "Pages", "Index.cshtml.cs");
        var consumerFile = Path.Combine(projectRoot, "Support", "PageConsumer.cs");

        var result = await RenameSymbolTool.RenameSymbol(
            solutionPath,
            viewFile,
            "CurrentValue",
            "VisibleValue");

        Assert.Contains("Successfully renamed", result);
        Assert.Contains("VisibleValue", await File.ReadAllTextAsync(viewFile));
        Assert.Contains("VisibleValue", await File.ReadAllTextAsync(codeBehindFile));
        Assert.Contains("VisibleValue", await File.ReadAllTextAsync(consumerFile));
    }

    [Fact]
    public async Task RenameSymbol_ViewImportsEntryPoint_FailsAndLeavesFileUnchanged()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "RazorPagesRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorPagesRenameFixture");
        var importsFile = Path.Combine(projectRoot, "Pages", "_ViewImports.cshtml");
        var originalText = await File.ReadAllTextAsync(importsFile);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            RenameSymbolTool.RenameSymbol(
                solutionPath,
                importsFile,
                "RazorPagesRenameFixture",
                "RenamedFixture"));

        Assert.Contains("Phase 1", exception.Message);
        Assert.Equal(originalText, await File.ReadAllTextAsync(importsFile));
    }

    [Fact]
    public async Task RenameSymbol_UnsupportedRazorSpan_FailsExplicitlyAndLeavesFilesUnchanged()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "BlazorRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BlazorRenameFixture");
        var razorFile = Path.Combine(projectRoot, "Components", "Pages", "RenameDemo.razor");
        var originalText = await File.ReadAllTextAsync(razorFile);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            RenameSymbolTool.RenameSymbol(
                solutionPath,
                razorFile,
                "Rename",
                "Retitle",
                line: 3,
                column: 5));

        Assert.Contains("selected Razor span", exception.Message);
        Assert.Equal(originalText, await File.ReadAllTextAsync(razorFile));
    }

    [Fact]
    public async Task RenameSymbol_CSharpTypeDeclaration_UpdatesRazorTypeUsage()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "BlazorRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BlazorRenameFixture");
        var originalTypeFile = Path.Combine(projectRoot, "Support", "ProbeWidget.cs");
        var renamedTypeFile = Path.Combine(projectRoot, "Support", "ProbeDisplayWidget.cs");
        var razorFile = Path.Combine(projectRoot, "Components", "Pages", "TypeRenameDemo.razor");

        var result = await RenameSymbolTool.RenameSymbol(
            solutionPath,
            originalTypeFile,
            "ProbeWidget",
            "ProbeDisplayWidget");

        Assert.Contains("Successfully renamed", result);
        Assert.False(File.Exists(originalTypeFile));
        Assert.Contains("ProbeDisplayWidget", await File.ReadAllTextAsync(renamedTypeFile));
        Assert.Contains("ProbeDisplayWidget", await File.ReadAllTextAsync(razorFile));
        Assert.DoesNotContain("private ProbeWidget Widget", await File.ReadAllTextAsync(razorFile));
    }

    [Fact]
    public async Task RenameSymbol_RazorTypeUsage_UpdatesCSharpTypeDeclaration()
    {
        var solutionPath = await TestUtilities.PrepareIsolatedFixtureAsync(
            Path.Combine("Razor", "BlazorRenameFixture"),
            TestOutputPath);
        await LoadSolutionTool.LoadSolution(solutionPath, null, CancellationToken.None);

        var projectRoot = Path.Combine(Path.GetDirectoryName(solutionPath)!, "BlazorRenameFixture");
        var razorFile = Path.Combine(projectRoot, "Components", "Pages", "TypeRenameDemo.razor");
        var originalTypeFile = Path.Combine(projectRoot, "Support", "ProbeWidget.cs");
        var renamedTypeFile = Path.Combine(projectRoot, "Support", "ProbeFinalWidget.cs");

        var result = await RenameSymbolTool.RenameSymbol(
            solutionPath,
            razorFile,
            "ProbeWidget",
            "ProbeFinalWidget");

        Assert.Contains("Successfully renamed", result);
        Assert.Contains("ProbeFinalWidget", await File.ReadAllTextAsync(razorFile));
        Assert.False(File.Exists(originalTypeFile));
        Assert.Contains("ProbeFinalWidget", await File.ReadAllTextAsync(renamedTypeFile));
        Assert.DoesNotContain("private ProbeWidget Widget", await File.ReadAllTextAsync(razorFile));
    }
}
