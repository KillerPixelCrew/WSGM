using System.Text;
using WSGM.DeviceLab.Core;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Fixtures;
using WSGM.DeviceLab.Core.Schemas;

namespace WSGM.DeviceLab.Tests;

/// <summary>Compatibility gates for imported versioned Device Lab JSON.</summary>
public class SchemaCompatibilityTests
{
    [Fact]
    public async Task RecipeDraftV0_MigratesToGoldenV1AndDiscardsImportedAuthority()
    {
        await using FileStream input = OpenGolden("recipe-v0.json");

        DeviceLabSchemaReadResult<ObserveOnlyRecipe> result =
            await DeviceLabSchemaReader.ReadRecipeAsync(input);

        Assert.True(result.Succeeded);
        Assert.True(result.Migrated);
        Assert.Equal(0, result.SourceVersion);
        Assert.Equal(RecipeAuthority.InertEvidence, result.Value!.Authority);
        Assert.Equal(
            Normalize(await ReadGoldenAsync("recipe-v1.json")),
            Normalize(DeviceLabJson.Serialize(result.Value)));
    }

    [Fact]
    public async Task FixtureDraftV0_MigratesToGoldenV1AndForcesSimulatorReplay()
    {
        await using FileStream input = OpenGolden("fixture-v0.json");

        DeviceLabSchemaReadResult<FixtureManifest> result =
            await DeviceLabSchemaReader.ReadFixtureAsync(input);

        Assert.True(result.Succeeded);
        Assert.True(result.Migrated);
        Assert.Equal(0, result.SourceVersion);
        Assert.Equal(FixtureReplayPolicy.SimulatorOnly, result.Value!.ReplayPolicy);
        Assert.Equal(
            Normalize(await ReadGoldenAsync("fixture-v1.json")),
            Normalize(DeviceLabJson.Serialize(result.Value)));
    }

    [Fact]
    public async Task CurrentGolden_RoundTripsToByteStableCanonicalJson()
    {
        await using FileStream input = OpenGolden("recipe-v1.json");

        DeviceLabSchemaReadResult<ObserveOnlyRecipe> result =
            await DeviceLabSchemaReader.ReadRecipeAsync(input);

        Assert.True(result.Succeeded);
        Assert.False(result.Migrated);
        Assert.Equal(CaptureSchema.RecipeVersion, result.SourceVersion);
        string first = DeviceLabJson.Serialize(result.Value!);
        await using MemoryStream secondInput = JsonStream(first);
        DeviceLabSchemaReadResult<ObserveOnlyRecipe> second =
            await DeviceLabSchemaReader.ReadRecipeAsync(secondInput);
        Assert.True(second.Succeeded);
        Assert.Equal(first, DeviceLabJson.Serialize(second.Value!));
    }

    [Fact]
    public async Task EveryTypedReader_RejectsAnUnknownFutureVersionBeforeDeserialization()
    {
        const string future = "{\"schemaVersion\":2147483647}";

        Assert.Equal(
            DeviceLabSchemaReadFailure.UnsupportedVersion,
            (await DeviceLabSchemaReader.ReadShareableManifestAsync(JsonStream(future))).Failure);
        Assert.Equal(
            DeviceLabSchemaReadFailure.UnsupportedVersion,
            (await DeviceLabSchemaReader.ReadRecipeAsync(JsonStream(future))).Failure);
        Assert.Equal(
            DeviceLabSchemaReadFailure.UnsupportedVersion,
            (await DeviceLabSchemaReader.ReadFixtureAsync(JsonStream(future))).Failure);
        Assert.Equal(
            DeviceLabSchemaReadFailure.UnsupportedVersion,
            (await DeviceLabSchemaReader.ReadScaffoldInputAsync(JsonStream(future))).Failure);
        Assert.Equal(
            DeviceLabSchemaReadFailure.UnsupportedVersion,
            (await DeviceLabSchemaReader.ReadScaffoldOutputAsync(JsonStream(future))).Failure);
    }

