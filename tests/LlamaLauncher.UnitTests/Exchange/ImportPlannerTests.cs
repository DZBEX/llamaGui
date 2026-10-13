using LlamaLauncher.Application.Exchange;
using LlamaLauncher.Domain;
using Xunit;

namespace LlamaLauncher.UnitTests.Exchange;

public sealed class ImportPlannerTests
{
    private static ProfileExchangeEntry CreateEntry(string name, string? modelPath = null) => new()
    {
        Name = name,
        Model = modelPath is null ? null : new ModelReferenceSnapshot
        {
            Name = name + "-model",
            FilePath = modelPath,
            PathIsRelative = false,
        },
        Options = new ServerLaunchOptions { Port = 8080, Host = "127.0.0.1" },
    };

    private static ProfileExchangeDocument CreateDocument(params ProfileExchangeEntry[] entries) => new()
    {
        SchemaVersion = ProfileExchangeDocument.CurrentSchemaVersion,
        ExportedUtc = DateTimeOffset.UtcNow,
        Profiles = entries.ToList(),
    };

    [Fact]
    public void FutureSchemaVersion_IsRejected()
    {
        var document = CreateDocument(CreateEntry("A"));
        document.SchemaVersion = ProfileExchangeDocument.CurrentSchemaVersion + 1;

        var plan = ImportPlanner.CreatePlan(document, "C:\\", new Dictionary<string, Guid>());

        Assert.False(plan.SchemaSupported);
        Assert.Contains("高于当前支持", plan.SchemaError);
        Assert.False(plan.HasApplicableItems);
    }

    [Fact]
    public void MissingSchemaVersion_IsRejected()
    {
        var document = CreateDocument(CreateEntry("A"));
        document.SchemaVersion = 0;

        var plan = ImportPlanner.CreatePlan(document, "C:\\", new Dictionary<string, Guid>());

        Assert.False(plan.SchemaSupported);
    }

    [Fact]
    public void NewProfile_IsAdd()
    {
        var document = CreateDocument(CreateEntry("新配置"));
        var plan = ImportPlanner.CreatePlan(document, "C:\\", new Dictionary<string, Guid>());

        Assert.Single(plan.Items);
        Assert.Equal(ImportItemAction.Add, plan.Items[0].Action);
        Assert.True(plan.HasApplicableItems);
    }

    [Fact]
    public void ExistingName_IsOverwrite()
    {
        var existingId = Guid.NewGuid();
        var document = CreateDocument(CreateEntry("已存在"));
        var plan = ImportPlanner.CreatePlan(
            document,
            "C:\\",
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["已存在"] = existingId });

        Assert.Equal(ImportItemAction.Overwrite, plan.Items[0].Action);
        Assert.Equal(existingId, plan.Items[0].ExistingProfileId);
    }

    [Fact]
    public void DuplicateNamesInDocument_SecondIsInvalid()
    {
        var document = CreateDocument(CreateEntry("重复"), CreateEntry("重复"));
        var plan = ImportPlanner.CreatePlan(document, "C:\\", new Dictionary<string, Guid>());

        Assert.Equal(ImportItemAction.Add, plan.Items[0].Action);
        Assert.Equal(ImportItemAction.Invalid, plan.Items[1].Action);
        Assert.True(plan.Items[1].HasErrors);
    }

    [Fact]
    public void EmptyName_IsInvalid()
    {
        var document = CreateDocument(CreateEntry("   "));
        var plan = ImportPlanner.CreatePlan(document, "C:\\", new Dictionary<string, Guid>());

        Assert.Equal(ImportItemAction.Invalid, plan.Items[0].Action);
    }

    [Fact]
    public void MissingModelFile_AddsWarningButKeepsItemUsable()
    {
        var document = CreateDocument(CreateEntry("A", "C:\\not-exists\\model.gguf"));
        var plan = ImportPlanner.CreatePlan(document, "C:\\", new Dictionary<string, Guid>());

        var item = plan.Items[0];
        Assert.False(item.ModelFileExists);
        Assert.False(item.HasErrors);
        Assert.Contains(item.Issues, i => !i.IsBlocking && i.Field == "model");
    }

    [Fact]
    public void RelativeModelPath_ResolvedAgainstImportFileDirectory()
    {
        var entry = CreateEntry("A", "models\\a.gguf");
        entry.Model!.PathIsRelative = true;
        var document = CreateDocument(entry);

        var plan = ImportPlanner.CreatePlan(document, "C:\\configs", new Dictionary<string, Guid>());

        Assert.Equal(Path.GetFullPath("C:\\configs\\models\\a.gguf"), plan.Items[0].ResolvedModelPath);
    }

    [Fact]
    public void UnknownFields_ProduceWarnings()
    {
        var document = CreateDocument(CreateEntry("A"));
        document.UnknownFields = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["futureFeature"] = System.Text.Json.JsonDocument.Parse("true").RootElement,
        };

        var plan = ImportPlanner.CreatePlan(document, "C:\\", new Dictionary<string, Guid>());

        Assert.Contains(plan.Warnings, w => w.Contains("futureFeature"));
    }

    [Fact]
    public void InvalidPortInEntry_IsBlocking()
    {
        var entry = CreateEntry("A");
        entry.Options.Port = 99999;
        var document = CreateDocument(entry);

        var plan = ImportPlanner.CreatePlan(document, "C:\\", new Dictionary<string, Guid>());

        Assert.True(plan.Items[0].HasErrors);
        Assert.False(plan.HasApplicableItems);
    }

    [Fact]
    public void EmptyDocument_ProducesWarning()
    {
        var plan = ImportPlanner.CreatePlan(new ProfileExchangeDocument(), "C:\\", new Dictionary<string, Guid>());
        Assert.Contains(plan.Warnings, w => w.Contains("不包含任何配置档案"));
    }
}