    [Fact]
    public async Task MalformedMissingAndDeepJson_ReturnClosedFailures()
    {
        DeviceLabSchemaReadResult<ObserveOnlyRecipe> malformed =
            await DeviceLabSchemaReader.ReadRecipeAsync(JsonStream("{\"schemaVersion\":1,"));
        DeviceLabSchemaReadResult<ObserveOnlyRecipe> missing =
            await DeviceLabSchemaReader.ReadRecipeAsync(JsonStream("{}"));
        string deep = "{\"schemaVersion\":1,\"extra\":"
            + new string('[', DeviceLabSchemaReader.MaximumJsonDepth + 1)
            + "0"
            + new string(']', DeviceLabSchemaReader.MaximumJsonDepth + 1)
            + "}";
        DeviceLabSchemaReadResult<ObserveOnlyRecipe> excessiveDepth =
            await DeviceLabSchemaReader.ReadRecipeAsync(JsonStream(deep));

        Assert.Equal(DeviceLabSchemaReadFailure.Malformed, malformed.Failure);
        Assert.Equal(DeviceLabSchemaReadFailure.Malformed, missing.Failure);
        Assert.Equal(DeviceLabSchemaReadFailure.Malformed, excessiveDepth.Failure);
    }

    [Fact]
    public async Task ExplicitNullForRequiredData_IsRejectedInsteadOfReachingAValidator()
    {
        const string nullIdentifier = """
            {
              "schemaVersion": 1,
              "recipeId": null,
              "displayName": "Reference",
              "authority": "InertEvidence",
              "steps": []
            }
            """;
        const string nullSteps = """
            {
              "schemaVersion": 1,
              "recipeId": "reference",
              "displayName": "Reference",
              "authority": "InertEvidence",
              "steps": null
            }
            """;

        DeviceLabSchemaReadResult<ObserveOnlyRecipe> identifierResult =
            await DeviceLabSchemaReader.ReadRecipeAsync(JsonStream(nullIdentifier));
        DeviceLabSchemaReadResult<ObserveOnlyRecipe> stepsResult =
            await DeviceLabSchemaReader.ReadRecipeAsync(JsonStream(nullSteps));

        Assert.Equal(DeviceLabSchemaReadFailure.Malformed, identifierResult.Failure);
        Assert.Equal(DeviceLabSchemaReadFailure.Malformed, stepsResult.Failure);
        Assert.Null(identifierResult.Value);
        Assert.Null(stepsResult.Value);
    }

    [Fact]
    public async Task OversizedNonSeekableInput_IsRejectedAtTheHardByteLimit()
    {
        await using NonSeekableRepeatingStream input = new(DeviceLabSchemaReader.MaximumJsonBytes + 1);

        DeviceLabSchemaReadResult<ObserveOnlyRecipe> result =
            await DeviceLabSchemaReader.ReadRecipeAsync(input);

        Assert.Equal(DeviceLabSchemaReadFailure.Oversized, result.Failure);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ConflictingLegacyAndCurrentProperties_FailsMigrationWithoutGuessing()
    {
        const string conflicting = """
            {
              "schemaVersion": 0,
              "recipeId": "reference",
              "name": "Legacy",
              "displayName": "Current",
              "steps": []
            }
            """;

        DeviceLabSchemaReadResult<ObserveOnlyRecipe> result =
            await DeviceLabSchemaReader.ReadRecipeAsync(JsonStream(conflicting));

        Assert.Equal(DeviceLabSchemaReadFailure.MigrationFailed, result.Failure);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task StructurallyValidButUnsafeArtifact_ReturnsSemanticErrorsWithoutAValue()
    {
        string unsafeFixture = (await ReadGoldenAsync("fixture-v1.json"))
            .Replace("\"inputs\": []", """
                "inputs": [
                  {
                    "path": "../private/event.bin",
                    "mediaType": "application/octet-stream",
                    "length": 1,
                    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                  }
                ]
                """, StringComparison.Ordinal);

        DeviceLabSchemaReadResult<FixtureManifest> result =
            await DeviceLabSchemaReader.ReadFixtureAsync(JsonStream(unsafeFixture));

        Assert.Equal(DeviceLabSchemaReadFailure.Invalid, result.Failure);
        Assert.Null(result.Value);
        Assert.Contains(result.ValidationErrors, error =>
            error.Message.Contains("input/", StringComparison.Ordinal));
    }

    private static FileStream OpenGolden(string filename) => File.OpenRead(GoldenPath(filename));

    private static Task<string> ReadGoldenAsync(string filename) =>
        File.ReadAllTextAsync(GoldenPath(filename));

    private static string GoldenPath(string filename) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Schema", filename);

    private static MemoryStream JsonStream(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string Normalize(string json) => json.ReplaceLineEndings("\n").TrimEnd();

    private sealed class NonSeekableRepeatingStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Min(count, _remaining);
            buffer.AsSpan(offset, read).Fill((byte)' ');
            _remaining -= read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..read].Fill((byte)' ');
            _remaining -= read;
            return ValueTask.FromResult(read);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
